# VoiceTyper

**简体中文** | [English](README.en.md)

[![Release](https://img.shields.io/github/v/release/oyasmi/voice-typer)](https://github.com/oyasmi/voice-typer/releases)
[![License](https://img.shields.io/github/license/oyasmi/voice-typer)](LICENSE)
![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-lightgrey)

**按住热键说话，松开，文字就出现在光标处。** 识别在本机完成，录音不出电脑。

VoiceTyper 安静地待在菜单栏或系统托盘里。回消息、写文档、记下刚冒出来的想法——不用打开网页，
不用上传录音，也不用在转写结果和当前窗口之间来回复制粘贴。它不替换你的输入法，任何能输入文字的
地方都能用。

## 下载

| 平台 | 系统要求 | 默认热键 | |
| --- | --- | --- | --- |
| **macOS** | Apple Silicon，macOS 14+ | `Fn` | [下载](https://github.com/oyasmi/voice-typer/releases) · [使用说明](macos/README.md) |
| **Windows** | Windows 10 (1809+) / 11，x64 或 arm64，需 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) | 右 `Ctrl` | [下载](https://github.com/oyasmi/voice-typer/releases) · [使用说明](windows/README.md) |
| Linux / Intel Mac | 暂无一体化版本 | — | 可使用[客户端—服务端版本](client-server/README.md) |

## 特点

- **随处可用**：聊天、浏览器、文档、编辑器……光标在哪里，文字就写到哪里。
- **边说边看**：浮窗实时显示识别结果，听到更多上下文后会自动修正前面的字。
- **拿来就能发**：自动加标点，把口述数字整理成书写形式（「六十四兆」→「64兆」），中英文之间自动加空格。
- **本地、离线、免费**：基于 SenseVoice 模型在本机识别，无需账号，没有次数限制。
- **多语言**：中文、英文、粤语、日语、韩语，可自动判断，中英混说不用切换。
- **不打扰**：不抢焦点；按 `Esc` 随时取消；说过的话可以从菜单里找回。
- **可选 AI 校对**：接入任意 OpenAI 兼容服务修正同音字和口误，默认关闭。

## 快速上手

1. 下载并安装对应平台的应用（详见各平台使用说明）。
2. 第一次打开时，跟着四步引导完成准备：macOS 需要授予麦克风、输入监控和辅助功能三项权限；Windows 只需
   确认麦克风可用。约 240MB 的语音模型会在后台自动下载，只需下载一次。引导最后会让你真正试一次听写。
3. 之后每次使用：
   - 把光标放在要输入的位置；
   - 按住热键说话，浮窗里实时显示识别结果；
   - 松开热键，文字自动写入。

短于 0.3 秒的按键会被当作误触忽略。说长段内容时，可以把触发方式改成「按一次开始、再按一次结束」。

## 隐私

语音往往比最终写下的文字包含更多私人信息，所以本地识别是 VoiceTyper 的设计前提，而不是附加卖点。

- 录音只在本机内存中处理，不会上传到任何服务器；
- 模型下载完成后可以完全断网使用；
- 日志不记录识别出的文字；
- AI 校对默认关闭。开启后只发送**识别出的文字**到你自己配置的服务，音频始终不发送；API Key 保存在
  macOS 钥匙串或 Windows DPAPI 加密存储中。想完全离线，可以接入本机运行的模型。

## 常见问题

### 它是输入法吗？

不是。VoiceTyper 不替换系统输入法，只是把识别结果写入当前的输入框，原来的打字习惯不受影响。

### 每次使用都需要联网吗？

不需要。只有首次下载模型、手动检查更新，以及你主动开启 AI 校对时才会联网。

### 会占用很多内存吗？

默认启动时加载模型并常驻，这样按下热键就能立刻开始，代价是常驻数百 MB 内存（macOS 实测约 500MB）。
不常用的话，可以在设置里关闭预加载、或设置空闲后自动释放，下次按热键时会一边录音一边加载。

### 哪些地方用不了？

大多数能粘贴文字的地方都能用。部分密码框、游戏、远程桌面和以管理员身份运行的窗口会拒绝模拟输入，
这时结果会留在剪贴板里，也可以从菜单里找回，手动粘贴即可。

### 为什么首次打开会被系统拦截？

安装包目前没有 Apple 公证或 Windows 代码签名。macOS 上到「系统设置 → 隐私与安全性」点「仍要打开」；
Windows 上点「更多信息 → 仍要运行」。只需一次。

### 可以换模型或添加热词吗？

一体化应用专注于 SenseVoice-Small，不支持更换模型，也没有热词功能——清晰稳定的默认体验优先于大量
未打磨的选项。需要其他模型可以使用[客户端—服务端版本](client-server/README.md)。

### Windows 版稳定吗？

功能与 macOS 版对等，已在 Windows 真机上跑通听写与模型下载；arm64 设备、长时间运行和多显示器 DPI 等场景
仍在系统性验证中。遇到问题欢迎[提交 Issue](https://github.com/oyasmi/voice-typer/issues)。

## 参与开发

```text
voice-typer/
├── macos/           # macOS 应用：Swift + AppKit/SwiftUI + ONNX Runtime
├── windows/         # Windows 应用：.NET 10 + WinForms + ONNX Runtime
└── client-server/   # 旧的客户端—服务端架构，用于 Linux、远程与共享服务端场景
```

两个平台共享同一套产品架构与识别管线，用同一套金标准夹具校验数值一致性。构建、测试与架构说明：

- macOS：[开发说明](macos/DEVELOPMENT.md) · [设计文档](macos/DESIGN.md) · [更新日志](macos/CHANGELOG.md)
- Windows：[开发说明](windows/DEVELOPMENT.md) · [设计文档](windows/DESIGN.md) · [变更记录](windows/CHANGELOG.md)
- 客户端—服务端：[说明](client-server/README.md) · [通信协议](client-server/PROTOCOL.md)

欢迎通过 [Issues](https://github.com/oyasmi/voice-typer/issues) 反馈问题或提出建议。

## 致谢

本地识别能力建立在 [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) 与
[ONNX Runtime](https://github.com/microsoft/onnxruntime) 之上，感谢这些项目让高质量的离线语音识别能够走进
普通桌面应用。

## 许可证

[Apache License 2.0](LICENSE)。第三方组件（SenseVoice 模型、ONNX Runtime 等）遵循各自的许可证。
