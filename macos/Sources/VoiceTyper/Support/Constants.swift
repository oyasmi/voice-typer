import Foundation

enum AppConstants {
    static let appName = "VoiceTyper"
    static let bundleIdentifier = "com.voicetyper.app"
    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "3.1.6"
    static let repositoryURL = URL(string: "https://github.com/oyasmi/voice-typer")!
    static let targetSampleRate: Double = 16_000
    static let appSupportDirectoryName = "VoiceTyper"
    static let configFileName = "config.yaml"
}

enum SystemSettingsURL {
    static let microphone = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
    static let accessibility = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!
    static let inputMonitoring = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!
}
