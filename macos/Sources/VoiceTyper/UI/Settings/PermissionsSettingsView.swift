import SwiftUI

struct PermissionsSettingsView: View {
    let vm: SettingsViewModel

    var body: some View {
        Form {
            Section {
                ForEach(PermissionKind.allCases, id: \.self) { kind in
                    PermissionRow(
                        kind: kind,
                        status: vm.permissions.status(for: kind),
                        onRequest: { vm.onRequestPermission?(kind) },
                        onOpenSystemSettings: { vm.onOpenSystemSettings?(kind) }
                    )
                }
            } header: {
                Text("权限")
            }

            Section {
                summaryBanner
            }

            // 只在确实是 ad-hoc / 未签名的构建里出现；用 Developer ID 签名分发后自动消失。
            if CodeSigningInfo.mayLosePermissionsOnUpdate {
                Section {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: "info.circle")
                            .foregroundStyle(.secondary)
                        // 注意：这里是多个字符串字面量拼接出的 String，不是字面量，
                        // 因此 SwiftUI 不会按 Markdown 解析——不要在里面写 ** 之类的标记。
                        Text("当前这份 VoiceTyper 使用本机（ad-hoc）签名，没有稳定的开发者签名标识。"
                             + "系统的权限授权记录以代码签名为键，因此更新到新版本后，上面三项权限可能需要重新授权一次。"
                             + "这是未签名分发的固有限制，不是出了故障。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 0)
                    }
                    .padding(.vertical, 2)
                } header: {
                    Text("关于更新后重新授权")
                }
            }
        }
        .formStyle(.grouped)
    }

    private var summaryBanner: some View {
        let ready = vm.permissions.allRequiredGranted
        return HStack(alignment: .top, spacing: 10) {
            Image(systemName: ready ? "checkmark.seal.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(ready ? .green : .orange)
            VStack(alignment: .leading, spacing: 3) {
                Text(ready ? "权限检查通过" : "仍需完成授权")
                    .font(.system(size: 13, weight: .semibold))
                Text(ready
                     ? "权限已就绪；识别引擎的状态请查看「识别」页。"
                     : "请先完成上方未授权项，处理完成后本窗口会自动更新状态。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if !ready {
                Button("重新检测") { vm.onRetryReadinessCheck?() }
            }
        }
        .padding(.vertical, 2)
    }
}
