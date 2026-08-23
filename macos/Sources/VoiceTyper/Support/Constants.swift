import Foundation

enum AppConstants {
    static let appName = "VoiceTyper"
    static let bundleIdentifier = "com.voicetyper.app"
    // 兜底值故意不是一个看起来合理的版本号：读不到 Info.plist（测试宿主、非常规启动方式）
    // 这件事本身应该可见，而不是被伪装成"读到了 3.1.9"——历史上版本号已经在生成脚本与
    // pbxproj 之间漂移过一次（见 macos/DESIGN.md §9），一个逼真的兜底值只会让下一次
    // 漂移更难被发现（R4-08）。
    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0-dev"
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
