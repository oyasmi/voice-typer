import SwiftUI

struct PermissionsSettingsView: View {
    let vm: SettingsViewModel

    var body: some View {
        Form {
            Section {
                ForEach(PermissionKind.allCases, id: \.self) { kind in
                    permissionRow(kind)
                }
            } header: {
                Text("权限")
            } footer: {
                Text("VoiceTyper 需要以下权限：麦克风用于录音，辅助功能用于插入文本，输入监控用于全局热键（尤其是 Fn 键）。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("服务连接") {
                HStack(spacing: 8) {
                    Image(systemName: vm.serviceReady ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .foregroundStyle(vm.serviceReady ? .green : .red)
                    Text(vm.serverStatus)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("重试连接") { vm.onRetryServerCheck?() }
                }
            }

            Section {
                summaryBanner
            }
        }
        .formStyle(.grouped)
    }

    @ViewBuilder
    private func permissionRow(_ kind: PermissionKind) -> some View {
        let status = vm.permissions.status(for: kind)
        HStack(spacing: 10) {
            Image(systemName: symbol(for: status))
                .foregroundStyle(color(for: status))
                .font(.system(size: 15))
            VStack(alignment: .leading, spacing: 2) {
                Text(kind.title)
                    .font(.system(size: 13, weight: .semibold))
                Text(status.displayText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if status != .authorized {
                Button("授权") { vm.onRequestPermission?(kind) }
                    .buttonStyle(.borderedProminent)
                Button("系统设置") { vm.onOpenSystemSettings?(kind) }
            }
        }
        .padding(.vertical, 2)
    }

    private var summaryBanner: some View {
        let ready = vm.permissions.allRequiredGranted && vm.serviceReady
        return HStack(alignment: .top, spacing: 10) {
            Image(systemName: ready ? "checkmark.seal.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(ready ? .green : .orange)
            VStack(alignment: .leading, spacing: 3) {
                Text(ready ? "全部检查通过" : "仍需完成检查项")
                    .font(.system(size: 13, weight: .semibold))
                Text(ready
                     ? "权限和服务连接都已就绪，现在可以直接关闭本窗口并开始使用。"
                     : "请先完成未授权项或修复服务连接问题，处理完成后本窗口会自动更新状态。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
        }
        .padding(.vertical, 2)
    }

    private func symbol(for status: PermissionStatus) -> String {
        switch status {
        case .authorized: return "checkmark.circle.fill"
        case .denied: return "xmark.circle.fill"
        case .notDetermined: return "exclamationmark.circle.fill"
        }
    }

    private func color(for status: PermissionStatus) -> Color {
        switch status {
        case .authorized: return .green
        case .denied: return .red
        case .notDetermined: return .orange
        }
    }
}
