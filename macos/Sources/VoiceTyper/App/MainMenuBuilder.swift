import AppKit

/// 构建一个仅含标准「编辑」菜单的最小 `NSApp.mainMenu`。
///
/// 本应用是 `.accessory` 策略的菜单栏应用，没有 Dock 图标，日常不显示应用级菜单栏。
/// 但 AppKit 的 Cmd+C / Cmd+V / Cmd+X / Cmd+A / Cmd+Z 等编辑快捷键**不是**靠
/// `NSTextField` 自身处理的：它们通过 `NSWindow.performKeyEquivalent(with:)` 向上
/// 遍历响应链，最终匹配到主菜单里 `cut:`/`copy:`/`paste:`/`selectAll:`/`undo:`/`redo:`
/// 这些标准动作对应的菜单项——没有主菜单（或菜单里没有这些项）时，这些快捷键在任何
/// `NSTextField` / SwiftUI `TextField`（包括设置窗口里的 Base URL、API Key 等字段）
/// 里都会静默失效，但普通字符输入不受影响，因为那是走 `keyDown → insertText`，
/// 不经过这条 key-equivalent 匹配路径。这也是为什么现象是"能打字但不能粘贴"。
///
/// 不需要给这几个菜单项手写 target/action：AppKit 对 `cut:`/`copy:`/`paste:`/
/// `selectAll:`/`undo:`/`redo:` 这些标准编辑选择器有内置的响应链转发（First Responder），
/// 只要菜单项的 `action` 设置为对应 selector、`target` 留空，系统会自动路由到当前
/// 第一响应者（即正在编辑的文本字段），行为与任何标准 Mac 应用一致。
enum MainMenuBuilder {
    static func build() -> NSMenu {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        mainMenu.addItem(appMenuItem)
        let appMenu = NSMenu()
        appMenuItem.submenu = appMenu
        appMenu.addItem(withTitle: "退出 \(AppConstants.appName)", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")

        let editMenuItem = NSMenuItem()
        mainMenu.addItem(editMenuItem)
        let editMenu = NSMenu(title: "编辑")
        editMenuItem.submenu = editMenu

        editMenu.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "拷贝", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")

        return mainMenu
    }
}
