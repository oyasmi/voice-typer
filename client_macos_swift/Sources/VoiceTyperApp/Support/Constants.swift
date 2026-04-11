import Foundation

enum AppConstants {
    static let appName = "VoiceTyper"
    static let bundleIdentifier = "com.voicetyper.app"
    static let version = "2.0.0"
    static let minimumRecordingDuration: TimeInterval = 0.3
    static let targetSampleRate: Double = 16_000
    static let configDirectoryName = "voice_typer"
    static let configFileName = "config.yaml"
    static let defaultHotwordsFileName = "hotwords.txt"
}

enum SystemSettingsURL {
    static let microphone = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
    static let accessibility = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!
    static let inputMonitoring = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!
}
