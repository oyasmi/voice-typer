import SwiftUI

struct GeneralSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            HotkeySettingsView(vm: vm)

            Section(L("通用")) {
                Toggle(L("开机自启"), isOn: Binding(
                    get: { vm.launchAtLogin },
                    set: { vm.setLaunchAtLogin($0) }
                ))

                // 语言切换只重建一部分界面是做不到的：菜单栏、主菜单、已经构造好的窗口
                // 都带着启动时的文案。与其做半套实时刷新，不如明确要求重启（footer 已注明）。
                Picker(L("界面语言"), selection: Binding(
                    get: { vm.interfaceLanguage },
                    set: { vm.interfaceLanguage = $0; vm.commitInterfaceLanguage() }
                )) {
                    ForEach(AppLanguage.allCases, id: \.self) { language in
                        Text(language.displayName).tag(language)
                    }
                }
                .help(L("切换后请重启 VoiceTyper，界面文本才会全部改用新语言。"))
                Text(L("界面语言切换后需要重启 VoiceTyper 才会全面生效。识别语言在「识别」页单独设置。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if !vm.generalMessage.isEmpty {
                    Text(vm.generalMessage)
                        .font(.caption)
                        .foregroundStyle(vm.generalMessageKind.color)
                }
            }

            Section {
                Picker(L("位置"), selection: Binding(
                    get: { vm.hudPosition },
                    set: { vm.hudPosition = $0; vm.commitHUDPosition() }
                )) {
                    ForEach(HUDPosition.allCases, id: \.self) { position in
                        Text(position.displayName).tag(position)
                    }
                }
                .help(L("「跟随光标」把浮窗放在鼠标下方；「不显示」只隐藏录音与预览，出错时仍会提示。"))

                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(L("HUD 背景不透明度"))
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
                        in: 0.4...1.0,
                        onEditingChanged: { editing in
                            if !editing { vm.commitHUDOpacity() }
                        }
                    )
                }
                // 位置设为"不显示"时透明度没有意义，也没有实时预览可看。
                .disabled(vm.hudPosition == .hidden)
            } header: {
                Text(L("悬浮窗"))
            } footer: {
                Text(vm.hudPosition == .hidden
                     ? L("已关闭浮窗。录音、实时预览与「已输入」确认都不再显示；识别失败、没有采到声音这类提示仍会浮出来——那些信息在别处看不到。")
                     : L("拖动不透明度时会临时预览浮窗效果，松手后保存生效。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

        }
        .formStyle(.grouped)
    }
}
