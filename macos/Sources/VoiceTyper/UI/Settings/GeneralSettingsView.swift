import SwiftUI

struct GeneralSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            HotkeySettingsView(vm: vm)

            Section("通用") {
                Toggle("开机自启", isOn: Binding(
                    get: { vm.launchAtLogin },
                    set: { vm.setLaunchAtLogin($0) }
                ))
                if !vm.generalMessage.isEmpty {
                    Text(vm.generalMessage)
                        .font(.caption)
                        .foregroundStyle(vm.generalMessageKind.color)
                }
            }

            Section {
                Picker("位置", selection: Binding(
                    get: { vm.hudPosition },
                    set: { vm.hudPosition = $0; vm.commitHUDPosition() }
                )) {
                    ForEach(HUDPosition.allCases, id: \.self) { position in
                        Text(position.displayName).tag(position)
                    }
                }
                .help("「跟随光标」把浮窗放在鼠标下方；「不显示」只隐藏录音与预览，出错时仍会提示。")

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
                // 位置设为"不显示"时透明度没有意义，也没有实时预览可看。
                .disabled(vm.hudPosition == .hidden)
            } header: {
                Text("悬浮窗")
            } footer: {
                Text(vm.hudPosition == .hidden
                     ? "已关闭浮窗。录音、实时预览与「已输入」确认都不再显示；识别失败、"
                       + "没有采到声音这类提示仍会浮出来——那些信息在别处看不到。"
                     : "拖动不透明度时会临时预览浮窗效果，松手后保存生效。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

        }
        .formStyle(.grouped)
    }
}
