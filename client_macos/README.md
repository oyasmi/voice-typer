# VoiceTyper macOS Client

macOS 菜单栏语音输入客户端，提供基于 FunASR 的离线语音识别功能。

👉 **如果您正在寻找完整的项目说明、服务端介绍以及其他平台客户端，请参阅 [VoiceTyper 主项目](../README.md)。**

由于 macOS 是本产品的主要支持平台之一，因此大部分的核心使用教程（如下载、授权、快捷键配置）都已包含在主项目的文档中。

## 功能特性

- 🎤 **菜单栏应用** - 简洁的菜单栏图标，显示实时工作状态
- ⌨️ **全局热键** - 按住热键录音，松开自动识别并插入文字
- 🌐 **Fn 键支持** - 支持绑定 macOS Fn（地球仪）键作为热键
- 🔒 **完全离线** - 本地处理音频，无需上传到云端
- 🔧 **高度可定制** - 自定义热键、透明度和识别服务地址
- 🎯 **用户词库** - 支持添加专业术语或常用词汇
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音

## 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Python 3.10+ (推荐 3.12)
- Apple Silicon (M1/M2/M3/M4) 系列芯片效果最佳

## 安装步骤与常规使用

关于如何下载、安装运行及首次配置说明（如系统权限的授予），请参考主目录的 [macOS 客户端说明](../README.md#macos-客户端)。

## 开发与构建指南

以下内容主要面向希望自行编译或进行二次开发的开发者：

### 1. 克隆仓库并安装依赖

```bash
cd client_macos
pip install -r requirements.txt
```

### 2. 本地运行调测

确保主项目的 VoiceTyper 服务端已启动。默认连接地址为 `127.0.0.1:6008`。

执行以下命令直接在 Python 环境下运行：

```bash
python main.py
# 或使用 Makefile 封装命令
make run
```

### 3. 构建发布版

使用 `PyInstaller` 脚本构建独立的 `.app` 文件（无需用户环境中安装 Python）：

```bash
./build.sh
```

构建完成后，在程序的 `dist` 目录下可以找到打包好的 `VoiceTyper.app`，可以直接双击执行或分发给其他用户。

## 配置参考（开发环境）

配置文件默认位于：`~/.config/voice_typer/config.yaml`。启动程序后会自动生成，如果您需要手工创建：

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  llm_recorrect: true   # 开启 LLM 纠错选项，需服务端配合启用配置

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

> 说明：macOS 客户端不配置本地 `device`；运行设备由服务端决定，当前支持 `cpu` / `cuda` / `cuda:N` 等。
