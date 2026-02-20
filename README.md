# VoiceTyper - 本地语音输入工具

基于 FunASR 的离线语音识别应用，支持 macOS 和 Linux 平台，采用客户端/服务端分离架构。

## 功能特性

- 🎤 **按住录音** - 按住热键开始录音，松开自动识别
- 🔒 **完全离线** - 无需联网，本地处理，保护隐私
- ⚙️ **自定义配置** - 支持自定义热键、用户词库
- 🌐 **Fn 键支持** - macOS 支持绑定 Fn（地球仪）键作为热键
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音
- 🤖 **LLM 智能纠错** - 可选的大语言模型智能纠错
- 🚀 **GPU 加速** - 支持 Apple Silicon (MPS) 和 NVIDIA (CUDA)
- 🌍 **多语言** - 支持中文、英文语音识别
- 🖥️ **多平台** - 支持 macOS、Windows 和 Linux (Wayland)

## 系统架构

```
┌─────────────────────────────────────────────────────────────┐
│                    VoiceTyper 系统架构                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────────┐      HTTP      ┌───────────────────┐
│  │   客户端             │  ──────────▶  │   语音识别服务     │
│  │   (macOS/Linux)     │  ◀──────────  │   (Tornado)       │
│  │                     │    JSON        │                   │
│  │  - 热键监听          │               │  - FunASR 模型    │
│  │  - 录音             │               │  - 标点恢复        │
│  │  - UI 提示          │               │  - 热词支持        │
│  │  - 文本插入          │               │  - LLM 纠错        │
│  └─────────────────────┘               └───────────────────┘
│                                                             │
│  配置: ~/.config/voice_typer/          端口: 127.0.0.1:6008 │
└─────────────────────────────────────────────────────────────┘
```

## 支持平台

| **macOS** | ✅ 完全支持 | Python + PyObjC + rumps |
| **Windows** | ✅ 完全支持 | Python + pystray + pynput |
| **Linux** | ✅ 完全支持 | Python + GTK4 + evdev (Wayland) |

> **注意**: 核心 ASR 服务由 Python 实现，支持跨平台运行。

## 快速开始

### 1. 安装服务端

服务端是所有客户端共享的，只需安装一次：

```bash
cd server
pip install -r requirements.txt
./run.sh
```

服务端默认监听 `127.0.0.1:6008`。

### 2. 选择并安装客户端

根据您的平台选择对应的客户端：

---

## macOS 客户端

### 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Python 3.9+（推荐 3.12）
- Apple Silicon (M1/M2/M3/M4) 推荐，Intel 也支持

### 安装步骤

```bash
cd client_macos
pip install -r requirements.txt
python main.py
```

### 构建应用

```bash
./build.sh
# 生成的 .app 文件在 dist/ 目录
```

### 配置

配置文件位置: `~/.config/voice_typer/config.yaml`

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  llm_recorrect: false  # 启用 LLM 智能纠错

hotkey:
  modifiers: ["ctrl"]    # cmd, ctrl, option, shift
  key: "f2"             # 默认 Ctrl+F2
  # 或使用 Fn(地球仪) 键:
  # modifiers: []
  # key: "fn"

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70
```

### 使用方法

1. 启动应用后，菜单栏会出现 VoiceTyper 图标
2. **按住热键**（默认 Ctrl+F2，可配置为 Fn 键）开始录音
3. **松开** 自动识别并插入文本到当前光标位置（录音不足 0.3 秒将被忽略）

### 权限要求

首次运行需要授予以下权限：
- **辅助功能** - 全局热键和文本输入
- **麦克风** - 录音权限

---

## Windows 客户端

### 系统要求

- Windows 10/11
- Python 3.8+

### 安装步骤

```bash
cd client_windows
pip install -r requirements.txt
python main.py
```

### 构建应用

```bash
pyinstaller voicetyper.spec
# 生成的 .exe 文件在 dist/ 目录
```

### 配置

配置文件位置: `%APPDATA%\voice_typer\config.yaml`

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  llm_recorrect: true  # 启用 LLM 智能纠错

hotkey:
  modifiers: ["ctrl"]    # ctrl, alt, shift, win_l, win_r
  key: "f2"             # 默认 Ctrl + F2

hotword_files:
  - "hotwords.txt"
```

### 使用方法

1. 启动应用后，系统托盘会出现 VoiceTyper 图标
2. **按住 Ctrl+F2** 开始录音
3. **松开** 自动识别并插入文本

---

## Linux 客户端

### 系统要求

- Linux (Wayland 会话)
- GNOME 桌面环境（推荐）
- Python 3.9+

### 安装步骤

#### Ubuntu / Debian

```bash
# 安装系统依赖
sudo apt update
sudo apt install -y python3 python3-pip python3-dev build-essential
sudo apt install -y gir1.2-gtk-4.0 libgtk-4-1 wl-clipboard libportaudio2 portaudio19-dev

# 安装 Python 依赖
cd client_linux
pip3 install -r requirements.txt

# 配置设备权限
make install-udev
```

#### Fedora / RHEL

```bash
sudo dnf install python3 python3-devel python3-pip \
    gtk4 wl-clipboard portaudio-devel

cd client_linux
pip3 install -r requirements.txt
make install-udev
```

#### Arch Linux

```bash
sudo pacman -S python python-pip gtk4 wl-clipboard portaudio

cd client_linux
pip3 install -r requirements.txt
make install-udev
```

**重要**: 配置设备权限后需要**注销并重新登录**以使组权限生效。

### 运行

```bash
cd client_linux
make run
# 或直接运行
python3 main.py
```

### 配置

配置文件位置: `~/.config/voice_typer/config.yaml`

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: ""          # API 密钥（远程服务器需要）
  llm_recorrect: false # 启用 LLM 智能纠错

hotkey:
  modifiers:
    - "ctrl"           # ctrl, alt, shift, super
  key: "f2"            # 默认 Ctrl+F2

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70
```

### 使用方法

1. **按住 Ctrl+F2** 开始录音
2. **松开** 自动识别并插入文本

### 故障排除

#### "未检测到键盘设备"

```bash
# 确认 udev 规则已安装
ls -l /etc/udev/rules.d/99-voicetyper-input.rules

# 确认用户在 input 组
groups $USER | grep input

# 重新安装权限
make install-udev
# 注销并重新登录
```

#### 文本插入失败

```bash
# 检查是否在 Wayland 会话
echo $XDG_SESSION_TYPE  # 应该输出 "wayland"

# 安装 wl-clipboard
sudo apt install wl-clipboard
```

---

## 服务端高级配置

### 基础选项

```bash
cd server
./run.sh --host 0.0.0.0 --port 6008 --model paraformer-zh --device mps
```

**参数说明**：
- `--host HOST` - 监听地址（默认: 127.0.0.1）
- `--port PORT` - 监听端口（默认: 6008）
- `--model MODEL` - 识别模型（paraformer-zh, paraformer-en, SenseVoiceSmall）
- `--punc-model MODEL` - 标点恢复模型（ct-punc 或 "none" 禁用）
- `--device DEVICE` - 处理设备（cpu, mps, cuda）
- `--api-keys KEYS` - API 密钥认证（逗号分隔）

### LLM 智能纠错

使用大语言模型自动修正识别错误（同音字、口语词、标点等）：

```bash
./run.sh --llm-base-url https://api.openai.com/v1 \
         --llm-api-key sk-xxx \
         --llm-model gpt-4o-mini
```

**LLM 参数**：
- `--llm-base-url URL` - LLM API 地址
- `--llm-api-key KEY` - API 密钥
- `--llm-model MODEL` - 模型名称（默认: gpt-4o-mini）
- `--llm-temperature T` - 温度参数（默认: 0.3）
- `--llm-max-tokens N` - 最大 token 数（默认: 600）

启用 LLM 后，在客户端配置中设置 `llm_recorrect: true` 即可使用。

### 组合示例

```bash
# 使用英文模型 + MPS 加速 + LLM 纠错 + API 认证
./run.sh --host 0.0.0.0 --port 6008 \
         --model paraformer-en --device mps \
         --punc-model ct-punc \
         --llm-base-url https://api.openai.com/v1 \
         --llm-api-key sk-xxx \
         --llm-model gpt-4o-mini \
         --api-keys "super_secret_key"
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

## API 端点

服务端提供 REST API：

- `POST /recognize` - 提交音频进行识别
- `GET /health` - 检查服务状态
- `GET /models` - 列出可用模型

### 请求示例

```bash
# 识别音频
curl -X POST http://127.0.0.1:6008/recognize \
     -F "audio=@test.wav"

# 带认证的请求
curl -X POST http://127.0.0.1:6008/recognize \
     -H "X-API-Key: your-api-key" \
     -F "audio=@test.wav"
```

## 架构设计

### 客户端职责

- 热键监听（全局快捷键）
- 音频录制（16kHz, mono, 16-bit）
- UI 指示器（录音状态提示）
- 文本插入（模拟键盘输入）
- 服务端通信（HTTP 客户端）

### 服务端职责

- 语音识别（FunASR 模型推理）
- 标点恢复
- 热词支持
- LLM 智能纠错
- API 认证

### 通信协议

客户端通过 HTTP 与服务端通信：
- 请求：multipart/form-data 上传 WAV 音频
- 响应：JSON 格式的识别结果和纠错文本

## 项目结构

```
voice-typer/
├── client_macos/          # macOS 客户端
│   ├── main.py            # 入口程序
│   ├── controller.py      # 核心控制器
│   ├── recorder.py        # 音频录制
│   ├── text_inserter.py   # 文本插入
│   ├── indicator.py       # UI 指示器
│   ├── asr_client.py      # 服务端通信
│   └── config.py          # 配置管理
├── client_linux/          # Linux 客户端
│   ├── main.py            # 入口程序
│   ├── controller.py      # 核心控制器
│   ├── recorder.py        # 音频录制
│   ├── text_inserter.py   # 文本插入
│   ├── indicator.py       # UI 指示器
│   ├── hotkey_listener.py # 热键监听
│   ├── asr_client.py      # 服务端通信
│   └── config.py          # 配置管理
└── server/                # 语音识别服务
    ├── asr_server.py      # Tornado 服务器
    ├── recognizer.py      # FunASR 集成
    ├── auth.py            # API 认证
    └── llm_client.py      # LLM 纠错
```

## 性能优化

### Apple Silicon (M1/M2/M3/M4)

使用 MPS 加速可显著提升识别速度：

```bash
./run.sh --device mps
```

### NVIDIA GPU

使用 CUDA 加速：

```bash
./run.sh --device cuda
```

### 内存优化

使用较小模型可降低内存占用：

```bash
# SenseVoice 模型更轻量
./run.sh --model SenseVoiceSmall
```

## 常见问题

### 1. 客户端无法连接服务端

- 确认服务端已启动：`curl http://127.0.0.1:6008/health`
- 检查配置文件中的 host 和 port 是否正确

### 2. 识别结果不准确

- 使用自定义词库添加专业术语
- 启用标点恢复：`--punc-model ct-punc`
- 启用 LLM 纠错

### 3. 录音无声音

- 检查麦克风权限
- 确认系统麦克风输入已选择正确设备

### 4. 文本插入位置错误

- macOS: 确保已授予辅助功能权限
- Linux: 确认在 Wayland 会话中运行

## 致谢

- [FunASR](https://github.com/alibaba-damo-academy/FunASR) - 阿里达摩院开源的语音识别工具包
- [rumps](https://github.com/jaredks/rumps) - macOS 菜单栏应用框架
- [pynput](https://github.com/moses-palmer/pynput) - 跨平台输入控制库
- [PyGObject](https://pygobject.readthedocs.io/) - Python GTK 绑定
- [evdev](https://python-evdev.readthedocs.io/) - Linux 输入设备处理

