import Foundation
import Security

/// 当前运行的这份 App 是怎么签名的。
///
/// 为什么应用需要知道自己的签名方式：ad-hoc（本机）签名没有稳定的 Team ID，每次构建
/// 的 cdhash 都会变，而 TCC（麦克风 / 辅助功能 / 输入监控）的授权记录是以代码签名标识
/// 为键的——**这意味着每次更新版本后三项权限都可能失效，需要用户重新授权一遍**。
///
/// 这是本项目当前最影响长期使用体验的一件事（见 macos/README.md「签名与公证」）。
/// 在真正拿到 Developer ID 之前，至少要让用户**事先知道**会发生这件事，而不是升级后
/// 突然发现"热键失灵了"再去猜原因。反过来，一旦维护者用 Developer ID 签名分发，
/// 这条提示必须自动消失——所以是运行期检测，而不是写死的常量。
enum CodeSigningInfo {
    enum Kind: Equatable {
        /// 有稳定 Team ID 的正式签名（Developer ID / App Store）：更新后 TCC 授权可保留。
        case teamSigned(team: String)
        /// ad-hoc 或完全未签名：更新后 TCC 授权可能失效。
        case adHocOrUnsigned
        /// 查询失败。不做任何断言，界面上按"不显示提示"处理，避免误报。
        case unknown
    }

    /// 查询一次当前进程的签名信息。结果在进程生命周期内不会变，故缓存。
    static let current: Kind = resolve()

    /// 是否需要向用户提示"更新后可能要重新授权"。
    /// `unknown` 不提示：查询失败时保持沉默好过报一条可能不成立的坏消息。
    static var mayLosePermissionsOnUpdate: Bool { current == .adHocOrUnsigned }

    private static func resolve() -> Kind {
        var code: SecCode?
        guard SecCodeCopySelf(SecCSFlags(rawValue: 0), &code) == errSecSuccess, let code else {
            return .unknown
        }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, SecCSFlags(rawValue: 0), &staticCode) == errSecSuccess,
              let staticCode else {
            return .unknown
        }
        var information: CFDictionary?
        let status = SecCodeCopySigningInformation(
            staticCode,
            SecCSFlags(rawValue: UInt32(kSecCSSigningInformation)),
            &information
        )
        guard status == errSecSuccess, let dictionary = information as? [String: Any] else {
            return .unknown
        }
        if let team = dictionary[kSecCodeInfoTeamIdentifier as String] as? String,
           !team.trimmingCharacters(in: .whitespaces).isEmpty {
            return .teamSigned(team: team)
        }
        return .adHocOrUnsigned
    }
}
