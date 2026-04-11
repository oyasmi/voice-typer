@preconcurrency import AVFoundation
import Foundation

struct RecordedAudio {
    let data: Data
    let duration: TimeInterval
}

final class AudioCaptureService: @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: AppConstants.targetSampleRate,
        channels: 1,
        interleaved: false
    )!

    private let lock = NSLock()
    private var converter: AVAudioConverter?
    private var capturedSamples: [Float] = []
    private var isRunning = false

    func start() throws {
        guard !isRunning else {
            return
        }

        capturedSamples = []

        let inputNode = engine.inputNode
        let inputFormat = inputNode.inputFormat(forBus: 0)
        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            throw NSError(domain: AppConstants.bundleIdentifier, code: 1001, userInfo: [NSLocalizedDescriptionKey: "无法创建音频格式转换器"])
        }

        self.converter = converter
        inputNode.removeTap(onBus: 0)
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            self?.append(buffer: buffer)
        }

        engine.prepare()
        try engine.start()
        isRunning = true
    }

    func stop() throws -> RecordedAudio {
        guard isRunning else {
            return RecordedAudio(data: Data(), duration: 0)
        }

        let inputNode = engine.inputNode
        inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false

        let samples: [Float]
        lock.lock()
        samples = capturedSamples
        capturedSamples = []
        lock.unlock()

        let data = samples.withUnsafeBufferPointer { pointer in
            guard let baseAddress = pointer.baseAddress else {
                return Data()
            }
            return Data(bytes: baseAddress, count: pointer.count * MemoryLayout<Float>.size)
        }
        let duration = Double(samples.count) / AppConstants.targetSampleRate
        return RecordedAudio(data: data, duration: duration)
    }

    func stopWithoutResult() {
        guard isRunning else {
            return
        }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        lock.lock()
        capturedSamples = []
        lock.unlock()
    }

    private func append(buffer: AVAudioPCMBuffer) {
        guard let converter else {
            return
        }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let targetFrameCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: max(targetFrameCapacity, 1)
        ) else {
            return
        }

        var providedInput = false
        var error: NSError?
        let status = converter.convert(to: convertedBuffer, error: &error) { _, outStatus in
            if providedInput {
                outStatus.pointee = .noDataNow
                return nil
            }

            providedInput = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard error == nil, status != .error, let channel = convertedBuffer.floatChannelData?.pointee else {
            return
        }

        let sampleCount = Int(convertedBuffer.frameLength)
        let newSamples = Array(UnsafeBufferPointer(start: channel, count: sampleCount))
        lock.lock()
        capturedSamples.append(contentsOf: newSamples)
        lock.unlock()
    }
}
