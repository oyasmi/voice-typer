# VoiceTyper Go Client

🎤 **跨平台语音输入桌面工具** - Go语言实现

[![Go Version](https://img.shields.io/badge/Go-1.21+-00ADD8?logo=go)](https://golang.org)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

一个快速、轻量、跨平台的离线语音输入工具，基于FunASR语音识别引擎。

## ✨ 特性

- 🎤 **按住说话** - 按住热键录音，松开即识别
- 🔒 **完全离线** - 无需联网，本地处理
- 🚀 **毫秒级启动** - Go编译，快速启动
- 📦 **单文件可执行** - 无需依赖，开箱即用
- 🌍 **跨平台** - macOS、Windows、Linux (X11 + Wayland)
- ⚙️ **可配置** - 自定义热键、词库
- 🤖 **LLM纠错** - 可选的智能纠错功能
- 🎨 **系统托盘** - 最小化到托盘，不占用任务栏

## 截图

（待添加：应用截图）

## 🎬 演示

（待添加：使用演示GIF）

## 📦 下载

### 最新版本

从 [Releases](../../releases) 下载预编译的二进制文件。

### 支持的平台

| 平台 | 下载 | 状态 |
|------|------|------|
| **macOS (Apple Silicon)** | [voicetyper-mac-arm64.zip](../../releases) | ✅ 完全支持 |
| **macOS (Intel)** | [voicetyper-mac-amd64.zip](../../releases) | ✅ 完全支持 |
| **Windows (64位)** | [voicetyper-win.zip](../../releases) | ✅ 完全支持 |
| **Linux (64位)** | [voicetyper-linux.tar.gz](../../releases) | ✅ 完全支持 |

## 🚀 快速开始

### 方法1：使用预编译二进制（推荐）

```bash
# 1. 下载对应平台的二进制文件

# 2. 赋予执行权限（Linux/macOS）
chmod +x voicetyper

# 3. 运行
./voicetyper
```

### 方法2：从源码构建

详见 [INSTALL.md](INSTALL.md)。

## 📖 文档

- **[INSTALL.md](INSTALL.md)** - 详细安装指南
- **[DESIGN.md](DESIGN.md)** - 设计文档
- **[PROJECT_SUMMARY.md](PROJECT_SUMMARY.md)** - 项目总结

## 💻 系统要求

### 通用要求
- Go 1.21+
- 语音识别服务端（FunASR服务器）

### Linux (X11)

**系统依赖**：
```bash
# Ubuntu/Debian
sudo apt-get install libgl1-mesa-dev xorg-dev libxtst-dev libxinerama-dev libxcursor-dev libxrandr-dev libxi-dev

# Fedora/RHEL
sudo dnf install mesa-libGL-devel libXtst-devel libXinerama-devel libXcursor-devel libXrandr-devel libXi-devel

# Arch Linux
sudo pacman -S mesa xorg-libxtst xorg-xinerama xorg-xcursor xorg-randr xorg-xi
```

### Linux (Wayland)

**额外依赖**：
```bash
# ydotool（用于键盘模拟）
sudo apt-get install ydotool  # Ubuntu/Debian
sudo dnf install ydotool      # Fedora/RHEL
```

**注意**：Wayland支持需要ydotool。如果未安装，程序会显示提示。

### macOS

**系统要求**：
- macOS 10.15 (Catalina) 或更高版本
- Xcode Command Line Tools

```bash
xcode-select --install
```

### Windows

**系统要求**：
- Windows 10/11
- MinGW-w64 或 MSYS2（用于编译）

## 构建说明

### 获取代码

```bash
cd client_go
```

### 安装Go依赖

```bash
go mod download
```

### 编译

```bash
# 当前平台
go build -o voicetyper

# macOS (Apple Silicon)
GOOS=darwin GOARCH=arm64 go build -o voicetyper-mac-arm64

# macOS (Intel)
GOOS=darwin GOARCH=amd64 go build -o voicetyper-mac-amd64

# Windows
GOOS=windows GOARCH=amd64 go build -o voicetyper.exe

# Linux
GOOS=linux GOARCH=amd64 go build -o voicetyper-linux
```

## 运行

### 1. 启动语音识别服务器

```bash
cd ../server
./run.sh
```

### 2. 运行客户端

```bash
./voicetyper
```

首次运行会自动创建配置文件：
- Linux/macOS: `~/.config/voice-typer/config.yaml`
- Windows: `%APPDATA%\voice-typer\config.yaml`

## 配置

配置文件示例（`~/.config/voice-typer/config.yaml`）：

```yaml
servers:
  - name: local
    host: "127.0.0.1"
    port: 6008
    timeout: 30.0
    api_key: ""
    llm_recorrect: false

hotkey:
  modifiers:
    - "cmd"  # macOS使用cmd，Windows/Linux使用ctrl
  key: "space"

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70

input:
  method: "clipboard"
```

## 使用方法

1. **启用语音输入**：程序启动后会自动启用
2. **录音**：按住热键（默认：Cmd+Space on macOS, Ctrl+Space on Win/Linux）
3. **释放**：松开热键，自动识别并插入文本
4. **禁用**：通过托盘菜单禁用

## 功能特性

- ✅ **全局热键**：按住录音，释放识别
- ✅ **离线识别**：完全本地处理，无需联网
- ✅ **自定义词库**：支持热词文件
- ✅ **LLM纠错**：可选的智能纠错功能
- ✅ **跨平台**：macOS、Windows、Linux (X11 + Wayland)
- ✅ **系统托盘**：最小化到托盘
- ✅ **视觉反馈**：录音状态显示

## 故障排除

### Linux: X11相关错误

```
fatal error: X11/extensions/XTest.h: No such file or directory
```

**解决方案**：安装X11开发库（见上文系统依赖）

### Linux: OpenGL相关错误

```
Package gl was not found in the pkg-config search path
```

**解决方案**：安装OpenGL开发库（见上文系统依赖）

### 权限问题

**macOS**：首次运行需要授予以下权限：
- 麦克风权限
- 辅助功能权限（用于热键监听）
- 自动化权限（用于键盘控制）

**Linux**：确保用户有音频设备访问权限

### 热键不工作

- 检查是否启用了语音输入（托盘菜单）
- 尝试更改热键组合
- 检查其他应用是否占用了相同热键

### 识别失败

- 确认ASR服务器正在运行
- 检查服务器地址和端口配置
- 查看服务器日志

## 开发

### 项目结构

```
voice-typer/
├── main.go              # 程序入口
├── internal/            # 内部包
│   ├── config/          # 配置管理
│   ├── audio/           # 音频录制
│   ├── hotkey/          # 热键监听
│   ├── api/             # API客户端
│   ├── input/           # 文本输入
│   ├── ui/              # 用户界面
│   └── controller/      # 核心控制器
└── pkg/platform/        # 平台相关代码
```

### 运行测试

```bash
# 单元测试（需要添加）
go test ./...

# 手动测试
go run main.go
```

## 已知问题

1. **Wayland支持**：需要ydotool，部分功能可能受限
2. **文本输入**：使用剪贴板方法，会临时覆盖剪贴板内容
3. **robotgo**：在某些配置下可能不稳定

## 许可证

与主项目相同

## 致谢

- [FunASR](https://github.com/alibaba-damo-academy/FunASR) - 语音识别
- [Fyne](https://fyne.io/) - 跨平台GUI框架
- [robotgo](https://github.com/go-vgo/robotgo) - 键盘鼠标控制
- [malgo](https://github.com/gen2brain/malgo) - 音频录制
