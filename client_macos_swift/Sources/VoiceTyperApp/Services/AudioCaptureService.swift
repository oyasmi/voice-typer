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
    var onChunk: ((Data) -> Void)?
    /// 停止录音时触发一次，传入剩余不足一帧的尾音（可能为空 Data）
    var onTailChunk: ((Data) -> Void)?

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
        inputNode.removeTap(onBus: 0)
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            self?.append(buffer: buffer)
        }

        engine.prepare()
        try engine.start()
        isRunning = true

        configurationChangeObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { _ in
            AppLog.audio.warning("音频引擎配置变更（设备切换），当前录音可能受影响")
        }
    }

    /// 停止录音，将剩余尾音通过 `onTailChunk` 发出。
    func stop() {
        guard isRunning else { return }

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        removeConfigurationChangeObserver()

        lock.lock()
        let tail = ringBuffer
        ringBuffer = []
        lock.unlock()

        let tailData = floatsToData(tail)
        onTailChunk?(tailData)
    }

    func stopWithoutResult() {
        guard isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        removeConfigurationChangeObserver()
        lock.lock()
        ringBuffer = []
        lock.unlock()
    }

    // MARK: - Private

    private func append(buffer: AVAudioPCMBuffer) {
        guard let converter else { return }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let targetFrameCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: max(targetFrameCapacity, 1)
        ) else { return }

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
              let channel = convertedBuffer.floatChannelData?.pointee else { return }

        let newSamples = Array(UnsafeBufferPointer(start: channel, count: Int(convertedBuffer.frameLength)))

        lock.lock()
        ringBuffer.append(contentsOf: newSamples)
        // 每凑满 chunkSamples 就取出一帧
        while ringBuffer.count >= chunkSamples {
            let chunk = Array(ringBuffer.prefix(chunkSamples))
            ringBuffer.removeFirst(chunkSamples)
            lock.unlock()
            onChunk?(floatsToData(chunk))
            lock.lock()
        }
        lock.unlock()
    }

    private func floatsToData(_ samples: [Float]) -> Data {
        samples.withUnsafeBufferPointer { ptr in
            guard let base = ptr.baseAddress else { return Data() }
            return Data(bytes: base, count: ptr.count * MemoryLayout<Float>.size)
        }
    }

    private func removeConfigurationChangeObserver() {
        if let observer = configurationChangeObserver {
            NotificationCenter.default.removeObserver(observer)
            configurationChangeObserver = nil
        }
    }
}
