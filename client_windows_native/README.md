# VoiceTyper Windows 原生客户端

VoiceTyper 的 Windows 原生客户端，基于 .NET 8 + WinForms 实现。提供全局热键录音、实时状态指示和离线语音识别功能。

与 [client_macos_swift](../client_macos_swift/) 对齐的设计水准，替代原有 Python 实现 ([client_windows](../client_windows/))。

## 相比 Python 版的改进

- **分发体积**: ~3MB (便携版) / ~30-50MB (完整版)，vs Python 版 40-80MB
- **无杀毒误报**: 原生 .NET 应用不会被 Windows Defender 误报
- **启动速度**: <0.5 秒冷启动
- **零 Python 依赖**: 无需安装 Python 环境
- **修复热词编码**: 直接发送 UTF-8 原文（修复 Python 版的 percent-encoding bug）
- **验证 HTTP 状态码**: 对齐 macOS Swift 版修复后的行为

## 环境要求

- Windows 10/11
- .NET Desktop Runtime 8.0（便携版需要）或不需要任何运行时（完整版）

### 开发环境

- .NET 8 SDK
- Visual Studio 2022 或 Rider

## 构建

### 从源码构建

```bash
# 还原依赖
dotnet restore

# 调试运行
dotnet run

# 发布（同时产出便携版和完整版）
build.bat
```

构建产物:
- `dist/VoiceTyper-{版本}-win-x64-portable.exe` — 便携版 (~3MB)，需 .NET Desktop Runtime 8.0
- `dist/VoiceTyper-{版本}-win-x64.exe` — 完整版 (~30-50MB)，独立运行

## 使用方法

1. **启动**: 双击 `VoiceTyper.exe`，应用将驻留在系统托盘
2. **录音**: 按住 `Ctrl + F2`（默认热键）开始说话
3. **输入**: 松开按键，识别结果自动插入到当前光标位置
4. **托盘菜单**: 右键点击托盘图标，可打开配置文件、词库或退出

> 录音不足 0.3 秒将被自动忽略。

## 配置

配置文件路径: `%APPDATA%\voice_typer\config.yaml`（与 Python 版完全兼容）

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: ""
  llm_recorrect: true

hotkey:
  modifiers:
    - "ctrl"            # 支持: ctrl, alt, shift, win_l, win_r
  key: "f2"

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70
```

### 自定义词库

编辑 `%APPDATA%\voice_typer\hotwords.txt`，每行一个词:

```text
# 专业术语
FunASR
ChatGPT
GitHub
```

### 配置兼容性

配置文件和词库与 Python 版 (`client_windows`) 完全兼容，可无缝切换。

## 架构

```
Program.cs                  — 入口点 (ApplicationContext)
App/
  AppCoordinator.cs         — 中心调度 + 生命周期管理
Core/
  AppState.cs               — 状态枚举
  AppConfig.cs              — 配置数据模型
  ConfigStore.cs            — YAML 配置读写
  VoiceTyperController.cs   — 核心控制器（状态机）
Services/
  HotkeyService.cs          — 全局热键 (SetWindowsHookEx)
  AudioCaptureService.cs    — 录音 (NAudio WASAPI)
  ASRClient.cs              — HTTP 识别客户端
  TextInsertionService.cs   — 文本插入 (Clipboard + SendInput)
UI/
  TrayIconManager.cs        — 系统托盘
  RecordingOverlay.cs       — 录音浮窗 (Phase 2)
Support/
  Constants.cs              — 应用常量
  AppLog.cs                 — 日志
  NativeInterop.cs          — Win32 P/Invoke
```

## 依赖

仅 2 个 NuGet 包:
- [NAudio](https://github.com/naudio/NAudio) — 音频捕获 + 重采样
- [YamlDotNet](https://github.com/aaubry/YamlDotNet) — YAML 配置解析

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [服务端文档](../server/README.md)
- [macOS 原生客户端](../client_macos_swift/)
- [Windows Python 客户端](../client_windows/)（已被本项目替代）
