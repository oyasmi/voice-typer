import ApplicationServices
import Foundation

/// 读取系统当前焦点控件的性质。控制器通过协议依赖它，测试注入假实现（R8-B2）。
@MainActor
protocol FocusInspecting: AnyObject {
    /// 焦点控件是否为密码输入框（`AXSecureTextField`）。取不到焦点或查询失败一律返回 false：
    /// 误判为密码框会让正常听写被拒绝，代价比漏判更大。
    func isFocusedElementSecure() -> Bool
}

@MainActor
final class AXFocusInspector: FocusInspecting {
    /// 辅助功能查询是跨进程调用，目标应用无响应时默认要等数秒；这里在主线程上做，
    /// 把单次查询压到 250ms 以内，超时按「取不到」处理。
    private static let messagingTimeout: Float = 0.25

    func isFocusedElementSecure() -> Bool {
        guard AXIsProcessTrusted() else { return false }
        let systemWide = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(systemWide, Self.messagingTimeout)

        var focusedObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(systemWide, kAXFocusedUIElementAttribute as CFString, &focusedObject) == .success,
              let focusedObject,
              CFGetTypeID(focusedObject) == AXUIElementGetTypeID() else {
            return false
        }
        // 上面已校验类型，强转是安全的。
        let element = focusedObject as! AXUIElement
        AXUIElementSetMessagingTimeout(element, Self.messagingTimeout)

        var subroleObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subroleObject) == .success,
              let subrole = subroleObject as? String else {
            return false
        }
        return subrole == (kAXSecureTextFieldSubrole as String)
    }
}
