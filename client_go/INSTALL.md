# VoiceTyper Go Client - 安装指南

本文档提供VoiceTyper Go客户端的详细安装和使用说明。

## 目录

- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [详细安装说明](#详细安装说明)
  - [Linux](#linux)
  - [macOS](#macos)
  - [Windows](#windows)
- [从源码构建](#从源码构建)
- [配置](#配置)
- [使用方法](#使用方法)
- [故障排除](#故障排除)
- [高级功能](#高级功能)

## 系统要求

### 通用要求
- Go 1.21 或更高版本（仅从源码构建时需要）
- VoiceTyper语音识别服务端（FunASR）
- 麦克风

### 各平台要求

#### Linux
- **X11**：libxtst, libxinerama, libgl (OpenGL)
- **Wayland**：ydotool（可选，用于键盘模拟）

#### macOS
- macOS 10.15 (Catalina) 或更高版本
- Xcode Command Line Tools

#### Windows
- Windows 10 或 11
- MinGW-w64 或 MSYS2（仅从源码构建时需要）

## 快速开始

### 使用预编译二进制（推荐）

1. **下载**：从 [Releases](../../releases) 下载对应平台的二进制文件

2. **安装**：
   ```bash
   # Linux/macOS
   chmod +x voicetyper
   sudo mv voicetyper /usr/local/bin/

   # Windows
   # 将 voicetyper.exe 放到 PATH 中的目录
   ```

3. **运行**：
   ```bash
   voicetyper
   ```

### 使用Docker（可选）

```bash
# 构建镜像
docker build -t voicetyper .

# 运行容器
docker run -d --device /dev/snd voicetyper
```

## 详细安装说明

### Linux

#### 1. 安装系统依赖

**Ubuntu/Debian:**
```bash
sudo apt-get update
sudo apt-get install -y \
    libgl1-mesa-dev \
    xorg-dev \
    libxtst-dev \
    libxinerama-dev \
    libxcursor-dev \
    libxrandr-dev \
    libxi-dev
```

**Fedora/RHEL/CentOS:**
```bash
sudo dnf install -y \
    mesa-libGL-devel \
    libXtst-devel \
    libXinerama-devel \
    libXcursor-devel \
    libXrandr-devel \
    libXi-devel
```

**Arch Linux:**
```bash
sudo pacman -S --needed \
    mesa \
    xorg-libxtst \
    xorg-xinerama \
    xorg-xcursor \
    xorg-randr \
    xorg-xi
```

#### 2. Wayland支持（可选）

```bash
# Ubuntu/Debian
sudo apt-get install ydotool

# Fedora
sudo dnf install ydotool

# Arch Linux (AUR)
yay -S ydotool
```

**配置ydotool权限**：
```bash
# 创建udev规则
sudo tee /etc/udev/rules.d/99-ydotool.rules <<EOF
KERNEL=="uinput", MODE="0660", OPTIONS+="static_node=uinput"
ENV{ID_INPUT_JOYSTICK}=="?*", RUN+="/usr/bin/ydotool"
EOF

# 重新加载udev规则
sudo udevadm control --reload-rules
sudo udevadm trigger

# 将用户添加到input组
sudo usermod -aG input $USER

# 重新登录以生效
```

#### 3. 从源码构建

```bash
# 克隆仓库
git clone https://github.com/yourusername/voice-typer.git
cd voice-typer/client_go

# 使用Makefile
make deps
make build

# 或使用脚本
chmod +x build/build.sh
./build/build.sh
```

### macOS

#### 1. 安装Xcode Command Line Tools

```bash
xcode-select --install
```

#### 2. 安装Homebrew（可选，用于依赖管理）

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

#### 3. 从源码构建

```bash
# 克隆仓库
git clone https://github.com/yourusername/voice-typer.git
cd voice-typer/client_go

# 使用Makefile
make deps
make build

# 或使用脚本
chmod +x build/build.sh
./build/build.sh
```

#### 4. 代码签名（可选，用于分发）

```bash
# 创建自签名证书（仅用于开发）
security create-keychain -p "" voicetyper.keychain
security import voicetyper.p12 -k ~/Library/Keychains/voicetyper.keychain

# 签名二进制
codesign --keychain ~/Library/Keychains/voicetyper.keychain \
         --sign "VoiceTyper" \
         --deep --force voicetyper-mac-arm64
```

### Windows

#### 1. 安装Go

从 [Go官网](https://golang.org/dl/) 下载并安装Go 1.21+。

#### 2. 安装MinGW-w64或MSYS2

**MSYS2（推荐）:**
1. 从 [msys2.org](https://www.msys2.org/) 下载并安装
2. 在MSYS2终端中安装所需工具：
```bash
pacman -S mingw-w64-x86_64-go mingw-w64-x86_64-gcc
```

#### 3. 从源码构建

```powershell
# 使用PowerShell
git clone https://github.com/yourusername/voice-typer.git
cd voice-typer\client_go

# 设置环境变量
$env:GOOS="windows"
$env:GOARCH="amd64"
$env:CGO_ENABLED="1"

# 构建
go build -ldflags="-s -w" -o voicetyper.exe
```

**或使用Git Bash:**
```bash
make deps
make build
```

## 从源码构建

### 使用Makefile（推荐）

```bash
# 查看所有命令
make help

# 安装依赖
make deps

# 构建当前平台
make build

# 跨平台构建
make build-all

# 创建发布包
make package

# 清理构建文件
make clean
```

### 使用构建脚本

```bash
# 跨平台构建
./build/build.sh

# 创建发布包
./build/package.sh
```

### 手动构建

```bash
# 设置构建标志
export VERSION=1.0.0
export LDFLAGS="-s -w -X main.version=$VERSION"

# 构建当前平台
go build -ldflags="$LDFLAGS" -o voicetyper

# macOS (Apple Silicon)
GOOS=darwin GOARCH=arm64 go build -ldflags="$LDFLAGS" -o voicetyper-mac-arm64

# macOS (Intel)
GOOS=darwin GOARCH=amd64 go build -ldflags="$LDFLAGS" -o voicetyper-mac-amd64

# Windows
GOOS=windows GOARCH=amd64 CGO_ENABLED=1 go build -ldflags="$LDFLAGS" -o voicetyper.exe

# Linux
GOOS=linux GOARCH=amd64 go build -ldflags="$LDFLAGS" -o voicetyper-linux
```

## 配置

### 配置文件位置

- **Linux/macOS**: `~/.config/voice-typer/config.yaml`
- **Windows**: `%APPDATA%\voice-typer\config.yaml`

### 首次运行

首次运行会自动创建默认配置文件和词库文件：

```bash
./voicetyper
```

### 配置示例

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
    - "cmd"   # macOS: cmd, Windows/Linux: ctrl
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

### 自定义词库

编辑 `~/.config/voice-typer/hotwords.txt`：

```
# 自定义词汇，每行一个
FunASR
Python
GitHub
OpenAI
ChatGPT

# 添加更多词汇...
```

## 使用方法

### 1. 启动语音识别服务

```bash
cd ../server
./run.sh
```

### 2. 启动客户端

```bash
./voicetyper
```

### 3. 使用语音输入

1. 客户端会自动启用（托盘图标显示"Enabled"）
2. 按住热键（默认：Cmd+Space on macOS, Ctrl+Space on Win/Linux）
3. 说话
4. 松开热键
5. 识别的文本自动插入到光标位置

### 4. 系统托盘菜单

- **Status**: 当前状态
- **Enable/Disable**: 启用/禁用语音输入
- **Open Config**: 打开配置文件
- **About**: 关于信息
- **Quit**: 退出程序

## 故障排除

### Linux常见问题

#### X11相关错误

**错误**: `fatal error: X11/extensions/XTest.h: No such file or directory`

**解决方案**:
```bash
sudo apt-get install libxtst-dev xorg-dev
```

#### OpenGL相关错误

**错误**: `Package gl was not found`

**解决方案**:
```bash
sudo apt-get install libgl1-mesa-dev
```

#### 权限问题

**错误**: 热键不工作

**解决方案**: 检查辅助功能权限

```bash
# Ubuntu
sudo apt-get install at-spi2-core
```

### macOS常见问题

#### 权限被拒绝

**错误**: 程序无法启动或热键不工作

**解决方案**:
1. 系统偏好设置 → 安全性与隐私 → 隐私
2. 授予以下权限：
   - 麦克风
   - 辅助功能
   - 自动化

### Windows常见问题

#### 防火墙警告

**解决方案**: 允许程序通过防火墙（本地通信）

### 识别失败

1. **确认服务器运行**:
   ```bash
   curl http://127.0.0.1:6008/health
   ```

2. **检查配置文件**:
   ```bash
   # Linux/macOS
   cat ~/.config/voice-typer/config.yaml

   # Windows
   type %APPDATA%\voice-typer\config.yaml
   ```

3. **查看日志**:
   ```bash
   # Linux/macOS
   tail -50 ~/.config/voice-typer/app.log

   # Windows
   type %APPDATA%\voice-typer\app.log
   ```

## 高级功能

### LLM智能纠错

#### 服务端配置

```bash
./run.sh --llm-base-url https://api.openai.com/v1 \
         --llm-api-key sk-xxx \
         --llm-model gpt-4o-mini
```

#### 客户端配置

在 `config.yaml` 中设置：
```yaml
servers:
  - llm_recorrect: true
```

### 多服务器配置

```yaml
servers:
  - name: local
    host: "127.0.0.1"
    port: 6008
  - name: remote
    host: "192.168.1.100"
    port: 6008
    api_key: "your-api-key"
```

客户端会自动选择第一个可用的服务器。

### 自定义热键

支持多种热键组合：

```yaml
# macOS
hotkey:
  modifiers: ["cmd", "shift"]
  key: "v"

# Windows/Linux
hotkey:
  modifiers: ["ctrl", "alt"]
  key: "r"
```

支持的主键：
- 字母键：a-z
- 数字键：0-9
- 功能键：f1-f12
- 特殊键：space, tab, enter

## 开发者指南

### 项目结构

```
client_go/
├── main.go              # 程序入口
├── Makefile            # 构建脚本
├── internal/           # 内部包
│   ├── config/         # 配置管理
│   ├── audio/          # 音频录制
│   ├── hotkey/         # 热键监听
│   ├── api/            # API客户端
│   ├── input/          # 文本输入
│   ├── ui/             # 用户界面
│   └── controller/     # 核心控制器
└── pkg/platform/       # 平台特定代码
```

### 运行测试

```bash
# 运行所有测试
make test

# 运行特定包的测试
go test ./internal/config

# 查看测试覆盖率
go test -cover ./...
```

### 代码格式化

```bash
# 格式化所有代码
make fmt

# 或手动格式化
gofmt -s -w .
```

### 代码检查

```bash
# 运行go vet
make vet

# 或使用golangci-lint（需要安装）
golangci-lint run
```

## 性能优化

### 减小二进制大小

```bash
# 使用UPX压缩
upx --best --lzma voicetyper

# 或在构建时使用更多优化标志
go build -ldflags="-s -w -extldflags=-static" -o voicetyper
```

### 启动时间优化

- 当前启动时间：~500ms
- 目标：<200ms

优化方向：
- 延迟加载模块
- 减少初始化操作

## 卸载

### Linux

```bash
# 删除二进制
sudo rm /usr/local/bin/voicetyper

# 删除配置（可选）
rm -rf ~/.config/voice-typer
```

### macOS

```bash
# 删除二进制
sudo rm /usr/local/bin/voicetyper

# 删除配置
rm -rf ~/.config/voice-typer
```

### Windows

```bash
# 删除二进制
del C:\Program Files\VoiceTyper\voicetyper.exe

# 删除配置
rmdir /s %APPDATA%\voice-typer
```

## 更新

### 检查更新

```bash
# 查看当前版本
voicetyper --version

# 从源码更新
git pull
make build
```

### 无缝升级

1. 停止运行的程序
2. 备份配置文件
3. 替换二进制文件
4. 重新启动

## 获取帮助

- **GitHub Issues**: [提交问题](../../issues)
- **文档**: 查看 [README.md](../README.md) 和 [DESIGN.md](DESIGN.md)
- **设计文档**: [client_go/DESIGN.md](DESIGN.md)

## 许可证

与主项目相同。详见项目根目录的LICENSE文件。
