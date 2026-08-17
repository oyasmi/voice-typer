import Foundation

enum AppConstants {
    static let appName = "VoiceTyperClient"
    static let bundleIdentifier = "com.voicetyper.client"
    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "2.7.0"
    static let targetSampleRate: Double = 16_000
    static let configDirectoryName = "voice_typer"
    static let configFileName = "config.yaml"
}

enum SystemSettingsURL {
    static let microphone = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
    static let accessibility = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!
    static let inputMonitoring = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!
}
