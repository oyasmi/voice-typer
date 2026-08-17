import SwiftUI

struct GeneralSettingsView: View {
    let vm: SettingsViewModel

    var body: some View {
        Form {
            Section("通用") {
                Toggle("开机自启", isOn: Binding(
                    get: { vm.launchAtLogin },
                    set: { vm.setLaunchAtLogin($0) }
                ))
            }

            Section {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("HUD 背景不透明度")
                        Spacer()
                        Text(String(format: "%.0f%%", vm.hudOpacity * 100))
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                    Slider(
                        value: Binding(
                            get: { vm.hudOpacity },
                            set: { vm.hudOpacityPreview($0) }
                        ),
                        in: 0.5...1.0,
                        onEditingChanged: { editing in
                            if !editing { vm.commitHUDOpacity() }
                        }
                    )
                }
            } header: {
                Text("悬浮窗")
            } footer: {
                Text("拖动时会临时预览 HUD 背景效果，松手后保存生效。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}
