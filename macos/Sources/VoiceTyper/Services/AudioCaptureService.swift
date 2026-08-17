@preconcurrency import AVFoundation
import Foundation

private final class AudioConverterInputState: @unchecked Sendable {
    var hasProvidedInput = false
}

/// 流式录音服务。
///
/// 录音期间每凑满 `chunkSamples` 个 float32 样本就通过 `onChunk` 发出一帧；
/// 停止时将剩余不足一帧的尾音通过 `onTailChunk` 发出，随后调用 `onStopped`。
final class AudioCaptureService: @unchecked Sendable {
    /// 每 600ms 触发一次，传入 float32 PCM 字节（9600 samples = 38400 bytes）
    var onChunk: (([Float]) -> Void)?
    /// 停止录音时触发一次，传入剩余不足一帧的尾音（可能为空数组）
    var onTailChunk: (([Float]) -> Void)?
    /// 每次音频输入回调触发一次（约 20–60ms），传入该缓冲区的线性 RMS 电平（0…1 量级，
    /// 未做分贝归一化）。在音频线程调用，消费方负责切换线程与平滑。
    var onLevel: ((Float) -> Void)?
    /// 录音期间输入设备变化（拔麦克风、切换音频设备等）导致本次录音被迫结束时触发一次，
    /// 传入用户可读的提示信息。已采到的音频仍会通过 `onTailChunk` 正常交给当前会话
    /// 完成识别；这里只做"可见告知"，不做自动重建 converter 或自动恢复（F-15）。
    /// 在主线程触发。
    var onFatalError: ((String) -> Void)?

    let chunkSamples: Int

    private let engine = AVAudioEngine()
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: AppConstants.targetSampleRate,
        channels: 1,
        interleaved: false
    )!

    private let lock = NSLock()
    private var converter: AVAudioConverter?
    private var ringBuffer: [Float] = []
    private var isRunning = false
    private var configurationChangeObserver: (any NSObjectProtocol)?
    /// 转换/丢帧计数：只用于诊断，日志里只记数量，不记音频内容。
    private var droppedBufferCount = 0

    init(chunkSamples: Int = 9600) {
        self.chunkSamples = chunkSamples
    }

    func start() throws {
        guard !isRunning else { return }

        lock.lock()
        ringBuffer = []
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
        onFatalError?("输入设备已变化，本次录音已结束")
    }

    /// 停止录音，将剩余尾音通过 `onTailChunk` 发出。
    func stop() {
        guard isRunning else { return }

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        removeConfigurationChangeObserver()
        logDroppedBuffersIfAny()

        lock.lock()
        let tail = ringBuffer
        ringBuffer = []
        lock.unlock()

        onTailChunk?(tail)
    }

    func stopWithoutResult() {
        guard isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        removeConfigurationChangeObserver()
        logDroppedBuffersIfAny()
        lock.lock()
        ringBuffer = []
        lock.unlock()
    }

    // MARK: - Private

    private func recordDroppedBuffer() {
        lock.lock()
        droppedBufferCount += 1
        lock.unlock()
    }

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
            recordDroppedBuffer()
            return
        }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let targetFrameCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: max(targetFrameCapacity, 1)
        ) else {
            recordDroppedBuffer()
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
            recordDroppedBuffer()
            return
        }

        let newSamples = Array(UnsafeBufferPointer(start: channel, count: Int(convertedBuffer.frameLength)))

        // 电平回调：在锁外计算 RMS 并发出，供 HUD 波形显示真实音量。
        if let onLevel, !newSamples.isEmpty {
            var sumSquares: Float = 0
            for sample in newSamples {
                sumSquares += sample * sample
            }
            onLevel((sumSquares / Float(newSamples.count)).squareRoot())
        }

        lock.lock()
        ringBuffer.append(contentsOf: newSamples)
        // 每凑满 chunkSamples 就取出一帧
        while ringBuffer.count >= chunkSamples {
            let chunk = Array(ringBuffer.prefix(chunkSamples))
            ringBuffer.removeFirst(chunkSamples)
            lock.unlock()
            onChunk?(chunk)
            lock.lock()
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
