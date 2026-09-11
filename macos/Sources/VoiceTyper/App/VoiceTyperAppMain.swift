import AppKit

@main
enum VoiceTyperAppMain {
    @MainActor
    static func main() {
        // 菜单栏与主菜单在 AppDelegate 构造时就已经带着文案生成，语言必须在这之前定下来。
        L10n.bootstrap()
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        // 没有主菜单时，设置窗口里的文本字段无法响应 Cmd+V/C/X/A（见 MainMenuBuilder 注释）。
        app.mainMenu = MainMenuBuilder.build()
        withExtendedLifetime(delegate) {
            app.run()
        }
    }
}
