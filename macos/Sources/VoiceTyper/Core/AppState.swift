import Foundation

enum AppState: Equatable {
    case booting
    case setupRequired
    /// 权限齐全，但本地识别模型尚未就绪：本机既无内置也无侧载模型副本。
    case modelMissing
    /// 正在下载模型，参数为 0…1 的下载进度。
    case downloadingModel(Double)
    /// 模型文件已就绪，正在加载进内存（ONNX session 构建等）。
    case modelLoading
    case idle
    case recording
    case recognizing
    case inserting
    /// 用户主动暂停听写：热键监听停止，直到用户从菜单恢复。
    case paused
    case error(String)

    /// 录音/识别/纠错/插入进行中：此时保存设置若销毁控制器会丢已录制内容（F-13）。
    var isActiveDictation: Bool {
        switch self {
        case .recording, .recognizing, .inserting:
            return true
        default:
            return false
        }
    }

    var menuTitle: String {
        switch self {
        case .booting:
            return "启动中"
        case .setupRequired:
            return "需要完成授权与设置"
        case .modelMissing:
            return "需要下载语音模型"
        case .downloadingModel(let progress):
            return "下载模型 \(Int(progress * 100))%"
        case .modelLoading:
            return "模型加载中…"
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
        case .modelMissing, .downloadingModel:
            return "arrow.down.circle"
        case .modelLoading:
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
