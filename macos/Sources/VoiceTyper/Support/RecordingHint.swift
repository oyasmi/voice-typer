import Foundation

/// 录音状态行末尾的操作提示（R8-C4）。
enum RecordingHint {
    /// hold 模式前几次录音提示「Esc 取消」，之后用户已经知道了，不再占位。
    static let escHintMaximumShows = 5
    static let escShownCountDefaultsKey = "hint.escCancelShownCount"

    /// - Returns: 提示文本；`nil` 表示不提示。
    static func text(mode: HotkeyMode, hotkeyDisplay: String, escShownCount: Int) -> String? {
        switch mode {
        case .toggle:
            return LF("再按 %@ 结束", hotkeyDisplay)
        case .hold:
            return escShownCount < escHintMaximumShows ? L("Esc 取消") : nil
        }
    }
}

/// 更新后重新授权引导（R8-H1）：本机（ad-hoc）签名的版本更新后，系统把它当成新应用，权限失效。
enum ReauthorizationNotice {
    static let lastVersionKey = "reauth.lastLaunchedVersion"
    static let wereAllGrantedKey = "reauth.permissionsWereAllGranted"

    /// 启动时判定：版本号变化、上次权限齐全而这次不齐、且签名确实会丢授权（Developer ID 签名不显示）。
    static func shouldShow(
        lastVersion: String?, currentVersion: String,
        wereAllGranted: Bool, nowAllGranted: Bool, mayLosePermissionsOnUpdate: Bool
    ) -> Bool {
        guard mayLosePermissionsOnUpdate, let lastVersion, lastVersion != currentVersion else { return false }
        return wereAllGranted && !nowAllGranted
    }

    static func bannerText(version: String) -> String {
        LF("刚更新到 %@。本机签名的版本更新后，系统会把它当成新应用，需要重新授权：请在「系统设置 → 隐私与安全性」里先移除旧的 VoiceTyper 条目，再重新添加。", version)
    }
}
