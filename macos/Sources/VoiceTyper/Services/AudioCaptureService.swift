@preconcurrency import AVFoundation
import CoreAudio
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
    /// 例外：还没收到任何样本时的配置变更（启动阶段）会在内部透明重启，不触发本回调。
    /// 在主线程触发。
    var onDeviceChanged: (() -> Void)?

    let chunkSamples: Int

    /// 本次录音实际使用的输入设备；`start` 成功后才有值，只在主线程读写。
    private(set) var activeInputDevice: ActiveInputDevice?

    /// 跨会话复用；只在"上次钉过设备、这次要回到跟随系统默认"时整体重建（见 `bindInputDevice`）。
    /// 只在主线程替换，且只在未录音时替换。
    private var engine = AVAudioEngine()
    /// 上一次显式钉到引擎上的输入设备；nil 表示引擎处于跟随系统默认的状态。只在主线程读写。
    private var pinnedDeviceID: AudioDeviceID?
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
    /// 每次（重新）启动引擎递增；配置变更通知经 `main.async` 转发，可能在 stop / 重启之后
    /// 才到达，用它丢弃上一轮引擎的过期通知。只在主线程读写。
    private var engineGeneration = 0
    /// 本次录音是否已因"启动阶段配置变更"重启过一次引擎，防止反复重启。只在主线程读写。
    private var hasRestartedForConfigurationChange = false
    /// 本次录音是否已收到过有效样本（受 `lock` 保护）：区分"启动阶段的配置变更"
    /// （可安全重启，用户无感）与"录音中途的设备变化"（结束本次录音）。
    private var hasReceivedAudio = false
    /// 转换失败丢帧计数：只用于诊断，日志里只记数量，不记音频内容。
    private var droppedConversionFailedCount = 0
    /// `stop()` 与音频线程竞态导致的"迟到"丢帧计数：这是 R3-01 场景 (a) 的正常代价
    /// （tap 回调与 `isRunning` 置位之间存在窗口），不代表转换出错，日志措辞与
    /// 严重级别都应与转换失败区分（R4-07）。
    private var droppedNotRunningCount = 0

    /// 串行投递队列：`onChunk`/`onTailChunk` 统一从这里触发，而不是分别直接从音频线程
    /// 和主线程调用，从而保证两者之间的相对顺序与 `lock` 临界区顺序一致（见上方注释）。
    private let deliveryQueue = DispatchQueue(label: "com.voicetyper.app.audiocapture.delivery")

    init(chunkSamples: Int = 9600) {
        self.chunkSamples = chunkSamples
        self.chunker = AudioChunker(chunkSamples: chunkSamples)
    }

    /// - Parameter inputPolicy: 输入设备策略。设备在录音开始前解析一次，录音中不切换。
    func start(inputPolicy: AudioInputPolicy) throws {
        guard !isRunning else { return }

        // 必须在读取 inputFormat / installTap 之前完成：换设备会改变输入格式。
        activeInputDevice = bindInputDevice(inputPolicy)

        // isRunning 必须在 installTap 之前、且在锁内置位：tap 回调理论上可能在
        // engine.start() 返回后的极窄窗口内、于音频线程几乎立即触发，若仍在锁外、
        // 在 engine.start() 之后才置位，这段窗口里到达的样本会被 append(buffer:)
        // 误判为"stop() 已经跑过"而丢弃，且这本身就是一处不受锁保护的跨线程写（R4-07）。
        lock.lock()
        chunker = AudioChunker(chunkSamples: chunkSamples)
        droppedConversionFailedCount = 0
        droppedNotRunningCount = 0
        hasReceivedAudio = false
        isRunning = true
        lock.unlock()
        hasRestartedForConfigurationChange = false

        do {
            try startEngine()
        } catch {
            // 启动失败时回滚 isRunning，避免残留一个不会再被 stop() 正常清理的挂起状态。
            lock.lock()
            isRunning = false
            lock.unlock()
            throw error
        }
    }

    /// 按当前输入格式建 converter、装 tap、启动引擎并开始监听配置变更。
    /// 失败时撤掉 tap 与 converter 后抛出，`isRunning` 由调用方负责回滚。
    private func startEngine() throws {
        let inputNode = engine.inputNode
        let inputFormat = inputNode.inputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0 else {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1003,
                userInfo: [NSLocalizedDescriptionKey: L("没有可用的音频输入设备，请检查麦克风连接")]
            )
        }
        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            throw NSError(
                domain: AppConstants.bundleIdentifier,
                code: 1001,
                userInfo: [NSLocalizedDescriptionKey: L("无法创建音频格式转换器")]
            )
        }
        self.converter = converter

        inputNode.removeTap(onBus: 0)
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            self?.append(buffer: buffer)
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            inputNode.removeTap(onBus: 0)
            self.converter = nil
            throw error
        }

        engineGeneration &+= 1
        let generation = engineGeneration
        configurationChangeObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            AppLog.audio.warning("音频引擎配置变更（设备切换），当前录音可能受影响")
            DispatchQueue.main.async {
                self?.handleConfigurationChangeDuringRecording(generation: generation)
            }
        }
    }

    /// 按策略解析输入设备并绑定到引擎，返回实际使用的设备。
    ///
    /// - 目标就是系统默认输入（`auto` 的绝大多数情况、`system`）：**不设置设备**，让引擎跟随
    ///   系统默认。显式设置会让 AUHAL 从引擎自己的默认聚合设备切到麦克风本身，引擎启动后
    ///   随即发出配置变更并停机（原因见 `AudioInputDevice.deviceToPin`）。引擎空闲期间系统
    ///   默认设备变化（睡眠唤醒、插拔设备、合盖）时，引擎会自行重建聚合设备，所以这条路径
    ///   在"隔夜后第一次按热键"时同样稳定。
    /// - 上一次钉过别的设备、这次要回到系统默认：引擎不能"取消钉住"，直接换一个新引擎。
    /// - 需要钉到非默认设备（蓝牙场景改用内置麦克风、指定 UID）：显式设置。此时仍可能在启动
    ///   阶段收到一次配置变更，由 `handleConfigurationChangeDuringRecording` 透明重启兜底。
    ///
    /// 设置失败只记 warning、沿用引擎当前设备，不中断录音。
    private func bindInputDevice(_ policy: AudioInputPolicy) -> ActiveInputDevice? {
        let resolved = AudioInputDevice.resolveCurrent(policy: policy)

        guard let pinID = AudioInputDevice.deviceToPin(
            resolvedID: resolved.deviceID, defaultInputID: resolved.defaultInputID
        ) else {
            if pinnedDeviceID != nil {
                engine = AVAudioEngine()
                pinnedDeviceID = nil
            }
            // 跟随默认时 AUHAL 读回的是聚合设备，展示名称与耗时日志以系统默认输入为准。
            return resolved.deviceID.map {
                AudioInputDevice.activeDevice(id: $0, switchedByAuto: resolved.switchedByAuto)
            }
        }

        guard let audioUnit = engine.inputNode.audioUnit else { return nil }
        var deviceID = pinID
        let status = AudioUnitSetProperty(
            audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
            &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        if status != noErr {
            AppLog.audio.warning("设置输入设备失败（OSStatus \(status, privacy: .public)），沿用引擎当前设备")
        }
        // 即使设置失败也记为"钉过"：引擎状态已不可知，下次回到默认时宁可重建。
        pinnedDeviceID = pinID

        // 读回实际使用的设备：显示名称与耗时日志都以它为准，而不是以"想设置的"为准。
        var actualID = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioUnitGetProperty(
            audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &actualID, &size
        ) == noErr, actualID != 0 else { return nil }
        return AudioInputDevice.activeDevice(
            id: actualID, switchedByAuto: resolved.switchedByAuto && actualID == pinID
        )
    }

    /// 配置变更的两种处理：
    /// - 还没收到任何样本（启动阶段，设备切换尚未"落定"）：按新格式重建 converter 与 tap、
    ///   重启引擎，录音继续，用户无感；每次录音至多重启一次。
    /// - 已经在收音（录音中途拔麦克风、切换设备等）：保留已采到的音频交给当前会话完成识别
    ///   （走与正常停止相同的尾音刷出路径），并明确告知用户设备已变化、本次录音已结束。
    private func handleConfigurationChangeDuringRecording(generation: Int) {
        guard isRunning, generation == engineGeneration else { return }

        lock.lock()
        let receivedAudio = hasReceivedAudio
        lock.unlock()

        if !receivedAudio, !hasRestartedForConfigurationChange {
            hasRestartedForConfigurationChange = true
            removeConfigurationChangeObserver()
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
            do {
                try startEngine()
                AppLog.audio.info("录音启动阶段音频引擎配置变更，已重启采集")
                return
            } catch {
                AppLog.audio.error("配置变更后重启采集失败: \(error.localizedDescription, privacy: .public)")
            }
        }

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

    /// 只记计数与错误码，不含音频内容（AGENTS.md 日志约束）。分开报告两类丢帧：
    /// 转换失败是真实异常，值得 warning；"迟到"丢帧是 stop() 与音频线程竞态下的
    /// 正常代价，不代表出错，用 info 即可（R4-07）。
    private func logDroppedBuffersIfAny() {
        lock.lock()
        let conversionFailed = droppedConversionFailedCount
        let notRunning = droppedNotRunningCount
        droppedConversionFailedCount = 0
        droppedNotRunningCount = 0
        lock.unlock()
        if conversionFailed > 0 {
            AppLog.audio.warning("录音期间丢弃了 \(conversionFailed, privacy: .public) 个音频缓冲区（格式转换失败）")
        }
        if notRunning > 0 {
            AppLog.audio.info("录音期间丢弃了 \(notRunning, privacy: .public) 个音频缓冲区（stop() 之后的迟到样本，属预期行为）")
        }
    }

    private func append(buffer: AVAudioPCMBuffer) {
        guard let converter else {
            lock.lock(); droppedConversionFailedCount += 1; lock.unlock()
            return
        }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let targetFrameCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: max(targetFrameCapacity, 1)
        ) else {
            lock.lock(); droppedConversionFailedCount += 1; lock.unlock()
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
            lock.lock(); droppedConversionFailedCount += 1; lock.unlock()
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
            droppedNotRunningCount += 1
            lock.unlock()
            return
        }
        hasReceivedAudio = true
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
