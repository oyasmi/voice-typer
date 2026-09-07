import SwiftUI

/// 热键配置现嵌入「通用」页，不再占用独立 tab。
struct HotkeySettingsView: View {
    let vm: SettingsViewModel

    var body: some View {
        Section {
            HStack {
                Text("快捷键")
                Spacer()
                HotkeyRecorder(
                    config: vm.hotkeyConfig,
                    onBegin: { vm.beginHotkeyRecording() },
                    onCapture: { vm.applyRecordedHotkey($0) },
                    onCancel: { vm.cancelHotkeyRecording() }
                )
                .frame(width: 240, height: 30)
            }
            Button("使用 Fn🌐（推荐）") { vm.resetHotkeyToFn() }

            Picker("触发方式", selection: Binding(
                get: { vm.hotkeyConfig.mode },
                set: { vm.applyHotkeyMode($0) }
            )) {
                ForEach(HotkeyMode.allCases, id: \.self) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .help("长段口述（写邮件、写文档）时不必一直按住热键，可以改成按一次开始、再按一次结束。")
        } header: {
            Text("热键")
        } footer: {
            Text("点击右侧输入框后按下想要的快捷键即可捕获。推荐使用 Fn；也可设为组合键，如 ⌃⌥Space。"
                 + "录制期间全局热键会临时暂停。无论哪种触发方式，录音中和识别中都可以按 Esc 取消。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        // Fn 与系统「按下🌐键」的默认行为冲突是新用户最容易撞上、又最难自己定位的问题：
        // 表现是"一说话就弹表情面板 / 输入法被切走"，看上去完全像 VoiceTyper 的 bug。
        if let warning = vm.fnConflictWarning {
            Section {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                        Text(warning)
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 0)
                    }
                    HStack(spacing: 8) {
                        Spacer()
                        Button("打开键盘设置") { vm.onOpenKeyboardSettings?() }
                            .buttonStyle(.borderedProminent)
                        Button("已改好，重新检测") { vm.refreshFnConflictWarning() }
                    }
                }
            }
        }

        if !vm.hotkeyMessage.isEmpty {
            Section {
                Text(vm.hotkeyMessage)
                    .font(.callout)
                    .foregroundStyle(vm.hotkeyMessageKind.color)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}
