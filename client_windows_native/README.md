# VoiceTyper Windows 原生客户端

基于 .NET 8 + WinForms 的 Windows 原生客户端，与 macOS Swift 版采用相同的状态机与流式优先架构。

## 亮点

- **流式识别（默认）**：WebSocket 实时回传，HUD 上同步显示逐字预览。
- **非流式兼容**：服务端使用 `--no-streaming` 时自动走 HTTP `/recognize`（在设置里取消勾选"流式识别"即可）。
- **完全原生**：仅依赖 `NAudio` 与 `YamlDotNet` 两个 NuGet 包，无 Python 运行时。
- **托盘 + 设置窗口**：托盘单击打开设置；设置窗口含连接、热键、用户热词三页。
- **并发会话**：可以在前一次识别尚未完成时再次按下热键开始新一段录音；旧会话回来时静默插入。

## 系统要求

- Windows 10 / 11
- 便携版需 `.NET Desktop Runtime 8.0`，完整版自带运行时无需安装

## 服务端要求

服务端默认就是流式模式（`v1.1.0+`），本客户端勾选"流式识别"即可。如果你的服务端启动时显式加了 `--no-streaming`，请在客户端设置中取消"流式识别"以匹配。详见 [服务端文档](../server/README.md)。

## 构建

```bat
REM 还原依赖
dotnet restore

REM 调试运行
dotnet run

REM 发布两种产物（便携 + 完整）
build.bat
```

构建产物：
- `dist/VoiceTyper-{版本}-win-x64-portable.exe` — 便携版 (~3MB)，需 .NET Desktop Runtime 8.0
- `dist/VoiceTyper-{版本}-win-x64.exe` — 完整版 (~30-50MB)，开箱即用

## 使用

1. 双击 `VoiceTyper.exe`，应用驻留在系统托盘
2. 单击托盘图标或选择"权限与设置..."打开设置窗口
3. 在"连接"标签页填写服务地址，点"测试连接"确认服务端可达
4. 按住 `Ctrl + F2`（默认热键）开始说话，松开自动识别并插入到当前光标位置
5. HUD 顶端实时显示录音状态与流式预览文本

录音不足 0.3 秒不会触发识别（由服务端兜底处理）。

## 配置文件

路径：`%APPDATA%\voice_typer\config.yaml`（与 macOS / Python 版完全兼容）

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60
  api_key: ""
  llm_recorrect: true
  streaming: true       # 流式（推荐）；若服务端用了 --no-streaming，请改为 false
hotkey:
  modifiers:
    - "ctrl"            # 支持: ctrl / alt / shift / win
  key: "f2"
hotword_files:
  - "hotwords.txt"
ui:
  opacity: 0.85
  width: 320
  height: 90
```

热词：`%APPDATA%\voice_typer\hotwords.txt`，每行一个词，`#` 开头为注释。

## 日志

路径：`%APPDATA%\voice_typer\client.log`，自动滚动（2MB × 3 份）。

## 架构

```
Program.cs                          入口（单例锁 + 同步上下文 + 启动 TrayApplicationContext）
App/
  AppCoordinator.cs                 中央调度（装配、状态机回调、生命周期）
  TrayApplicationContext.cs         ApplicationContext 子类
Core/
  AppConfig.cs                      YAML 模型
  AppState.cs                       状态枚举 + 显示信息
  ConfigStore.cs                    YAML 读写 + 热词管理
  VoiceTyperController.cs           核心状态机 + 流式/非流式双路
Services/
  HotkeyService.cs                  SetWindowsHookEx(WH_KEYBOARD_LL)
  AudioCaptureService.cs            WASAPI 抓音 + 16kHz/mono/float32 重采样 + 600ms 分帧
  StreamingASRClient.cs             ClientWebSocket
  ASRClient.cs                      HttpClient
  TextInsertionService.cs           剪贴板 + SendInput Ctrl+V
UI/
  TrayController.cs                 NotifyIcon + 上下文菜单 + 程序绘制状态图标
  RecordingHud.cs                   无边框置顶 Form + 呼吸点 + 流式预览
  SetupForm.cs                      三页 TabControl
Support/
  AppLog.cs                         滚动文件日志
  Constants.cs                      版本号、路径常量
  NativeMethods.cs                  P/Invoke
  UiDispatcher.cs                   非 UI 线程回调投递到 UI 线程
```

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [服务端文档](../server/README.md)
- [macOS Swift 原生客户端](../client_macos_swift/)
