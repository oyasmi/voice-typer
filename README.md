# VoiceTyper - 本地语音输入工具

基于 FunASR 的离线语音识别应用，支持 macOS、Windows 和 Linux 平台，采用客户端/服务端分离架构。

## 功能特性

- 🎤 **按住录音** - 按住热键开始录音，松开自动识别
- 🔒 **完全离线** - 无需联网，本地处理，保护隐私
- ⚡ **流式实时预览** - 原生客户端默认流式模式，边说边在 HUD 浮窗中显示识别结果，松手后离线整段复识别产出准确文本
- ⚙️ **自定义配置** - 支持自定义热键、用户词库
- 🌐 **Fn 键支持** - macOS 支持绑定 Fn（地球仪）键作为热键
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音
- 🤖 **LLM 智能纠错** - 可选的大语言模型智能纠错
- 🚀 **硬件加速** - 服务端当前支持 CPU 与 NVIDIA CUDA
- 🌍 **多语言** - 支持中文、英文语音识别
- 🖥️ **多平台** - 支持 macOS、Windows 和 Linux (Wayland)

## 系统架构

```text
  按住热键                                           松开热键
     │                                                  │
     ▼                                                  ▼
 ┌────────────────── 客户端 ──────────────────┐  流式: WS /recognize/stream
 │                                            │  非流式: POST /recognize
 │  热键监听    录音     UI 浮窗    文本插入   │     (16kHz float32)
 │  ─────────────────────────────────────────  │         │
 │  macOS : Swift+AppKit  原生录音  剪贴板/AX  │         │
 │  Windows: .NET8+WinForms 原生录音 剪贴板   │         ▼
 │  Linux : evdev      GTK4     wl-copy       │  ┌─────────────┐
 │                                            │  │ Server :6008│
 │  配置: ~/.config/voice_typer/config.yaml   │  │  (Tornado)  │
 └────────────────────────────────────────────┘  │             │
                                                 │  ┌───────┐  │
     ┌───────────────────────────────────────────┤  │  ASR  │  │
     │   partial 实时预览 / final 准确结果       │  │(ONNX) │  │
     ▼                                           │  └───┬───┘  │
 光标处插入文本                                    │      ▼      │
                                                 │  标点恢复   │
                                                 │      ▼      │
                                                 │  LLM 纠错?  │
                                                 │  (可选)     │
                                                 └─────────────┘
```

## 客户端支持平台

| 平台 | 支持状态 | 客户端技术栈 |
| --- | --- | --- |
| **macOS** | ✅ 完全支持 | Swift + AppKit |
| **Windows** | ✅ 完全支持 | .NET 8 + WinForms |
| **Linux** | ✅ 完全支持 | Python + GTK4 + evdev (Wayland) |

## 快速开始

### 1. 安装服务端

服务端提供语音识别服务，可以本地部署，也可以部署在服务器上，供多客户端共享使用。

#### 使用脚本安装并启动

```bash
curl -O -L https://github.com/oyasmi/voice-typer/raw/refs/heads/master/server/scripts/voice_typer_server.sh
bash ./voice_typer_server.sh setup
```

**macOS Swift 客户端 / Windows 原生客户端**（流式识别，默认，HUD 实时预览 + 离线准确结果）：

```bash
bash ./voice_typer_server.sh run
```

**Linux 客户端**（非流式，兼容模式）：

```bash
bash ./voice_typer_server.sh run --no-streaming
```

> **兼容说明**：服务端默认为流式（WebSocket）模式，macOS Swift 客户端与 Windows 原生客户端均支持。Linux 客户端须加 `--no-streaming` 以使用 HTTP 非流式模式。

#### 或者让 🤖 AI Agent 帮你安装

将以下说明复制给你常用的 AI Agent（如 Claude Code、OpenCode 等），它会自动完成安装：

```
请帮我安装 VoiceTyper 语音识别服务端（voice-typer-server）。步骤如下：

1. 找到可用的 Python 3.10+ 解释器：
   - 先检查 python3 的版本，如果 >= 3.10 则直接使用
   - 如果 python3 版本 <= 3.9，依次检查 python3.10、python3.11、python3.12、python3.13、python3.14、python3.15 可执行文件是否存在于 PATH 环境变量中
   - 如果都找不到，报错告知用户需要安装 Python 3.10+，并给出安装 Python 的建议

2. 检查虚拟环境 ~/.venvs/voice-typer 是否已存在：
   - 如果已存在，检查其中的 Python 版本是否 >= 3.10
     - 如果 >= 3.10，询问用户是否复用已有环境（默认复用），若复用则跳到第 3 步
     - 如果 < 3.10，提示用户需要重建环境，删除后重新创建
   - 如果不存在，用找到的 Python 创建：<找到的python> -m venv ~/.venvs/voice-typer

3. 在虚拟环境中安装：
   ~/.venvs/voice-typer/bin/pip install --upgrade pip setuptools wheel
   ~/.venvs/voice-typer/bin/pip install --upgrade voice-typer-server

4. 安装完成后告知用户，并给出以下启动命令供用户自行使用：
   - 前台启动（macOS Swift / Windows 原生客户端，流式，默认）：~/.venvs/voice-typer/bin/voice-typer-server
   - 前台启动（Linux 客户端，非流式）：~/.venvs/voice-typer/bin/voice-typer-server --no-streaming
   - 后台启动：nohup ~/.venvs/voice-typer/bin/voice-typer-server [--no-streaming] &

5. 询问用户是否需要现在就帮忙启动服务：
   - 先询问用户使用的是哪种客户端：macOS Swift 版 / Windows 原生版 / Linux 版
   - macOS Swift 和 Windows 原生客户端支持流式（默认），Linux 客户端须加 --no-streaming
   - 再询问用户是否需要配置 LLM 纠错功能（需要 --llm-base-url、--llm-api-key、--llm-model 三个参数）
   - 如果用户不提供 LLM 参数，则禁用纠错功能，以对应参数启动
```

> 📖 服务端的完整参数说明、API 端点、使用场景等，请参阅 [server/README.md](server/README.md)。

### 2. 选择并安装客户端

根据您的平台选择对应的客户端：

---

## macOS 客户端

基于 Swift + AppKit 的原生菜单栏应用，流式识别（默认）。

- **系统要求**：macOS 14.0 (Sonoma) 或更高版本，Apple Silicon
- **安装**：从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper-macOS.dmg`，将 `VoiceTyper.app` 拖入「应用程序」后打开
- **首次授权**：应用会自动检查并引导完成三项权限——麦克风、辅助功能 (Accessibility)、输入监控 (Input Monitoring)，以及服务端连通性；存在未完成项时会自动弹出「权限与设置」窗口
- **默认热键**：`Fn`（地球仪）键，可在「权限与设置」窗口改为组合键
- **配置**：常用项均已 UI 化（服务地址/端口/API Key、LLM 纠错、热键、用户热词），底层仍保存于 `~/.config/voice_typer/config.yaml`

👉 详细安装、授权与构建说明请参阅 [client_macos_swift/README.md](client_macos_swift/README.md)。

---

## Windows 客户端

基于 .NET 8 + WinForms 的原生托盘应用，流式识别（默认）。

- **系统要求**：Windows 10 / 11（便携版需 `.NET Desktop Runtime 8.0`，完整版自带运行时）
- **安装**：从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper.exe`，双击运行，应用驻留系统托盘
- **默认热键**：`Ctrl + F2`
- **配置**：托盘菜单「权限与设置」中配置，配置文件位于 `%APPDATA%\voice_typer\config.yaml`

👉 详细安装、构建与配置说明请参阅 [client_windows_native/README.md](client_windows_native/README.md)。

---

## Linux 客户端

基于 Python + GTK4 + evdev 的 Wayland 客户端，非流式（HTTP）模式，须配合服务端 `--no-streaming`。

- **系统要求**：Linux Wayland 会话（推荐 GNOME），Python 3.10+，GTK4、wl-clipboard
- **安装**：`cd client_linux && make install && make install-udev`（udev 规则用于 evdev 访问，安装后需注销重新登录）
- **默认热键**：`Ctrl + F2`
- **配置**：`~/.config/voice_typer/config.yaml`

👉 详细安装、依赖与故障排除请参阅 [client_linux/README.md](client_linux/README.md)。

---

## LLM 智能纠错

VoiceTyper 支持接入 OpenAI 兼容的大语言模型，对识别结果进行二次纠错（同音字、口语词、标点等）。

### 服务端配置

启动服务端时传入 LLM 相关参数：

```bash
voice-typer-server --llm-base-url https://api.openai.com/v1 \
                   --llm-api-key sk-xxx \
                   --llm-model gpt-4o-mini
```

**LLM 参数**：
- `--llm-base-url URL` - LLM API 地址
- `--llm-api-key KEY` - API 密钥
- `--llm-model MODEL` - 模型名称（默认: gpt-4o-mini）
- `--llm-temperature T` - 温度参数（默认: 0.3）
- `--llm-max-tokens N` - 最大 token 数（默认: 600）

### 客户端配置

在客户端的 `config.yaml` 中启用：

```yaml
server:
  llm_recorrect: true
```

## 自定义词库

编辑热词文件，每行一个词，`#` 开头为注释：

```text
# 专业术语
FunASR
Python
GitHub

# 自定义词汇
你的名字
公司名称
```

热词文件位置：

- macOS / Linux：`~/.config/voice_typer/hotwords.txt`
- Windows：`%APPDATA%\voice_typer\hotwords.txt`

> **注意**：热词仅在**非流式模式**下生效（流式预览模型本身不支持热词）。原生客户端默认走流式，可在客户端设置中关闭「流式识别」以启用热词。

## 致谢

- [FunASR](https://github.com/alibaba-damo-academy/FunASR) - 阿里达摩院开源的语音识别工具包
- [PyGObject](https://pygobject.readthedocs.io/) - Python GTK 绑定（Linux 客户端）
- [evdev](https://python-evdev.readthedocs.io/) - Linux 输入设备处理
