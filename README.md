# VoiceTyper - macOS 本地语音输入

基于 FunASR 的离线语音识别，支持客户端/服务端分离部署。

## 功能

- 🎤 按住热键语音输入，松开自动识别
- 🔒 完全本地离线，无需联网
- ⚙️ 支持自定义热键、词库
- 🤖 支持 LLM 智能纠错
- 🚀 支持 Apple Silicon GPU 加速

## 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Python 3.9+（推荐 3.12）
- Apple Silicon (M1/M2/M3/M4) 推荐，Intel 也支持

## 架构设计
```text
┌─────────────────────────────────────────────────────────────┐
│                    VoiceTyper 系统架构                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────────┐      HTTP      ┌───────────────────┐
│  │   macOS 客户端       │  ──────────▶  │   语音识别服务     │
│  │   (VoiceTyper.app)  │  ◀──────────  │   (voice_server)  │
│  │                     │    JSON        │                   │
│  │  - 热键监听          │               │  - FunASR 模型    │
│  │  - 录音             │               │  - 标点恢复        │
│  │  - UI 提示          │               │  - 热词支持        │
│  │  - 文本插入          │               │                   │
│  └─────────────────────┘               └───────────────────┘
│                                                             │
│  配置: ~/.config/voice_typer/          端口: 127.0.0.1:6008 │
└─────────────────────────────────────────────────────────────┘
```

## 快速开始

### 0. (可选) 配置 LLM 纠错

服务端启动时添加 LLM 参数：

```bash
./run.sh --llm-base-url https://api.openai.com/v1 \
         --llm-api-key sk-xxx \
         --llm-model gpt-4o-mini
```

### 1. 启动服务端

```bash
cd server
pip install -r requirements.txt
./run.sh
```

### 2. 启动客户端
```bash
cd client
pip install -r requirements.txt
python main.py
```

### 3. 使用
按住 Cmd+Space 说话
松开自动识别并输入
配置
配置文件：~/.config/voice_typer/config.yaml
```yaml
server:
  host: "127.0.0.1"
  port: 6008
  llm_recorrect: false  # 启用 LLM 智能纠错（需服务端配置 LLM）

hotkey:
  modifiers: ["cmd"]
  key: "space"

hotword_files:
  - "hotwords.txt"
```

## LLM 智能纠错

可选功能，使用 LLM 自动修正识别错误（同音字、口语词、标点等）。

服务端配置 LLM API，客户端设置 `llm_recorrect: true` 即可启用。

## 致谢
- FunASR - 阿里达摩院开源的语音识别工具包
- rumps - macOS 菜单栏应用框架
- pynput - 键盘鼠标监听库
- 