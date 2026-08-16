import AppKit

@main
enum VoiceTyperAppMain {
    @MainActor
    static func main() {
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
