import OSLog

enum AppLog {
    static let subsystem = AppConstants.bundleIdentifier
    static let app = Logger(subsystem: subsystem, category: "app")
    static let permissions = Logger(subsystem: subsystem, category: "permissions")
    static let hotkey = Logger(subsystem: subsystem, category: "hotkey")
    static let audio = Logger(subsystem: subsystem, category: "audio")
    static let asr = Logger(subsystem: subsystem, category: "asr")
    static let llm = Logger(subsystem: subsystem, category: "llm")
    static let model = Logger(subsystem: subsystem, category: "model")
}
