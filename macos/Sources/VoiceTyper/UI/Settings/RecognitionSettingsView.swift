import SwiftUI

struct RecognitionSettingsView: View {
    @Bindable var vm: SettingsViewModel

    var body: some View {
        Form {
            Section("语音模型") {
                modelCard
            }

            Section("识别语言") {
                Picker("语言", selection: $vm.language) {
                    ForEach(ASRLanguage.allCases, id: \.self) { lang in
                        Text(lang.displayName).tag(lang)
                    }
                }
                .help("SenseVoice 支持自动判断语种，也可指定为固定语言以提升准确率。")
            }

            Section {
                Toggle("启用智能校对", isOn: $vm.llmEnabled)
                if vm.llmEnabled {
                    TextField("Base URL", text: $vm.llmBaseURL, prompt: Text("https://api.openai.com/v1"))
                    SecureField("API Key", text: $vm.llmAPIKey)
                    TextField("模型", text: $vm.llmModel, prompt: Text("gpt-4o-mini"))
                    HStack {
                        Text("温度")
                        Slider(value: $vm.llmTemperature, in: 0...1, step: 0.1)
                        Text(String(format: "%.1f", vm.llmTemperature))
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .frame(width: 28, alignment: .trailing)
                    }
                    HStack {
                        Text("超时（秒）")
                        Spacer()
                        TextField("", value: $vm.llmTimeout, format: .number)
                            .frame(width: 60)
                            .multilineTextAlignment(.trailing)
                    }
                }
            } header: {
                Text("智能校对")
            } footer: {
                Text("用 OpenAI 兼容接口对识别结果做二次校对（修正同音错字、口语填充词等）。留空 Base URL 则不启用。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                HStack {
                    Spacer()
                    if vm.llmEnabled {
                        Button("测试校对") { vm.testLLMCorrection() }
                    }
                    Button("保存并应用") { vm.saveRecognitionSettings() }
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
                    Text("SenseVoice-Small · int8 · 已就绪")
                        .font(.system(size: 13, weight: .semibold))
                    Text("离线识别引擎已加载，可直接使用。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("重新加载") { vm.reloadModel() }
                    .disabled(vm.modelActionBusy)
            }
        case .suspendedForIdle:
            HStack(spacing: 10) {
                Image(systemName: "moon.zzz").foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text("引擎已空闲卸载")
                        .font(.system(size: 13, weight: .semibold))
                    Text("下次录音会自动重新加载，无需手动操作。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }
        case .loading:
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text("模型加载中…")
                    .foregroundStyle(.secondary)
                Spacer()
            }
        case .modelMissing:
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(systemName: "arrow.down.circle").foregroundStyle(.orange)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("需要下载语音模型")
                            .font(.system(size: 13, weight: .semibold))
                        Text("SenseVoice-Small · 约 230 MB，下载一次后完全离线可用。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("开始下载") { vm.startModelDownload() }
                        .buttonStyle(.borderedProminent)
                        .disabled(vm.modelActionBusy)
                }
            }
        case .unloaded:
            HStack(spacing: 10) {
                Image(systemName: "clock").foregroundStyle(.secondary)
                Text("等待权限就绪后自动加载模型…")
                    .foregroundStyle(.secondary)
                Spacer()
            }
        case .failed(let message):
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("模型加载失败")
                            .font(.system(size: 13, weight: .semibold))
                        Text(message)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("重试") { vm.reloadModel() }
                        .disabled(vm.modelActionBusy)
                }
            }
        }

        if let progress = vm.downloadProgress {
            VStack(alignment: .leading, spacing: 6) {
                ProgressView(value: progress)
                HStack {
                    Text("下载中 \(Int(progress * 100))%")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("取消") { vm.cancelModelDownload() }
                        .font(.caption)
                }
            }
        }
    }
}
