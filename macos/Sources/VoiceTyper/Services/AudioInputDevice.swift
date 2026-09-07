@preconcurrency import AVFoundation
import Foundation

/// 当前系统默认音频输入设备的信息。
///
/// 只用于**显示**：HUD 在录音时把设备名写进状态行。没有它的话，"切了蓝牙耳机之后
/// 一个字都录不到"这类问题里，用户完全看不出应用正在听哪一个麦克风——而这恰好是
/// 静音检测（`VoiceTyperController` 的静音探针）触发后，用户最需要的下一条线索。
///
/// 用 `AVCaptureDevice.default(for: .audio)` 而不是绕到 CoreAudio HAL：它返回的就是
/// 系统默认输入设备，与 `AVAudioEngine.inputNode` 实际使用的是同一个；查询本身不会
/// 触发麦克风权限弹窗，也不会启动任何采集。
enum AudioInputDevice {
    /// 当前默认输入设备名；取不到返回 nil（调用方据此只显示"录音中"）。
    static func currentName() -> String? {
        guard let name = AVCaptureDevice.default(for: .audio)?.localizedName else { return nil }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
