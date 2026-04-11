import Foundation

enum AppState: Equatable {
    case booting
    case setupRequired
    case idle
    case recording
    case recognizing
    case inserting
    case paused
    case error(String)

    var menuTitle: String {
        switch self {
        case .booting:
            return "启动中"
        case .setupRequired:
            return "需要完成授权与设置"
        case .idle:
            return "就绪"
        case .recording:
            return "录音中..."
        case .recognizing:
            return "识别中..."
        case .inserting:
            return "输入中..."
        case .paused:
            return "已暂停"
        case .error(let message):
            return "错误: \(message)"
        }
    }

    var statusIcon: String {
        switch self {
        case .booting:
            return "⏳"
        case .setupRequired:
            return "⚠️"
        case .idle:
            return "🎤"
        case .recording:
            return "🟢"
        case .recognizing:
            return "🟡"
        case .inserting:
            return "🟠"
        case .paused:
            return "⏸️"
        case .error:
            return "🔴"
        }
    }
}
