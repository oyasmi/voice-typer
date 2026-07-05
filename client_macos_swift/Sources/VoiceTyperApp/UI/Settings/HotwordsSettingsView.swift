import SwiftUI

struct HotwordsSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            Section {
                TextEditor(text: $vm.hotwordsText)
                    .font(.system(size: 13, design: .monospaced))
                    .autocorrectionDisabled(true)
                    .frame(minHeight: 300)
                    .onChange(of: vm.hotwordsText) { _, _ in
                        vm.hotwordsChanged()
                    }
            } header: {
                Text("编辑主热词文件")
            } footer: {
                Text(infoText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                HStack {
                    Text(vm.hotwordsMessage.isEmpty ? "每行一个词条，# 起始的行为注释。停止输入约 1 秒后自动保存。" : vm.hotwordsMessage)
                        .font(.callout)
                        .foregroundStyle(vm.hotwordsMessage.isEmpty ? Color.secondary : vm.hotwordsMessageKind.color)
                    Spacer()
                    Text("词条数 \(vm.hotwordCount)")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
    }

    private var infoText: String {
        if vm.additionalHotwordFileCount > 0 {
            return "这里编辑的是主热词文件。当前还有 \(vm.additionalHotwordFileCount) 个附加词库会继续参与加载，但不在此处编辑。"
        }
        return "这里编辑的是主热词文件。保存后会立即写回本地词库并重新加载。"
    }
}
