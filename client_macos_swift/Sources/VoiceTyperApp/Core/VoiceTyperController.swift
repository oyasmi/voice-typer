import AppKit
import Foundation

@MainActor
final class VoiceTyperController {
    private let config: AppConfig
    private let hotwords: [String]
    private let hotkeyService: HotkeyService
    private let audioCaptureService: AudioCaptureService
    private let asrClient: ASRClient
    private let textInsertionService: TextInsertionService

    private var isRecording = false
    private var isRunning = false
    private var recognitionTask: Task<Void, Never>?

    var onStateChange: ((AppState) -> Void)?
    var onRecognizedText: ((String) -> Void)?
    var isStarted: Bool { isRunning }

    init(
        config: AppConfig,
        hotwords: [String],
        hotkeyService: HotkeyService = HotkeyService(),
        audioCaptureService: AudioCaptureService = AudioCaptureService(),
        asrClient: ASRClient = ASRClient(),
        textInsertionService: TextInsertionService = TextInsertionService()
    ) {
        self.config = config
        self.hotwords = hotwords
        self.hotkeyService = hotkeyService
        self.audioCaptureService = audioCaptureService
        self.asrClient = asrClient
        self.textInsertionService = textInsertionService
    }

    func start() throws {
        guard !isRunning else {
            return
        }

        hotkeyService.onPress = { [weak self] in
            Task { @MainActor [weak self] in
                self?.beginRecording()
            }
        }
        hotkeyService.onRelease = { [weak self] in
            Task { @MainActor [weak self] in
                self?.finishRecording()
            }
        }
        try hotkeyService.start(with: config.hotkey)
        isRunning = true
        onStateChange?(.idle)
    }

    func stop() {
        hotkeyService.stop()
        audioCaptureService.stopWithoutResult()
        recognitionTask?.cancel()
        recognitionTask = nil
        isRunning = false
        isRecording = false
    }

    func healthCheck() async -> Bool {
        await asrClient.healthCheck(server: config.server)
    }

    private func beginRecording() {
        guard isRunning, !isRecording else {
            return
        }
        isRecording = true

        do {
            try audioCaptureService.start()
            onStateChange?(.recording)
        } catch {
            AppLog.audio.error("开始录音失败: \(error.localizedDescription, privacy: .public)")
            isRecording = false
            onStateChange?(.error("开始录音失败"))
        }
    }

    private func finishRecording() {
        guard isRecording else {
            return
        }
        isRecording = false

        do {
            let result = try audioCaptureService.stop()
            guard result.duration >= AppConstants.minimumRecordingDuration else {
                onStateChange?(.idle)
                return
            }

            onStateChange?(.recognizing)

            let asrClient = self.asrClient
            let server = self.config.server
            let hotwords = self.hotwords
            let task = Task(priority: .userInitiated) { @MainActor [weak self, asrClient, server, hotwords, audioData = result.data] in
                defer {
                    self?.recognitionTask = nil
                }
                do {
                    guard !Task.isCancelled else {
                        return
                    }
                    let text = try await asrClient.recognize(
                        audioData: audioData,
                        hotwords: hotwords,
                        server: server
                    )

                    guard !Task.isCancelled else {
                        return
                    }
                    guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                        self?.onStateChange?(.idle)
                        return
                    }

                    guard self?.isRunning == true else {
                        return
                    }
                    self?.onStateChange?(.inserting)

                    let inserted = self?.textInsertionService.insert(text: text) ?? false
                    if inserted {
                        self?.onRecognizedText?(text)
                        self?.onStateChange?(.idle)
                    } else {
                        self?.onStateChange?(.error("文本插入失败"))
                    }
                } catch is CancellationError {
                    return
                } catch {
                    AppLog.network.error("识别失败: \(error.localizedDescription, privacy: .public)")
                    self?.onStateChange?(.error("识别失败"))
                }
            }
            recognitionTask = task
        } catch {
            AppLog.audio.error("停止录音失败: \(error.localizedDescription, privacy: .public)")
            onStateChange?(.error("停止录音失败"))
        }
    }


}
