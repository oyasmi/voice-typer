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
    /// 辅助功能查询是跨进程调用，这里在主线程上做；超时（`AXMessaging.probeTimeout`）按「取不到」处理。
    func isFocusedElementSecure() -> Bool {
        guard AXIsProcessTrusted(),
              let element = AXMessaging.focusedElement(timeout: AXMessaging.probeTimeout) else {
            return false
        }
        var subroleObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subroleObject) == .success,
              let subrole = subroleObject as? String else {
            return false
        }
        return subrole == (kAXSecureTextFieldSubrole as String)
    }
}
