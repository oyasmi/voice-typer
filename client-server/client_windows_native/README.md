# VoiceTyper Windows 原生客户端

[← 返回分体式项目](../README.md) · [VoiceTyper 主项目](../../README.md) · [服务端文档](../server/README.md) · [线上协议](../PROTOCOL.md)

基于 .NET 8 + WinForms 的 Windows 原生托盘客户端，与 macOS Swift 版共用同一套状态机模型和流式优先架构。当前版本 **3.0.0**。

它负责监听热键、录音、把音频送给服务端、把返回的文本粘贴到光标处。识别模型全部跑在[服务端](../server/README.md)。

**本文适合**：Windows 用户，以及要自行编译或二次开发的人。

> **大多数 Windows 用户应该用 [`windows/`](../../windows/README.md) 下的一体化 VoiceTyper**：
> 识别引擎直接内置进应用，不需要单独部署 Python 服务端（⚠️ 尚未完成真机验证，见其
> `DESIGN.md` 的风险清单）。本目录是分体式架构下的 Windows 客户端一半，适合已有远程/局域网
> 服务端、或多台设备共用一台服务端的场景。

---

## 目录

- [功能与限制](#功能与限制)
- [系统要求](#系统要求)
- [安装与使用](#安装与使用)
- [配置](#配置)
- [架构](#架构)
- [关键实现](#关键实现)
- [构建](#构建)
- [日志与排障](#日志与排障)

---

## 功能与限制

**支持**

- 系统托盘常驻，图标由程序绘制，随状态变化
- 按住热键录音，松开自动识别并粘贴
- 流式实时预览：录音时 HUD 顶端持续显示识别文本，且会自我修正
- 非流式兼容：服务端加了 `--no-streaming` 时，取消勾选「流式识别」即可切到 HTTP 路径
- **并发会话**：前一次识别还没回来就可以再按热键开始下一段；旧会话的结果回来时静默插入，不打断当前录音
- 依赖极轻：只有 `NAudio` 和 `YamlDotNet` 两个 NuGet 包，无 Python 运行时
- 单实例保护（全局 Mutex），重复启动会直接退出
- Per-Monitor V2 DPI 感知，多显示器混合缩放下 HUD 不糊
- 短录音过滤：0.3 秒以下的误触录音直接丢弃，不发请求

**不支持 / 已知限制**

- **没有 Esc 取消**。录音一旦开始，只能松开热键让它走完。
- **只支持明文 `http` / `ws`**。配置里没有 `scheme` 字段，连不了 HTTPS 服务端（macOS 客户端支持）。
- **管理员权限窗口收不到热键**。低级键盘钩子受 UIPI 限制，普通权限运行的本程序无法在提权窗口（如以管理员身份运行的终端）中拿到按键。
- 只能按住说话，没有按一次开始、再按一次结束的切换模式。
- 仅 x64，未提供 ARM64 构建。

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 / 11（x64） |
| 运行时 | 便携版需 [.NET Desktop Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)；完整版自带，无需安装 |
| 服务端 | 一台可访问的 [voice-typer-server](../server/README.md) |
| 构建 | .NET 8 SDK（仅自行编译时） |

---

## 安装与使用

### 选哪个版本

| 产物 | 体积 | 说明 |
| --- | --- | --- |
| `VoiceTyper-<版本>-win-x64.exe` | ~30–50MB | **完整版**，自带 .NET 运行时，下载即用。拿不准选这个 |
| `VoiceTyper-<版本>-win-x64-portable.exe` | ~3MB | **便携版**，需要先装 .NET Desktop Runtime 8.0。适合已有运行时、在意体积的场景 |

两者功能完全一致，都是单文件 exe，没有安装程序。放哪个目录都行。

> 注意与一体化 Windows App 的 `VoiceTyper-<版本>-win-x64-setup.exe`（安装程序形态）区分：
> 名字相近但不是同一个东西——一体化版内置识别引擎、无需服务端，本目录产物是分体式客户端，
> 必须另行部署[服务端](../server/README.md)。

### 首次使用

1. 双击 exe，应用驻留系统托盘。
2. 单击托盘图标（或右键菜单「权限与设置...」）打开设置窗口。
3. 「连接」页填服务地址，点**测试连接**确认服务端可达。
4. 按住 `Ctrl + F2`（默认热键）开始说话，HUD 出现并显示实时预览。
5. 松开热键，最终文本粘贴到当前光标位置。

录音不足 **0.3 秒**视为误触，直接丢弃：流式路径关闭 WebSocket 而不发 `finalize`，非流式路径不发出 HTTP 请求（`VoiceTyperController.MinimumRecordingDuration`）。

### 麦克风权限

Windows 会在首次录音时拦截。如果按下热键报「麦克风权限被拒绝」，到**设置 → 隐私和安全性 → 麦克风**，确认「让桌面应用访问你的麦克风」是开启的。设置窗口顶部也会在检测到这种情况时显示提示条和快捷跳转按钮。

### 服务端模式必须匹配

服务端默认跑流式（v1.2.0+），此时客户端保持勾选「流式识别」。如果服务端显式加了 `--no-streaming`，客户端必须取消勾选——两种模式注册的路由不同，配错了会直接连不上。详见[服务端文档](../server/README.md#两种运行模式)。

---

## 配置

路径：`%APPDATA%\voice_typer\config.yaml`，与 macOS / Linux 客户端格式兼容。

设置窗口有两页：**连接**（服务地址、端口、API Key、流式开关、LLM 纠错开关、测试连接）和**热键**。

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60           # 秒
  api_key: ""           # 远程服务端且服务端配了 --api-keys 时必填
  llm_recorrect: true   # 客户端侧开关，服务端也得配好 LLM 才生效
  streaming: true       # 流式（推荐）；服务端用了 --no-streaming 时改为 false
hotkey:
  modifiers:
    - "ctrl"            # ctrl / alt / shift / win
  key: "f2"
ui:
  opacity: 0.85         # HUD 背景不透明度
  width: 320
  height: 90
```

> 没有 `scheme` 字段——本客户端硬编码 `http://` 与 `ws://`。要接 HTTPS 服务端目前只能用 macOS 客户端。

---

## 架构

```
Program.cs                          入口：单例 Mutex + DPI/视觉样式 + 全局异常兜底 + Application.Run
App/
  TrayApplicationContext.cs         ApplicationContext 子类，承载托盘生命周期
  AppCoordinator.cs                 中央调度：装配各服务、状态机回调、生命周期
Core/
  AppConfig.cs                      YAML 模型（server / hotkey / ui）
  AppState.cs                       状态枚举 + 显示信息
  ConfigStore.cs                    YamlDotNet 读写
  VoiceTyperController.cs           核心状态机 + 流式/非流式双路径
Services/
  HotkeyService.cs                  SetWindowsHookEx(WH_KEYBOARD_LL) 全局钩子
  AudioCaptureService.cs            WASAPI 采集 + WDL 重采样到 16kHz/mono/float32 + 600ms 分帧
  StreamingASRClient.cs             ClientWebSocket
  ASRClient.cs                      HttpClient（POST /recognize 与 /health）
  TextInsertionService.cs           剪贴板 + SendInput Ctrl+V
UI/
  TrayController.cs                 NotifyIcon + 上下文菜单 + 程序绘制的状态图标
  RecordingHud.cs                   无边框置顶 Form + 呼吸点 + 流式预览
  SetupForm.cs                      两页 TabControl（连接 / 热键）+ 麦克风权限提示条
Support/
  AppLog.cs                         滚动文件日志
  Constants.cs                      版本、路径、采样率、分帧大小
  NativeMethods.cs                  P/Invoke 声明
  UiDispatcher.cs                   把非 UI 线程的回调投递回 UI 线程
```

### 线程模型

所有状态迁移和回调都在 **UI 线程**上完成。音频采集回调、WebSocket 接收循环跑在后台线程，一律经 `UiDispatcher.Post` 派回 UI 线程再触碰控制器状态。这样 `VoiceTyperController` 内部不需要任何锁。

### 状态机

```
Idle ──按住热键──▶ Recording ──松开──▶ Recognizing ──▶ Inserting ──▶ Idle
                                                                      │
                             Error(message) ◀────────────────────────┘
```

`AppStateInfo` 携带状态和展示文案，`StateChanged` 事件同时驱动托盘图标和 HUD。

---

## 关键实现

### 全局热键

用 `SetWindowsHookExW(WH_KEYBOARD_LL)` 装低级键盘钩子，进程内只允许一个实例。

- 修饰键状态用 `GetAsyncKeyState` 实时查询，而不是维护自己的按下集合——避免焦点切换、Alt+Tab 之类的场景漏掉抬起事件后状态卡死。
- 按下判定要求修饰键**精确匹配**：`Ctrl+F2` 不会被 `Ctrl+Shift+F2` 触发。
- 主键抬起就触发 `OnRelease`，不要求修饰键也还按着。
- 钩子不吞事件，热键组合仍会传给前台应用。

**限制**：低级键盘钩子受 UIPI 约束。以普通权限运行时，提权窗口（管理员终端、部分系统对话框）中的按键拿不到，热键在那些窗口里不生效。以管理员身份运行本程序可以绕过，但会带来别的问题（拖放失效等），不推荐。

### 音频采集

`NAudio` 的 `WasapiCapture`（事件同步模式）拿默认输入设备的原始格式，用 **WDL 重采样器**转成 16kHz / mono / float32。选 WDL 而非 `MediaFoundationResampler` 是因为它纯托管、不依赖 MF DLL，且对语音的 16kHz 重采样质量足够。

每凑满 **9600 样本（600ms）**通过 `OnChunk` 发出；停止录音时把剩余尾音通过 `OnTailChunk` 发出——**即使尾音为空也会触发**，因为这个回调同时承担「录音结束」的信号职责：流式路径在这里发 `finalize`，非流式路径在这里拼接整段并 POST。

启动失败会包成 `AudioStartException` 并区分 `IsAccessDenied`，好让 UI 给出「麦克风权限被拒绝」这种可操作的提示，而不是一句笼统的失败。

### 文本插入

只有一条路径：剪贴板 + `SendInput` 模拟 `Ctrl+V`。（macOS 客户端还有一条 Accessibility 直写的路径，Windows 没有对应能力。）

流程：

1. 备份当前剪贴板的 `IDataObject`（保留所有数据格式，不只是文本）；
2. 写入识别文本，`Clipboard.SetDataObject` 带 5 次重试 × 20ms 延迟——剪贴板是全局独占资源，被其他程序短暂占用很常见；
3. `SendInput` 发 Ctrl+V；
4. 500ms 后还原剪贴板，但**只在剪贴板内容仍是我们写进去的那段文本时**才还原，避免覆盖用户在这期间的复制操作。

新一次插入会取消上一次待执行的还原任务，防止两次操作互相干扰。

### 并发会话

允许在前一次识别未完成时开始新一段录音。判定靠 `ReferenceEquals(_streamingClient, client)` 比较对象引用：

- 是当前会话 → 正常走清理 + 插入 + 状态迁移；
- 不是当前会话（已被新录音取代）→ 关掉旧连接，**静默插入**文本，完全不触碰当前会话的状态。

同样地，`HandleFinalText` 在插入完成后会检查 `_isRecording`，若已有新一轮录音在进行就不把状态拉回 `Idle`。

WebSocket 连接是异步的，连接期间用户可能已经按了第二次热键，所以 `StartAudioCaptureForStreaming` 在真正开始录音前会再确认一次会话身份。

### 流式预览是全量文本

`partial.text` 是**当前完整的预览转写**，客户端直接替换而非拼接：

```csharp
client.OnPartial = text => { _accumulatedPreview = text; ... };
```

服务端的预览是对已累积音频整段重跑，后一次结果会修正前一次的文字，增量语义表达不了这种回溯修改。见 [`PROTOCOL.md`](../PROTOCOL.md) §4.3。上屏文本永远来自 `final` 帧。

---

## 构建

```bat
REM 还原依赖
dotnet restore

REM 调试运行
dotnet run

REM 发布两种产物
build.bat
```

`build.bat` 的流程：检查 .NET SDK → 从 csproj 读 `Version` → 清理 `dist`/`bin`/`obj` → restore → build → 发布便携版（`--self-contained false`）→ 发布完整版（`--self-contained true` + 压缩 + 内嵌原生库）→ 按版本号重命名，最后打印两个产物的实际体积。

产物在 `dist\`：

```
VoiceTyper-<版本>-win-x64.exe            完整版（self-contained，单文件，压缩）
VoiceTyper-<版本>-win-x64-portable.exe   便携版（framework-dependent，单文件）
Assets\icon.ico
```

### 改版本号

改 `VoiceTyper.csproj` 里的 `Version` / `AssemblyVersion` / `FileVersion` 三处，以及 `app.manifest` 的 `assemblyIdentity version`。`build.bat` 通过 `dotnet msbuild -getProperty:Version` 读取，产物文件名随之变化。

### 应用清单

`app.manifest` 声明了：`asInvoker`（不请求提权）、Per-Monitor V2 DPI 感知、长路径支持，以及 Windows 7–11 的 `supportedOS` 兼容标记。

---

## 日志与排障

日志路径：`%APPDATA%\voice_typer\client.log`，2MB 滚动，保留 3 份备份（`client.log.1` ~ `client.log.3`）。

记录了热键事件、录音启停、WebSocket 帧收发、插入结果和所有异常。日志和配置文件在同一个目录，托盘菜单的「打开配置目录」可以直接跳过去。

托盘右键菜单还有「重新连接服务」，服务端重启后不必重启客户端。

### 热键没反应

1. 确认没有第二个 VoiceTyper 实例在跑（单例 Mutex 会让第二个直接退出，但别的分支版本可能占着钩子）。
2. 确认当前焦点窗口不是以管理员身份运行的——见上文 UIPI 限制。
3. 检查是否与其他软件的全局热键冲突，换一个组合试试。
4. 看日志里有没有 `SetWindowsHookExW` 失败的记录。

### 连不上服务端

设置窗口点「测试连接」看具体错误，或直接：

```bat
curl http://127.0.0.1:6008/health
```

返回里的 `streaming` 字段要和客户端的「流式识别」勾选状态一致，`ready` 必须为 `true`。

### 文字没粘贴进去

1. 目标程序可能不接受模拟的 `Ctrl+V`（部分远程桌面、游戏、密码框）。文本此时还在剪贴板里，手动 `Ctrl+V` 即可。
2. 剪贴板被其他程序长时间独占。日志里会有 `Clipboard.SetDataObject` 重试耗尽的记录。
3. 目标窗口是提权窗口——`SendInput` 同样受 UIPI 限制。

### HUD 位置或大小不对

`ui.opacity` 可调，`ui.width` / `ui.height` 影响 HUD 尺寸。多显示器混合缩放下如果仍有异常，附上日志和显示器配置提 issue。

---

## 相关链接

- [VoiceTyper 主项目](../../README.md)
- [一体化 Windows App（推荐大多数用户）](../../windows/README.md)
- [服务端文档](../server/README.md)
- [客户端 ↔ 服务端协议](../PROTOCOL.md)
- [macOS Swift 原生客户端](../client_macos_swift/README.md)
