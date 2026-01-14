# VoiceTyper Windows 客户端

Windows 版本的语音输入桌面应用，通过按住热键进行语音转文字输入。

## 功能特性

- 🎤 **离线语音识别**: 基于 FunASR，无需联网即可使用
- ⌨️ **全局热键**: 按住 Win+Space 在任何应用中输入语音
- 📋 **自动粘贴**: 识别结果自动粘贴到当前光标位置
- 🔧 **可配置**: 支持自定义热键、词库等
- 🚀 **轻量级**: 单个可执行文件，无需安装 Python

## 系统要求

- Windows 10 或 Windows 11
- 麦克风设备
- 语音识别服务（需单独安装和启动）

## 快速开始

### 1. 安装语音识别服务

VoiceTyper 采用客户端-服务器架构，需要先启动 ASR 服务器。

详见 [服务器文档](../server/README.md)。

### 2. 启动服务

```bash
cd server
./run.sh
```

### 3. 运行客户端

**开发模式** (需要 Python 3.9+):

```bash
cd client_windows
pip install -r requirements.txt
python main.py
```

**生产模式** (使用构建的可执行文件):

1. 双击运行 `build.bat` 构建可执行文件
2. 运行 `dist\VoiceTyper.exe`

### 4. 使用

1. 应用会在系统托盘显示图标
2. 将光标定位到需要输入文字的位置
3. 按住 **Win+Space** 开始说话
4. 松开热键，识别结果会自动粘贴

## 配置

配置文件位于: `%APPDATA%\voice_typer\config.yaml`

```yaml
# 语音识别服务地址
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: ""
  llm_recorrect: false

# 热键配置
hotkey:
  modifiers:
    - "win_l"  # 左Win键
  key: "space"

# 用户词库文件
hotword_files:
  - "hotwords.txt"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
```

### 热键配置

支持的修饰键:
- `ctrl` - Ctrl 键
- `alt` - Alt 键
- `shift` - Shift 键
- `win_l` - 左 Windows 键
- `win_r` - 右 Windows 键

示例:
```yaml
# Win+Space (默认)
modifiers: ["win_l"]
key: "space"

# Ctrl+Shift
modifiers: ["ctrl", "shift"]
key: "space"

# Alt+Space
modifiers: ["alt"]
key: "space"
```

## 自定义词库

编辑词库文件: `%APPDATA%\voice_typer\hotwords.txt`

```
# 每行一个词，支持中英文
FunASR
Python
GitHub
OpenAI
```

## 开发

### 安装依赖

```bash
pip install -r requirements.txt
```

### 运行开发版本

```bash
python main.py
```

### 构建

**使用批处理脚本** (Windows):
```bash
build.bat
```

**使用 Make** (Linux/WSL):
```bash
make build
```

### 清理

```bash
make clean
```

## 文件结构

```
client_windows/
├── main.py              # 系统托盘 UI 入口
├── controller.py        # 核心控制器
├── config.py            # 配置管理
├── recorder.py          # 音频录制
├── asr_client.py        # ASR 服务客户端
├── text_inserter.py     # 文本插入 (剪贴板 + Ctrl+V)
├── hotkey_listener.py   # 全局热键监听
├── requirements.txt     # Python 依赖
├── voicetyper.spec      # PyInstaller 配置
├── build.bat            # Windows 构建脚本
├── Makefile             # Make 命令
├── README.md            # 本文档
└── assets/
    └── icon.ico         # 应用图标
```

## 技术栈

- **系统托盘**: pystray
- **全局热键**: pynput
- **音频录制**: sounddevice + numpy
- **HTTP 客户端**: tornado
- **配置管理**: PyYAML
- **打包工具**: PyInstaller

## 与 macOS 版本的差异

1. **UI 框架**: 使用 pystray 代替 rumps
2. **视觉反馈**: 系统托盘状态代替浮动窗口
3. **默认热键**: Win+Space 代替 Cmd+Space
4. **配置目录**: `%APPDATA%\voice_typer` 代替 `~/.config/voice_typer`
5. **打包格式**: 单个 .exe 文件

## 故障排除

### 麦克风权限

首次运行时，Windows 会提示授予麦克风权限，请点击"允许"。

### 热键不工作

1. 检查是否有其他应用占用了相同的热键
2. 尝试修改配置文件中的热键设置
3. 重新启动应用

### 无法识别文字

1. 检查 ASR 服务是否正常运行
2. 检查配置文件中的服务器地址和端口
3. 查看系统托盘图标的状态提示

### 文本粘贴失败

1. 确保目标应用支持文本粘贴
2. 检查剪贴板是否被其他应用锁定
3. 尝试手动粘贴 (Ctrl+V) 查看剪贴板内容

## 许可证

同主项目。

## 相关链接

- [macOS 客户端](../client/)
- [Linux 客户端](../client_linux/)
- [ASR 服务器](../server/)
