<div align="center">

# VoiceTyper

**按住热键说话，松开，文字就落在光标处。**

本地离线的桌面语音输入工具 · 录音不出电脑 · 不替换输入法 · 打开就能用

[![Release](https://img.shields.io/github/v/release/oyasmi/voice-typer)](https://github.com/oyasmi/voice-typer/releases)
[![License](https://img.shields.io/github/license/oyasmi/voice-typer)](LICENSE)
![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-blue)
![ASR](https://img.shields.io/badge/ASR-100%25%20Local%20Offline-success)

**简体中文** · [English](README.en.md)

[下载安装](#下载安装) · [核心体验](#核心体验) · [三步上手](#三步上手) · [隐私承诺](#隐私承诺) · [常见问题](#常见问题) · [参与开发](#参与开发)

</div>

---

VoiceTyper 安静地常驻在 macOS 菜单栏或 Windows 系统托盘里。回消息、写文档、改代码注释、随手记下刚冒出来的念头——不用打开网页，不用上传录音，也不用在转写窗口和当前应用之间来回复制粘贴。它不接管系统输入法，**任何能打字的地方都能直接说**。

## 下载安装

| 平台 | 系统要求 | 默认热键 | 获取与指南 |
| --- | --- | --- | --- |
| **macOS** | Apple Silicon（M 系列），macOS 14+ | <kbd>Fn</kbd> | [下载 DMG](https://github.com/oyasmi/voice-typer/releases) · [macOS 使用说明](macos/README.md) |
| **Windows** | Windows 10 (1809+) / 11，x64 或 arm64<br>需安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) | 右 <kbd>Ctrl</kbd> | [下载安装包 / 便携版](https://github.com/oyasmi/voice-typer/releases) · [Windows 使用说明](windows/README.md) |
| **Linux / Intel Mac** | 暂无本地一体化版本 | — | 可使用[客户端—服务端版本](client-server/README.md) |

> [!TIP]
> 安装包尚未进行 Apple 公证或 Windows 代码签名。首次打开如遇系统拦截：macOS 请前往「系统设置 → 隐私与安全性」点击 **仍要打开**；Windows 请在弹窗中点击 **更多信息 → 仍要运行**（仅需放行一次）。

## 核心体验

- **随处可写，不换输入法**：聊天软件、浏览器、文档、终端、IDE……光标在哪里，文字就写到哪里，平时打字习惯完全不受影响。
- **边说边看，实时修正**：说话时浮窗同步展示识别预览，听到后文语境后会自动回头修正前面的同音误字。
- **出字即成稿**：自动添加标点，将口述数字整理为书面格式（「六十四兆」→「64兆」），并在中英文之间自动加空格（「我用 Python 写代码」）。
- **本机离线，免费无限**：基于 SenseVoice 模型在本机完成识别，无需注册账号，没有时长与次数限制，断网照常可用。
- **多语言与中英混说**：支持中文、英文、粤语、日语、韩语，可自动判断语种，中英夹杂无需手动切换。
- **认得你的专属词**：把常被认错的人名、产品名、项目术语加入「自定义词汇」（可附带近音读法），本机识别时自动向正确写法靠拢。
- **安静不打扰，细节顺手**：
  - 不抢当前窗口焦点；说错了按 <kbd>Esc</kbd> 立即作废；
  - 戴蓝牙耳机时默认调用电脑内置麦克风，音乐不中断、音质不降级；
  - macOS 上自动避开密码输入框。中途取消或未能写入的文字可以从菜单历史里找回；
  - 长段口述可切换为「按一次开始、再按一次结束」。
- **可选 AI 润色校对**：默认关闭。按需接入任意 OpenAI 兼容服务（包括 Ollama、LM Studio 等本机模型），进一步修正同音字与口误。

## 三步上手

1. **首次准备**：打开应用后跟随四步引导完成初始化——授权必要权限（macOS 需麦克风、输入监控与辅助功能；Windows 确认麦克风可用即可），后台会自动下载一次约 240 MB 的语音模型，最后在引导页内完成一次真实试听写。
2. **开始口述**：把光标点在想输入文字的位置，**按住热键开口说话**，浮窗实时显示预览。
3. **松开上屏**：松开热键，排版好的文字直接写入当前窗口（短于 0.3 秒的轻触会自动按误触忽略）。

> 不同平台的快捷键预设、历史记录找回与托盘快捷开关，详见 [macOS 使用说明](macos/README.md#日常使用) 与 [Windows 使用说明](windows/README.md#日常使用)。

## 隐私承诺

语音往往比最终发出的文字包含更多私人信息，因此**本地识别是 VoiceTyper 的设计前提，而不是附加选项**：

- **录音不出本机**：音频仅在本机内存中实时处理，不写入磁盘，也不上传到任何服务器。
- **可完全断网运行**：首次下载完语音模型后无需联网；检查更新仅在你手动点击时触发。
- **日志默认零文本留痕**：常规日志文件不记录识别出的文字与自定义词汇；仅当你主动开启智能校对中的「记录校对日志」（默认关闭）用于效果评估时，才会在本机单独保存校对记录且绝不上传。菜单中的近期结果只保存在内存里，退出即清空。
- **AI 校对边界清晰**：默认关闭。主动开启后**只发送识别出的文字**到你自行配置的服务地址，绝不发送音频；API Key 加密保存在 macOS 钥匙串或 Windows DPAPI 中。若接入本机大模型，整条链路依然完全离线。

## 常见问题

### 它是输入法吗？会影响平时打字吗？

不是。VoiceTyper 不注册为系统输入法，只在你松开热键的那一刻把识别好的文字送入当前焦点窗口，和你正在使用的拼音、五笔或英文输入法互不干扰。

### 为什么常驻几百 MB 内存？可以省内存吗？

默认在启动时把语音模型加载进内存常驻（macOS 实测约 500 MB），这样每次按下热键都能零等待立刻收音。如果不常口述，可以在设置中关闭启动预加载，或开启「空闲后自动释放内存」——下次按热键时会边录音边加载模型。

### 哪些地方可能写不进去文字？

绝大多数支持粘贴的输入框都能直接写入。部分密码框、全屏游戏、远程桌面，以及 Windows 上以管理员身份运行的窗口会拒绝外部模拟输入；此时识别结果会保留在剪贴板或菜单的近期记录里，手动粘贴即可。

### 可以添加热词或更换语音模型吗？

**支持热词**：在「设置 → 词汇」里添加常被认错的专有名词即可，全部在本机生效（详见 [macOS 词汇说明](macos/README.md#自定义词汇)、[Windows 词汇说明](windows/README.md#自定义词汇)）。  
**不支持更换语音模型**：一体化应用专注于把 SenseVoice-Small 打磨得稳定顺手，不堆砌未充分调优的模型选项。如需使用其他模型或远程 GPU 推理，可使用[客户端—服务端版本](client-server/README.md)。

### Windows 版目前的成熟度如何？

Windows 版采用原生系统托盘与界面交互，已在 Windows 真机上跑通模型下载与日常听写；近期细节改进、arm64 设备、长时间连续运行及多显示器缩放等场景仍在持续完善真机验收。遇到任何问题欢迎提交 [Issue](https://github.com/oyasmi/voice-typer/issues)。

## 参与开发

```text
voice-typer/
├── macos/           # macOS 一体化应用：Swift + AppKit/SwiftUI + ONNX Runtime
├── windows/         # Windows 一体化应用：.NET 10 + WinForms + ONNX Runtime
└── client-server/   # 旧客户端—服务端架构：用于 Linux、Intel Mac 与远程共享服务端场景
```

macOS 与 Windows 两端共享同一套产品状态机与识别管线，使用同一组金标准夹具校验跨平台数值一致性。仓库根目录提供统一的 `Makefile` 入口：

```bash
make          # 按当前系统构建（macOS 或 Windows）
make test     # 运行当前平台的自动化测试
make help     # 查看全部构建、测试与清理目标
```

进阶开发与架构资料：

- **macOS**：[开发说明](macos/DEVELOPMENT.md) · [架构设计](macos/DESIGN.md) · [更新日志](macos/CHANGELOG.md)
- **Windows**：[开发说明](windows/DEVELOPMENT.md) · [架构设计](windows/DESIGN.md) · [变更记录](windows/CHANGELOG.md)
- **客户端—服务端**：[架构说明](client-server/README.md) · [通信协议](client-server/PROTOCOL.md)
- **模型选型记录**：[Fun-ASR-Nano 调研与暂缓引入决定](FUN_ASR_NANO_EVALUATION.md)

欢迎通过 [GitHub Issues](https://github.com/oyasmi/voice-typer/issues) 报告问题或交流想法。

## 致谢

本地语音识别能力建立在 [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) 与 [ONNX Runtime](https://github.com/microsoft/onnxruntime) 之上，感谢这些开源项目让高质量的离线语音识别得以跑在普通个人电脑里。

## 许可证

本项目基于 [Apache License 2.0](LICENSE) 开源。第三方组件（SenseVoice 模型、ONNX Runtime 等）遵循各自的许可证条款。
