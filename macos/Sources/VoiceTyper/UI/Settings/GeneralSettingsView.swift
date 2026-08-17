import SwiftUI

struct GeneralSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
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

            Section {
                Picker("空闲多久后卸载模型", selection: Binding(
                    get: { vm.idleUnloadMinutes },
                    set: { vm.idleUnloadMinutes = $0; vm.commitIdleUnloadMinutes() }
                )) {
                    Text("5 分钟").tag(5)
                    Text("10 分钟").tag(10)
                    Text("30 分钟").tag(30)
                    Text("从不").tag(0)
                }
            } header: {
                Text("识别引擎")
            } footer: {
                Text("SenseVoice 常驻内存约 500MB。空闲达到设定时长后自动释放；下次按热键会与录音并行自动重新加载（约 1 秒），首句预览会稍晚出现，不影响最终识别结果。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}
