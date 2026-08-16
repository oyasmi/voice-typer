import SwiftUI

struct HotkeySettingsView: View {
    let vm: SettingsViewModel

    var body: some View {
        Form {
            Section {
                HStack {
                    Text("快捷键")
                    Spacer()
                    HotkeyRecorder(
                        config: vm.hotkeyConfig,
                        onBegin: { vm.beginHotkeyRecording() },
                        onCapture: { vm.applyHotkey($0) },
                        onCancel: { vm.cancelHotkeyRecording() }
                    )
                    .frame(width: 240, height: 30)
                }
                Button("使用 Fn🌐（推荐）") { vm.resetHotkeyToFn() }
            } header: {
                Text("热键")
            } footer: {
                Text("点击右侧输入框后按下想要的快捷键即可捕获。推荐使用 Fn；也可设为组合键，如 ⌃⌥Space。录制期间全局热键会临时暂停。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
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
        .formStyle(.grouped)
    }
}
