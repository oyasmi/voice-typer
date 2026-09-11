import SwiftUI

/// 提示框里的一个按钮。用普通值而不是 `@ViewBuilder` 泛型参数，省掉"这次到底传没传
/// 按钮"的类型体操，调用处也更好读。
struct OnboardingCalloutAction: Identifiable {
    let id = UUID()
    let title: String
    var isProminent: Bool = false
    let run: () -> Void
}

/// 首启引导。四步：欢迎 → 系统权限 → 语音模型 → 试一试。
///
/// 最后一步是重点：让用户在**引导窗口自己的输入框里**真正完成一次听写。
/// 只有这一步能同时验证「热键 → 麦克风 → 识别 → 文本插入」四个环节，
/// 并在失败时告诉用户断在哪里——而不是等用户回到自己的工作场景里撞上"按了没反应"。
struct OnboardingView: View {
    @Bindable var vm: OnboardingViewModel
    @FocusState private var trialFieldFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                content
                    .padding(.horizontal, 28)
                    .padding(.vertical, 22)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            footer
        }
        .onChange(of: vm.step) { _, newStep in
            trialFieldFocused = (newStep == .trial)
        }
        // 用户可能在模型还没下载完时就翻到了「试一试」：那时输入框是禁用的，聚焦不会生效。
        // 等它真正可用了再补一次聚焦，省掉一次"为什么打不进去"的困惑。
        .onChange(of: vm.canRunTrial) { _, ready in
            if ready, vm.step == .trial {
                trialFieldFocused = true
            }
        }
    }

    // MARK: - 头部 / 底部

    private var header: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(AppConstants.appName) · \(vm.step.title)")
                    .font(.system(size: 15, weight: .semibold))
                Text(LF("第 %d 步 / 共 %d 步", vm.step.rawValue + 1, OnboardingStep.allCases.count))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 6) {
                ForEach(OnboardingStep.allCases, id: \.rawValue) { step in
                    Circle()
                        .fill(step.rawValue <= vm.step.rawValue ? Color.accentColor : Color.secondary.opacity(0.3))
                        .frame(width: 7, height: 7)
                }
            }
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 14)
    }

    @ViewBuilder
    private var footer: some View {
        HStack {
            if vm.canGoBack {
                Button(L("上一步")) { vm.goBack() }
            }
            Spacer()
            if vm.step == .trial, case .succeeded = vm.trial {
                Button(L("完成")) { vm.finish() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            } else if vm.step == .trial {
                // 试用没成功也允许离开：引导不该把人困住。用「先跳过」而不是「完成」，
                // 措辞上明确这次没验证通过。
                Button(vm.primaryActionTitle) { vm.goForward() }
                    .buttonStyle(.bordered)
            } else {
                Button(vm.primaryActionTitle) { vm.goForward() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 14)
    }

    @ViewBuilder
    private var content: some View {
        switch vm.step {
        case .welcome: welcomeStep
        case .permissions: permissionsStep
        case .model: modelStep
        case .trial: trialStep
        }
    }

    // MARK: - ① 欢迎

    private var welcomeStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 6) {
                Text(LF("按住 %@ 说话，松开就上屏", vm.hotkeyDisplay))
                    .font(.system(size: 20, weight: .semibold))
                Text(L("识别完全在这台 Mac 上完成，音频不会离开设备，也不需要联网。"))
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 12) {
                bullet(
                    "keyboard",
                    L("按住热键"),
                    LF("默认是 %@。之后可以在「设置 → 通用」里换成组合键，或改成「按一次开始、再按一次结束」，方便长段口述。", vm.hotkeyDisplay)
                )
                bullet(
                    "waveform.circle",
                    L("看到浮窗再开口"),
                    L("按下热键后麦克风还需要约 0.2 秒才真正开始出声。等屏幕下方的浮窗出现再说话，开头的字就不会被吞掉。")
                )
                bullet(
                    "escape",
                    L("说错了就按 Esc"),
                    L("录音中和识别中都可以按 Esc 取消，这次的结果不会被插入到任何地方。")
                )
            }

            if let warning = vm.fnConflictWarning {
                calloutBox(
                    symbol: "exclamationmark.triangle.fill",
                    tint: .orange,
                    title: L("先改一个系统设置，否则按 Fn 会同时触发系统功能"),
                    detail: warning,
                    actions: [
                        OnboardingCalloutAction(title: L("打开键盘设置"), isProminent: true) {
                            vm.onOpenKeyboardSettings?()
                        },
                        OnboardingCalloutAction(title: L("已改好，重新检测")) {
                            vm.onRefreshStatus?()
                        },
                    ]
                )
            }

            if CodeSigningInfo.mayLosePermissionsOnUpdate {
                calloutBox(
                    symbol: "info.circle",
                    tint: .secondary,
                    title: L("关于以后更新"),
                    detail: L("这份 VoiceTyper 使用本机（ad-hoc）签名。系统的权限记录以代码签名为键，所以更新到新版本后，下一步的三项权限可能需要重新授权一次。这是未签名分发的固有限制。")
                )
            }
        }
    }

    // MARK: - ② 权限

    private var permissionsStep: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text(LF("已完成 %d / %d 项", vm.grantedPermissionCount, PermissionKind.allCases.count))
                    .font(.system(size: 17, weight: .semibold))
                ProgressView(
                    value: Double(vm.grantedPermissionCount),
                    total: Double(PermissionKind.allCases.count)
                )
                Text(L("三项缺一不可。点「授权」，系统会弹出授权窗口或跳到对应设置页；「输入监控」通常不会自动弹窗，需要在系统设置里手动打开开关。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            VStack(spacing: 4) {
                ForEach(PermissionKind.allCases, id: \.self) { kind in
                    PermissionRow(
                        kind: kind,
                        status: vm.permissions.status(for: kind),
                        onRequest: { vm.onRequestPermission?(kind) },
                        onOpenSystemSettings: { vm.onOpenSystemSettings?(kind) }
                    )
                    if kind != PermissionKind.allCases.last {
                        Divider()
                    }
                }
            }
            .padding(12)
            .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))

            HStack(spacing: 10) {
                if vm.permissions.allRequiredGranted {
                    Label(L("权限已齐全"), systemImage: "checkmark.seal.fill")
                        .foregroundStyle(.green)
                } else {
                    Text(L("授权完成后本页会自动更新；也可以手动重新检测。"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button(L("重新检测")) { vm.onRefreshStatus?() }
            }
        }
    }

    // MARK: - ③ 模型

    private var modelStep: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text(L("语音模型"))
                    .font(.system(size: 17, weight: .semibold))
                Text(L("SenseVoice-Small，约 230 MB，只需下载这一次。下载与上一步的授权是并行的，不必等它结束再去授权。完成后 VoiceTyper 完全离线可用。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            VStack(alignment: .leading, spacing: 10) {
                modelStatusCard
            }
            .padding(12)
            .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
        }
    }

    @ViewBuilder
    private var modelStatusCard: some View {
        if vm.isModelReady {
            statusLine("checkmark.circle.fill", .green, L("模型已就绪"), L("离线识别引擎可以直接使用。"))
        } else if let progress = vm.downloadProgress {
            statusLine("arrow.down.circle", .accentColor, LF("正在下载 %d%%", Int(progress * 100)),
                       L("支持断点续传，取消后可以随时继续。"))
            ProgressView(value: progress)
            HStack {
                Spacer()
                Button(L("取消")) { vm.onCancelModelDownload?() }
            }
        } else if case .failed(let message) = vm.asrState {
            statusLine("exclamationmark.triangle.fill", .red, L("模型加载失败"), message)
            // 这里接的是"重新下载"而不是"重新加载"，是刻意的：`downloadAll` 会先按 sha256
            // 校验已有文件，文件完好就直接跳过、随后照样触发一次 `asrService.reload()`。
            // 于是同一个按钮既能修"文件损坏"也能修"只是加载失败"，引导里不必让用户去分辨
            // 自己遇到的是哪一种。
            trailingButton(L("重试")) { vm.onStartModelDownload?() }
        } else if let error = vm.modelDownloadError {
            statusLine("exclamationmark.triangle.fill", .red, L("下载失败"), error)
            trailingButton(L("重试下载")) { vm.onStartModelDownload?() }
        } else if vm.asrState == .modelMissing {
            statusLine("arrow.down.circle", .orange, L("尚未下载"), L("点右侧按钮开始下载。"))
            trailingButton(L("开始下载")) { vm.onStartModelDownload?() }
        } else {
            statusLine("clock", .secondary, L("正在准备…"), L("正在检查本地模型并加载识别引擎。"))
        }
    }

    // MARK: - ④ 试一试

    private var trialStep: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text(L("在下面的输入框里试一次"))
                    .font(.system(size: 17, weight: .semibold))
                Text(vm.hotkeyMode == .hold
                     ? LF("把光标放进输入框，按住 %@ 说一句话，然后松开。", vm.hotkeyDisplay)
                     : LF("把光标放进输入框，按一下 %@ 开始说话，说完再按一下结束。", vm.hotkeyDisplay))
                    .foregroundStyle(.secondary)
                Text(L("这一步会真正跑一遍「热键 → 麦克风 → 本地识别 → 文本插入」，任何一环有问题都会在下面直接指出来。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            TextField(L("识别结果会出现在这里"), text: $vm.trialText, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 14))
                .lineLimit(2...4)
                .focused($trialFieldFocused)
                .disabled(!vm.canRunTrial)

            if let hint = vm.trialBlockingHint {
                calloutBox(
                    symbol: "exclamationmark.triangle.fill",
                    tint: .orange,
                    title: L("还不能试"),
                    detail: hint
                )
            } else if let summary = vm.trialSummary {
                calloutBox(
                    symbol: summary.symbol,
                    tint: summary.tint,
                    title: summary.title,
                    detail: summary.detail,
                    actions: retryActions
                )
            }

            DisclosureGroup(L("按下热键完全没反应？")) {
                VStack(alignment: .leading, spacing: 8) {
                    troubleshooting(L("「输入监控」权限没给全——回到上一步确认三项都是绿色。"))
                    troubleshooting(L("热键被别的应用抢走了（如 Spotlight ⌘Space、输入法切换 ⌃Space）；可以在「设置 → 通用」里换一个热键。"))
                    troubleshooting(L("刚更新过版本：本机签名的构建在更新后可能需要重新授权，先到「系统设置 → 隐私与安全性」把 VoiceTyper 移除再重新添加。"))
                }
                .padding(.top, 6)
            }
            .font(.callout)
        }
    }

    /// 只有"还没成功"的结果才提供「再试一次」。
    private var retryActions: [OnboardingCalloutAction] {
        switch vm.trial {
        case .succeeded, .idle, .recording, .recognizing:
            return []
        case .noSpeech, .cancelled, .failed, .blocked:
            return [OnboardingCalloutAction(title: L("再试一次")) {
                vm.resetTrial()
                vm.trialText = ""
                trialFieldFocused = true
            }]
        }
    }

    // MARK: - 小组件

    private func bullet(_ symbol: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 16))
                .foregroundStyle(Color.accentColor)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }

    private func statusLine(_ symbol: String, _ tint: Color, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol).foregroundStyle(tint)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }

    private func trailingButton(_ title: String, run: @escaping () -> Void) -> some View {
        HStack {
            Spacer()
            Button(title, action: run)
                .buttonStyle(.borderedProminent)
        }
    }

    private func calloutBox(
        symbol: String,
        tint: Color,
        title: String,
        detail: String,
        actions: [OnboardingCalloutAction] = []
    ) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            statusLine(symbol, tint, title, detail)
            if !actions.isEmpty {
                HStack(spacing: 8) {
                    Spacer()
                    ForEach(actions) { action in
                        if action.isProminent {
                            Button(action.title, action: action.run)
                                .buttonStyle(.borderedProminent)
                        } else {
                            Button(action.title, action: action.run)
                        }
                    }
                }
            }
        }
        .padding(12)
        .background(tint.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
    }

    private func troubleshooting(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text("·").foregroundStyle(.secondary)
            Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }
}
