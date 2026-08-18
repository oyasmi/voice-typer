import Foundation

/// `VoiceTyperController` 依赖的三个具体服务各抽一个只含控制器实际用到的成员的协议，
/// 供测试注入假实现——原先三者都是 `final class` 具体类型，导致控制器（承载
/// R2-01/R2-03/R2-04 全部状态机逻辑的地方）完全没有单元测试覆盖（§3.9）。
@MainActor
protocol HotkeyListening: AnyObject {
    var onPress: (() -> Void)? { get set }
    var onRelease: (() -> Void)? { get set }
    var onCancel: (() -> Void)? { get set }
    func start(with hotkey: HotkeyConfig) throws
    func stop()
}

@MainActor
protocol AudioCapturing: AnyObject {
    var onChunk: (([Float]) -> Void)? { get set }
    var onTailChunk: (([Float]) -> Void)? { get set }
    var onLevel: ((Float) -> Void)? { get set }
    var onDeviceChanged: (() -> Void)? { get set }
    func start() throws
    func stop()
    func stopWithoutResult()
}

@MainActor
protocol TextInserting: AnyObject {
    func insert(text: String, expectedFrontmostPID: pid_t?) -> TextInsertionResult
    func copyToClipboard(text: String)
}

extension HotkeyService: HotkeyListening {}
extension AudioCaptureService: AudioCapturing {}
extension TextInsertionService: TextInserting {}
