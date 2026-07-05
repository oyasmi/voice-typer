import SwiftUI

struct ConnectionSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            Section("服务连接") {
                Picker("协议", selection: $vm.scheme) {
                    Text("http").tag("http")
                    Text("https").tag("https")
                }
                .help("选择 https 时 WebSocket 自动改用 wss")

                TextField("服务地址", text: $vm.host, prompt: Text("127.0.0.1"))
                TextField("端口", text: $vm.port, prompt: Text("6008"))
                SecureField("API Key", text: $vm.apiKey, prompt: Text("可选"))
            }

            Section {
                Toggle("流式识别（推荐，低延迟）", isOn: $vm.streaming)
                Toggle("启用 LLM 纠错", isOn: $vm.llmRecorrect)
            } footer: {
                Text("流式模式通过 WebSocket 实时回传识别结果，延迟更低；非流式模式支持热词，兼容旧服务端。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                HStack {
                    Spacer()
                    Button("测试连接") { vm.testConnection() }
                    Button("保存并应用") { vm.saveConnection() }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.defaultAction)
                }
                .disabled(vm.connectionBusy)

                if !vm.connectionMessage.isEmpty {
                    Text(vm.connectionMessage)
                        .font(.callout)
                        .foregroundStyle(vm.connectionMessageKind.color)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .formStyle(.grouped)
    }
}
