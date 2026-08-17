import OSLog

enum AppLog {
    static let subsystem = AppConstants.bundleIdentifier
    static let app = Logger(subsystem: subsystem, category: "app")
    static let permissions = Logger(subsystem: subsystem, category: "permissions")
    static let hotkey = Logger(subsystem: subsystem, category: "hotkey")
    static let audio = Logger(subsystem: subsystem, category: "audio")
    static let network = Logger(subsystem: subsystem, category: "network")
    static let input = Logger(subsystem: subsystem, category: "input")
}
