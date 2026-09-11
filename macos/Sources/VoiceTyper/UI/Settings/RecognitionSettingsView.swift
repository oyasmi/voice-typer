import SwiftUI

struct RecognitionSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            Section {
                modelCard
                Picker(L("空闲多久后卸载模型"), selection: Binding(
                    get: { vm.idleUnloadMinutes },
                    set: { vm.idleUnloadMinutes = $0; vm.commitIdleUnloadMinutes() }
                )) {
                    Text(L("5 分钟")).tag(5)
                    Text(L("10 分钟")).tag(10)
                    Text(L("30 分钟")).tag(30)
                    Text(L("从不")).tag(0)
                }

                Toggle(L("启动时预加载模型"), isOn: Binding(
                    get: { vm.preloadOnLaunch },
                    set: { vm.preloadOnLaunch = $0; vm.commitPreloadOnLaunch() }
                ))
                .help(L("关闭时首次按热键才加载模型，加载与录音并行，约 1 秒；开机自启的场景下能省下常驻内存。"))

                Picker(L("识别语言"), selection: $vm.language) {
                    ForEach(ASRLanguage.allCases, id: \.self) { lang in
                        Text(lang.displayName).tag(lang)
                    }
                }
                .help(L("SenseVoice 支持自动判断语种，也可指定为固定语言以提升准确率。"))
            } header: {
                Text(L("语音模型"))
            }

            Section {
                Toggle(L("启用智能校对"), isOn: $vm.llmEnabled)
                if vm.llmEnabled {
                    TextField("Base URL", text: $vm.llmBaseURL, prompt: Text("https://api.openai.com/v1"))
                    SecureField("API Key", text: $vm.llmAPIKey)
                    TextField(L("模型"), text: $vm.llmModel, prompt: Text("gpt-4o-mini"))
                    HStack {
                        Text(L("温度"))
                        Slider(value: $vm.llmTemperature, in: 0...1, step: 0.1)
                        Text(String(format: "%.1f", vm.llmTemperature))
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .frame(width: 28, alignment: .trailing)
                    }
                    HStack {
                        Text(L("超时（秒）"))
                        Spacer()
                        TextField("", value: $vm.llmTimeout, format: .number)
                            .frame(width: 60)
                            .multilineTextAlignment(.trailing)
                    }
                }

                HStack {
                    Spacer()
                    if vm.llmEnabled {
                        Button(L("测试校对")) { vm.testLLMCorrection() }
                    }
                    Button(L("保存并应用")) { vm.saveRecognitionSettings() }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.defaultAction)
                }
                .disabled(vm.recognitionBusy)

                if !vm.recognitionMessage.isEmpty {
                    Text(vm.recognitionMessage)
                        .font(.callout)
                        .foregroundStyle(vm.recognitionMessageKind.color)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            } header: {
                Text(L("智能校对"))
            } footer: {
                Text(L("用 OpenAI 兼容接口对识别结果做二次校对（修正同音错字、口语填充词等）。留空 Base URL 则不启用。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    @ViewBuilder
    private var modelCard: some View {
        switch vm.asrState {
        case .ready:
            HStack(spacing: 10) {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("SenseVoice-Small · int8 · 已就绪"))
                        .font(.system(size: 13, weight: .semibold))
                    Text(L("离线识别引擎已加载，可直接使用。"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button(L("重新加载")) { vm.reloadModel() }
                    .disabled(vm.modelActionBusy)
            }
        case .suspendedForIdle:
            HStack(spacing: 10) {
                Image(systemName: "moon.zzz").foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 2) {
                    // 这个状态既可能是空闲卸载后的，也可能是"启动时没有预加载"的初始态，
                    // 文案要对两种情况都成立，不能一口咬定"已卸载"。
                    Text(L("模型已就绪，引擎未常驻内存"))
                        .font(.system(size: 13, weight: .semibold))
                    Text(L("下次录音会自动加载（约 1 秒），无需手动操作。"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }
        case .loading:
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text(L("模型加载中…"))
                    .foregroundStyle(.secondary)
                Spacer()
            }
        case .modelMissing:
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(systemName: "arrow.down.circle").foregroundStyle(.orange)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(vm.downloadProgress == nil ? L("语音模型尚未就绪") : L("正在准备语音模型"))
                            .font(.system(size: 13, weight: .semibold))
                        Text(modelMissingDetail)
                            .font(.caption)
                            .foregroundStyle(vm.modelDownloadError == nil ? Color.secondary : Color.red)
                    }
                    Spacer()
                    if vm.downloadProgress == nil {
                        Button(vm.modelDownloadError == nil ? L("立即下载") : L("重试下载")) {
                            vm.startModelDownload()
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(vm.modelActionBusy)
                    }
                }
            }
        case .unloaded:
            HStack(spacing: 10) {
                Image(systemName: "clock").foregroundStyle(.secondary)
                Text(L("正在检查本地语音模型…"))
                    .foregroundStyle(.secondary)
                Spacer()
            }
        case .failed(let message):
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(L("模型加载失败"))
                            .font(.system(size: 13, weight: .semibold))
                        Text(message)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(L("重试")) { vm.reloadModel() }
                        .disabled(vm.modelActionBusy)
                }
            }
        }

        if let progress = vm.downloadProgress {
            VStack(alignment: .leading, spacing: 6) {
                ProgressView(value: progress)
                HStack {
                    Text(LF("下载中 %d%%", Int(progress * 100)))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button(L("取消")) { vm.cancelModelDownload() }
                        .font(.caption)
                }
            }
        }
    }

    private var modelMissingDetail: String {
        if let error = vm.modelDownloadError {
            return LF("自动下载失败：%@", error)
        }
        if vm.downloadProgress != nil {
            return L("SenseVoice-Small · 约 230 MB，完成后会自动校验、加载并进入离线可用状态。")
        }
        return L("SenseVoice-Small · 约 230 MB，下载并校验完成后可完全离线使用。")
    }
}
