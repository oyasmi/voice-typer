# VoiceTyper macOS Client (Swift)

基于 `AppKit` 的原生 macOS 菜单栏语音输入客户端，是当前推荐使用的 macOS 实现。

👉 **如果您正在寻找完整的项目说明、服务端介绍以及其他平台客户端，请参阅 [VoiceTyper 主项目](../README.md)。**

Swift 版优先解决以下问题：

- 首次安装与权限引导
- 原生状态栏体验与稳定性
- 文本注入兼容性
- 分发、打包与后续签名/公证链路

## 功能特性

- 🎤 **原生菜单栏应用** - 使用 `AppKit` 实现，常驻状态栏，显示实时工作状态
- ⌨️ **全局热键** - 按住热键录音，松开自动识别并插入文字
- 🌐 **Fn 键支持** - 支持绑定 macOS `Fn`（地球仪）键作为热键
- 🔐 **权限集中引导** - 首次启动自动检查麦克风、辅助功能、输入监控与服务连接
- ⚙️ **设置 UI 化** - 可直接在界面中修改服务地址、热键与用户热词
- 📚 **用户热词编辑** - 在应用内直接编辑主热词文件并立即生效
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音

## 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Apple Silicon Mac
- 完整 Xcode（仅开发和本地构建时需要）

## 安装步骤与常规使用

关于服务端安装、整体使用方式以及其他平台客户端，请参考主目录的 [macOS 客户端说明](../README.md#macos-客户端)。

### 从 Release 安装

1. 从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper-macOS.dmg`
2. 打开 `DMG`
3. 将 `VoiceTyper.app` 拖到 `Applications`
4. 从“应用程序”中打开 `VoiceTyper`
5. 如果首次打开被系统拦截，到“系统设置 > 隐私与安全性”点击“仍要打开”

更简洁的对外安装文案见 [docs/install.md](/Users/oyasmi/projects/voice-typer/client_macos_swift/docs/install.md:1)。

### 首次启动

首次启动后，应用会自动检查：

- **麦克风**：用于录音
- **辅助功能**：用于文本输入与部分系统交互
- **输入监控**：用于监听全局热键，尤其是 `Fn`
- **服务连接**：用于确认语音识别服务端可用

如果存在未完成项，会自动弹出“权限与设置”窗口。完成后即可直接使用。

### 开始使用

1. 启动应用后，菜单栏会出现 VoiceTyper 图标
2. **按住热键**（默认 `Fn` / 地球仪键，也可改为组合键）开始录音
3. **松开** 自动识别并插入文本到当前光标位置
4. 录音不足 0.3 秒会被自动忽略

## 设置与配置

Swift 版默认通过 UI 管理常用配置，不再要求手工编辑 YAML。

在“权限与设置”窗口中，可以直接配置：

- 服务地址、端口、API Key
- 是否启用 `LLM` 纠错
- 热键模式（`Fn` 或组合键）
- 用户热词

配置文件仍然保存在：

```text
~/.config/voice_typer/config.yaml
```

用户热词主文件默认位于：

```text
~/.config/voice_typer/hotwords.txt
```

当前写回的配置格式类似：

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60
  api_key: ""
  llm_recorrect: true
hotkey:
  modifiers: []
  key: "fn"
hotword_files:
  - "hotwords.txt"
ui:
  opacity: 0.85
  width: 240
  height: 70
```

> 说明：macOS 客户端不配置本地 `device`；运行设备由服务端决定，当前支持 `cpu` / `cuda` / `cuda:N` 等。

## 开发与构建指南

以下内容主要面向希望自行编译或进行二次开发的开发者。

### 打开工程

```bash
cd client_macos_swift
open VoiceTyper.xcodeproj
```

### 命令行构建

```bash
cd client_macos_swift
./build_xcode.sh
```

构建完成后会生成：

- `dist/VoiceTyper.app`
- `dist/VoiceTyper-macOS.zip`
- `dist/VoiceTyper-macOS.dmg`

其中 `DMG` 内会附带一个极简 `INSTALL.txt`，用于引导用户完成拖拽安装和首次放行。

### 重新生成工程

如果你修改了源码目录结构或新增了 Swift 源文件，可以重新生成 `.xcodeproj`：

```bash
cd client_macos_swift
ruby scripts/generate_xcodeproj.rb
```

## 旧版 Python 客户端

仓库中仍保留一个 Python 技术栈的 macOS 客户端实现，用于兼容旧版本或参考实现：

- [client_macos](../client_macos/README.md)
