import SwiftUI

/// 单项权限的状态行。设置窗口「权限」页与首启引导共用，保证两处的措辞、图标、
/// 按钮位置完全一致——用户在引导里学会的东西，回到设置页应该原样认得出来。
struct PermissionRow: View {
    let kind: PermissionKind
    let status: PermissionStatus
    let onRequest: () -> Void
    let onOpenSystemSettings: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .foregroundStyle(color)
                .font(.system(size: 15))
            VStack(alignment: .leading, spacing: 2) {
                Text(kind.title)
                    .font(.system(size: 13, weight: .semibold))
                Text("\(kind.purpose) · \(status.displayText)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if status != .authorized {
                Button(L("授权"), action: onRequest)
                    .buttonStyle(.borderedProminent)
                Button(L("系统设置"), action: onOpenSystemSettings)
            }
        }
        .padding(.vertical, 2)
    }

    private var symbol: String {
        switch status {
        case .authorized: return "checkmark.circle.fill"
        case .denied: return "xmark.circle.fill"
        case .notDetermined: return "exclamationmark.circle.fill"
        }
    }

    private var color: Color {
        switch status {
        case .authorized: return .green
        case .denied: return .red
        case .notDetermined: return .orange
        }
    }
}
