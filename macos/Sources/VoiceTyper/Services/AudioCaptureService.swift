@preconcurrency import AVFoundation
import Foundation

private final class AudioConverterInputState: @unchecked Sendable {
    var hasProvidedInput = false
}

/// 纯逻辑的环形缓冲分帧器：累积样本 → 吐出定长 chunk，`drain()` 吐出剩余尾音。
/// 与 AVAudioEngine/线程无关，可脱离真实麦克风单测（R3-18 覆盖缺口之一）。
/// 调用方（`AudioCaptureService`）负责所有线程安全。
struct AudioChunker {
    let chunkSamples: Int
    private var buffer: [Float] = []

    init(chunkSamples: Int) {
        self.chunkSamples = chunkSamples
    }

    /// 累积新样本，返回本次凑满的完整 chunk（可能为空、一个或多个）。
    mutating func append(_ samples: [Float]) -> [[Float]] {
        buffer.append(contentsOf: samples)
        var chunks: [[Float]] = []
        while buffer.count >= chunkSamples {
            chunks.append(Array(buffer.prefix(chunkSamples)))
            buffer.removeFirst(chunkSamples)
        }
        return chunks
    }

    /// 取走并清空当前缓冲区（不足一个完整 chunk 的尾音）。
    mutating func drain() -> [Float] {
        defer { buffer.removeAll() }
        return buffer
    }
}

/// 流式录音服务。
///
/// 录音期间每凑满 `chunkSamples` 个 float32 样本就通过 `onChunk` 发出一帧；
/// 停止时将剩余不足一帧的尾音通过 `onTailChunk` 发出，随后调用 `onStopped`。
final class AudioCaptureService: @unchecked Sendable {
    /// 每凑满 `chunkSamples` 个 float32 样本（默认 9600 = 600ms）触发一次
    var onChunk: (([Float]) -> Void)?
    /// 停止录音时触发一次，传入剩余不足一帧的尾音（可能为空数组）
    var onTailChunk: (([Float]) -> Void)?
    /// 每次音频输入回调触发一次（约 20–60ms），传入该缓冲区的线性 RMS 电平（0…1 量级，
    /// 未做分贝归一化）。在音频线程调用，消费方负责切换线程与平滑。
    var onLevel: ((Float) -> Void)?
    /// 录音期间输入设备变化（拔麦克风、切换音频设备等）导致本次录音被迫结束时触发一次。
    /// 已采到的音频仍会通过 `onTailChunk`（本回调触发前已同步调用）正常交给当前会话
    /// 完成识别；这里只做"可见告知"，不做自动重建 converter 或自动恢复（F-15）。
    /// 在主线程触发。
    var onDeviceChanged: (() -> Void)?

    let chunkSamples: Int

    private let engine = AVAudioEngine()
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: AppConstants.targetSampleRate,
        channels: 1,
        interleaved: false
    )!

    /// `lock` 同时保护 `isRunning` 与 `chunker`：`append()`（音频线程）与 `stop()`（主线程）
    /// 若不在同一把锁下原子地"判断 isRunning + 处理缓冲区"，会出现两类竞态（R3-01）：
    /// (a) `stop()` 取走尾音之后，一个仍在途的 `append()` 才把新样本写进已被清空、此后
    ///     再也不会被读取的缓冲区——那部分音频（通常是松键前最后几十毫秒）静默丢失；
    /// (b) `onChunk`/`onTailChunk` 分别从音频线程与主线程独立发起，两者各自创建的
    ///     `Task { @MainActor in ... }` 之间没有强制的先后关系，可能乱序执行。
    /// 让 `deliveryQueue.async` 的入队动作也发生在同一把锁内，即可让"入队顺序"严格
    /// 遵循锁定义的临界区顺序，从根上同时解决两个问题，见下方 `append`/`stop` 实现。
    private let lock = NSLock()
    private var converter: AVAudioConverter?
    private var chunker: AudioChunker
    private var isRunning = false
    private var configurationChangeObserver: (any NSObjectProtocol)?
    /// 转换/丢帧计数：只用于诊断，日志里只记数量，不记音频内容。
    private var droppedBufferCount = 0

    /// 串行投递队列：`onChunk`/`onTailChunk` 统一从这里触发，而不是分别直接从音频线程
    /// 和主线程调用，从而保证两者之间的相对顺序与 `lock` 临界区顺序一致（见上方注释）。
    private let deliveryQueue = DispatchQueue(label: "com.voicetyper.app.audiocapture.delivery")

    init(chunkSamples: Int = 9600) {
        self.chunkSamples = chunkSamples
        self.chunker = AudioChunker(chunkSamples: chunkSamples)
    }

    func start() throws {
        guard !isRunning else { return }

        lock.lock()
        chunker = AudioChunker(chunkSamples: chunkSamples)
        lock.unlock()

        let inputNode = engine.inputNode
        let inputFormat = inputNode.inputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0 else {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1003,
                userInfo: [NSLocalizedDescriptionKey: "没有可用的音频输入设备，请检查麦克风连接"]
            )
        }
        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1001,
                userInfo: [NSLocalizedDescriptionKey: "无法创建音频格式转换器"]
            )
        }

        self.converter = converter
        droppedBufferCount = 0
        inputNode.removeTap(onBus: 0)
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            self?.append(buffer: buffer)
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            // 启动失败时回滚已安装的 tap，避免残留一个不会再被 stop() 正常清理的挂起状态。
            inputNode.removeTap(onBus: 0)
            self.converter = nil
            throw error
        }
        isRunning = true

        configurationChangeObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            AppLog.audio.warning("音频引擎配置变更（设备切换），当前录音可能受影响")
            DispatchQueue.main.async {
                self?.handleConfigurationChangeDuringRecording()
            }
        }
    }

    /// 保留已采到的音频交给当前会话完成识别（走与正常停止相同的尾音刷出路径），
    /// 但明确告知用户设备已变化、本次录音已结束——不做自动重建 converter 或自动恢复。
    private func handleConfigurationChangeDuringRecording() {
        guard isRunning else { return }
        stop()
        onDeviceChanged?()
    }

    /// 停止录音，将剩余尾音通过 `onTailChunk` 发出。
    func stop() {
        lock.lock()
        guard isRunning else { lock.unlock(); return }
        isRunning = false
        let tail = chunker.drain()
        let tailHandler = onTailChunk
        deliveryQueue.async { tailHandler?(tail) }
        lock.unlock()

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        removeConfigurationChangeObserver()
        logDroppedBuffersIfAny()
    }

    func stopWithoutResult() {
        lock.lock()
        guard isRunning else { lock.unlock(); return }
        isRunning = false
        _ = chunker.drain()
        lock.unlock()

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        removeConfigurationChangeObserver()
        logDroppedBuffersIfAny()
    }

    // MARK: - Private

    /// 只记计数与错误码，不含音频内容（AGENTS.md 日志约束）。
    private func logDroppedBuffersIfAny() {
        lock.lock()
        let count = droppedBufferCount
        droppedBufferCount = 0
        lock.unlock()
        guard count > 0 else { return }
        AppLog.audio.warning("录音期间丢弃了 \(count, privacy: .public) 个音频缓冲区（转换失败）")
    }

    private func append(buffer: AVAudioPCMBuffer) {
        guard let converter else {
            lock.lock(); droppedBufferCount += 1; lock.unlock()
            return
        }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let targetFrameCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: max(targetFrameCapacity, 1)
        ) else {
            lock.lock(); droppedBufferCount += 1; lock.unlock()
            return
        }

        let inputState = AudioConverterInputState()
        var error: NSError?
        let status = converter.convert(to: convertedBuffer, error: &error) { _, outStatus in
            if inputState.hasProvidedInput {
                outStatus.pointee = .noDataNow
                return nil
            }
            inputState.hasProvidedInput = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard error == nil, status != .error,
              let channel = convertedBuffer.floatChannelData?.pointee else {
            lock.lock(); droppedBufferCount += 1; lock.unlock()
            return
        }

        let newSamples = Array(UnsafeBufferPointer(start: channel, count: Int(convertedBuffer.frameLength)))

        // 电平回调：不涉及 ringBuffer/顺序保证，保持原样直接在音频线程触发。
        if let onLevel, !newSamples.isEmpty {
            var sumSquares: Float = 0
            for sample in newSamples {
                sumSquares += sample * sample
            }
            onLevel((sumSquares / Float(newSamples.count)).squareRoot())
        }

        lock.lock()
        guard isRunning else {
            // stop() 已经把 isRunning 置 false 并取走尾音：这批样本必然是"迟到"的，
            // 若仍写进 chunker 会成为永远不会被 flush 的孤儿数据（R3-01 场景 a）。
            droppedBufferCount += 1
            lock.unlock()
            return
        }
        let chunks = chunker.append(newSamples)
        if !chunks.isEmpty {
            let handler = onChunk
            // 入队动作必须在锁内完成，才能保证与 stop() 那侧的入队顺序一致（见类注释）。
            for chunk in chunks {
                deliveryQueue.async { handler?(chunk) }
            }
        }
        lock.unlock()
    }

    private func removeConfigurationChangeObserver() {
        if let observer = configurationChangeObserver {
            NotificationCenter.default.removeObserver(observer)
            configurationChangeObserver = nil
        }
    }
}
