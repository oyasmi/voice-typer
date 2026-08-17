import Foundation

enum AppState: Equatable {
    case booting
    case setupRequired
    /// 权限齐全但服务端尚未就绪（如模型仍在加载），后台自动轮询重连中。
    case connecting
    case idle
    case recording
    case recognizing
    case inserting
    /// 用户主动暂停听写：热键监听与服务轮询均停止，直到用户从菜单恢复。
    case paused
    case error(String)

    var menuTitle: String {
        switch self {
        case .booting:
            return "启动中"
        case .setupRequired:
            return "需要完成授权与设置"
        case .connecting:
            return "连接服务中…"
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

    var statusSymbolName: String {
        switch self {
        case .booting:
            return "hourglass"
        case .setupRequired:
            return "exclamationmark.triangle"
        case .connecting:
            return "arrow.triangle.2.circlepath"
        case .idle:
            return "mic"
        case .recording:
            return "mic.fill"
        case .recognizing:
            return "waveform"
        case .inserting:
            return "character.cursor.ibeam"
        case .paused:
            return "mic.slash"
        case .error:
            return "exclamationmark.circle"
        }
    }
}
