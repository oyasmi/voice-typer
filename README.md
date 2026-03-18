# VoiceTyper - 本地语音输入工具

基于 FunASR 的离线语音识别应用，支持 macOS、Windows 和 Linux 平台，采用客户端/服务端分离架构。

## 功能特性

- 🎤 **按住录音** - 按住热键开始录音，松开自动识别
- 🔒 **完全离线** - 无需联网，本地处理，保护隐私
- ⚙️ **自定义配置** - 支持自定义热键、用户词库
- 🌐 **Fn 键支持** - macOS 支持绑定 Fn（地球仪）键作为热键
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音
- 🤖 **LLM 智能纠错** - 可选的大语言模型智能纠错
- 🚀 **硬件加速** - 服务端当前支持 CPU 与 NVIDIA CUDA
- 🌍 **多语言** - 支持中文、英文语音识别
- 🖥️ **多平台** - 支持 macOS、Windows 和 Linux (Wayland)

## 系统架构

```text
┌─────────────────────────────────────────────────────────────┐
│                     VoiceTyper 系统架构                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────────┐      HTTP     ┌─────────────────┐  │
│  │ 客户端              │  ──────────▶  │ 语音识别服务    │  │
│  │ (macOS/Win/Linux)   │  ◀──────────  │ (Tornado)       │  │
│  │                     │      JSON     │                 │  │
│  │ - 热键监听          │               │ - FunASR 模型   │  │
│  │ - 录音              │               │ - 标点恢复      │  │
│  │ - UI 提示           │               │ - 热词支持      │  │
│  │ - 文本插入          │               │ - LLM 纠错      │  │
│  └─────────────────────┘               └─────────────────┘  │
│                                                             │
│  配置: ~/.config/voice_typer/         端口: 127.0.0.1:6008  │
└─────────────────────────────────────────────────────────────┘
```

## 客户端支持平台

| 平台 | 支持状态 | 客户端技术栈 |
| --- | --- | --- |
| **macOS** | ✅ 完全支持 | Python + PyObjC + rumps |
| **Windows** | ✅ 完全支持 | Python + pystray + pynput |
| **Linux** | ✅ 完全支持 | Python + GTK4 + evdev (Wayland) |

## 快速开始

### 1. 安装服务端

服务端提供语音识别服务，可以本地部署，也可以部署在服务器上，供多客户端共享使用。

#### 使用脚本安装并启动

```bash
curl -O -L https://github.com/oyasmi/voice-typer/raw/refs/heads/master/server/scripts/voice_typer_server.sh
bash ./voice_typer_server.sh setup
bash ./voice_typer_server.sh run
```

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
   - 前台启动：~/.venvs/voice-typer/bin/voice-typer-server
   - 后台启动：nohup ~/.venvs/voice-typer/bin/voice-typer-server &

5. 询问用户是否需要现在就帮忙启动服务：
   - 如果启动，先询问用户是否需要配置 LLM 纠错功能（需要 --llm-base-url、--llm-api-key、--llm-model 三个参数）
   - 如果用户不提供 LLM 参数，则禁用纠错功能，直接以默认参数启动
```

> 📖 服务端的完整参数说明、API 端点、使用场景等，请参阅 [server/README.md](server/README.md)。

### 2. 选择并安装客户端

根据您的平台选择对应的客户端：

---

## macOS 客户端

### 系统要求

- macOS 14.0 (Sonoma) 或更高版本

### 下载应用

从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载，解压 .app 文件，双击运行。

### 授予权限

由于 macOS 的安全机制，首次使用需要授予以下权限：
- **隐私与安全性 → 辅助功能 (Accessibility)**: 用于监听全局热键和模拟键盘输入。
- **隐私与安全性 → 输入监控 (Input Monitoring)**: 用于监听 Fn 等按键事件。
- **隐私与安全性 → 麦克风 (Microphone)**: 用于采集音频。

### 开始使用

1. 启动应用后，菜单栏会出现 VoiceTyper 图标
2. **按住热键**（默认 `Fn` / 地球仪键，也可配置为其他按键）开始录音
3. **松开** 自动识别并插入文本到当前光标位置（录音不足 0.3 秒将被忽略）


### 配置文件示例

配置文件位置: `~/.config/voice_typer/config.yaml`

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  llm_recorrect: true   # 启用 LLM 智能纠错，需要服务端配置 LLM 参数

hotkey:
  modifiers: []         # 默认单 Fn（地球仪）键
  key: "fn"
  # 或改成其他组合键:
  # modifiers:
  #   - "ctrl"
  # key: "space"

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70
```

---

## Windows 客户端

👉 详细安装和使用说明请参阅 [client_windows/README.md](client_windows/README.md)。

从 release 下载 `VoiceTyper.exe`，双击即可运行。默认热键 `Ctrl+F2`，按住录音，松开识别。

---

## Linux 客户端

👉 详细安装和使用说明请参阅 [client_linux/README.md](client_linux/README.md)。

支持 Wayland + GNOME 环境，使用 evdev 监听热键、GTK4 指示器、wl-clipboard 插入文本。默认热键 `Ctrl+F2`。

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

编辑 `~/.config/voice_typer/hotwords.txt`，每行一个词：

```text
# 专业术语
FunASR
Python
GitHub

# 自定义词汇
你的名字
公司名称
```

## 致谢

- [FunASR](https://github.com/alibaba-damo-academy/FunASR) - 阿里达摩院开源的语音识别工具包
- [rumps](https://github.com/jaredks/rumps) - macOS 菜单栏应用框架
- [pynput](https://github.com/moses-palmer/pynput) - 跨平台输入控制库
- [PyGObject](https://pygobject.readthedocs.io/) - Python GTK 绑定
- [evdev](https://python-evdev.readthedocs.io/) - Linux 输入设备处理
