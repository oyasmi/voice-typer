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

### 平台要求
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

### Windows

由于本客户端深度集成了 Win32 API 以获得轻量级和原生体验，**仅支持在 Windows 上运行**（或通过兼容层，但不保证稳定性）。

#### 1. 环境准备

需要安装以下工具：

1.  **Go 1.24+**: [下载链接](https://golang.org/dl/)
2.  **GCC 编译器**: 用于编译 CGO 代码 (malgo)。推荐使用 [MinGW-w64](https://www.mingw-w64.org/) 或 [MSYS2](https://www.msys2.org/)。

**MSYS2 安装步骤 (推荐):**
1. 下载并安装 MSYS2。
2. 打开 MSYS2 MINGW64 终端。
3. 运行: `pacman -S mingw-w64-x86_64-gcc`

#### 2. 从源码构建

**使用 PowerShell:**

```powershell
# 1. 克隆代码
git clone https://github.com/yourusername/voice-typer.git
cd voice-typer\client_go

# 2. 从 go.mod 安装依赖
go mod download

# 3. 设置环境变量并编译
$env:GOOS="windows"
$env:GOARCH="amd64"
$env:CGO_ENABLED="1"

# -H=windowsgui 隐藏黑色命令行窗口
go build -ldflags="-s -w -H=windowsgui" -o VoiceTyper.exe main.go
```

**使用 Makefile (Git Bash / MSYS2):**

```bash
make build
```

#### 3. 运行

双击生成的 `VoiceTyper.exe` 即可运行。程序会出现在系统托盘中。

---


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

# Windows
GOOS=windows GOARCH=amd64 CGO_ENABLED=1 go build -ldflags="$LDFLAGS" -o voicetyper.exe

```

## 配置

### 配置文件位置

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
    - "ctrl"   # Windows/Linux: ctrl
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

编辑 `%APPDATA%\voice-typer\hotwords.txt`：

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
2. 按住热键（默认：Ctrl+Space）
3. 说话
4. 松开热键
5. 识别的文本自动插入到光标位置

### 4. 系统托盘菜单

- **Status**: 当前状态
- **Enable/Disable**: 启用/禁用语音输入
- **Open Config**: 打开配置文件
- **About**: 关于信息
- **Quit**: 退出程序

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
   type %APPDATA%\voice-typer\config.yaml
   ```

3. **查看日志**:
   ```bash
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
