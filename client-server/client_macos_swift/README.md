# VoiceTyperClient — macOS 分体式客户端 (Swift)

[← 返回分体式项目](../README.md) · [VoiceTyper 主项目](../../README.md) · [服务端文档](../server/README.md) · [线上协议](../PROTOCOL.md) · [一体化 App](../../macos/README.md)

基于 Swift + AppKit 的原生 macOS 菜单栏客户端。当前版本 **2.7.0**，应用名 **VoiceTyperClient**，
Bundle ID `com.voicetyper.client`。

> **大多数 macOS 用户应该用 [`macos/`](../../macos/README.md) 下的一体化 VoiceTyper**：
> 拖进 Applications、打开即用，不需要单独部署 Python 服务端。本目录（`client_macos_swift/`）
> 是分体式架构下的客户端一半，配合独立部署的 [服务端](../server/README.md) 使用，适合：
> 已有远程/局域网 GPU 服务端、需要多设备共享一份服务端、或需要 paraformer 等
> 一体化 App 未提供的能力的场景。

它负责的事只有四件：监听热键、录音、把音频送给服务端、把返回的文本插到光标处。识别模型全部跑在[服务端](../server/README.md)。

**本文适合**：需要分体式部署的 macOS 用户，以及要自行编译或二次开发的人。

---

## 目录

- [功能与限制](#功能与限制)
- [系统要求](#系统要求)
- [安装](#安装)
- [权限](#权限)
- [使用](#使用)
- [设置与配置文件](#设置与配置文件)
- [架构](#架构)
- [关键实现](#关键实现)
- [构建](#构建)
- [日志与排障](#日志与排障)

---

## 功能与限制

**支持**

- 原生菜单栏常驻，图标随状态变化（就绪 / 录音 / 识别 / 输入 / 暂停 / 错误）
- 按住热键录音，松开自动识别并插入文本
- **Fn（地球仪）键**可作为热键——这是 macOS 上少见的能力，靠 `CGEventTap` 实现
- 流式实时预览：录音时 HUD 浮窗持续显示识别文本，且会自我修正
- 录音中按 **Esc** 取消，不触发识别与插入
- 首次启动集中引导三项权限 + 服务端连通性检查
- 服务端未就绪时自动退避重连（2/3/5/8/10 秒）
- 设置全部 UI 化：权限、连接、热键、通用四页
- 开机自启、HUD 不透明度可调
- HTTPS / WSS（`server.scheme`）—— **三个客户端里只有 macOS 支持**
- 菜单栏可暂停听写（停热键监听与服务轮询）

**不支持 / 已知限制**

- 只能按住说话，没有「按一次开始、再按一次结束」的切换模式
- 热键主键限于字母、数字、`space`/`tab`/`enter`、`F1`–`F12`，以及独立的 `fn`
- 应用未做签名公证，首次打开需在「系统设置 → 隐私与安全性」手动放行
- `ui.width` / `ui.height` 已废弃：HUD 是胶囊形态，尺寸随内容自适应，这两个字段仅为跨平台配置兼容保留

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 系统 | macOS 14.0 (Sonoma) 或更高 |
| 架构 | Apple Silicon 与 Intel 均可（分发包提供 arm64 / x86_64 / universal 三种） |
| 服务端 | 一台可访问的 [voice-typer-server](../server/README.md)，流式或非流式均可 |
| Xcode | 仅自行构建时需要 |

---

## 安装

### 从 Release 安装

1. 从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载对应架构的 `VoiceTyperClient-<版本>-macOS-<arch>.dmg`。拿不准就选 `universal`。
2. 打开 DMG，把 `VoiceTyperClient.app` 拖进 `Applications`。
3. 从「应用程序」打开 VoiceTyperClient。
4. 首次打开若被系统拦截，到「系统设置 → 隐私与安全性」点「仍要打开」。

第 4 步是因为应用只做了 ad-hoc 签名，没有 Apple 开发者签名和公证。DMG 里附了一份精简的 `INSTALL.txt` 说明同样的流程，另见 [docs/install.md](docs/install.md)。

### 卸载

删掉 `/Applications/VoiceTyperClient.app`，再删配置目录 `~/.config/voice_typer/`。权限授予记录留在系统设置里，可手动移除。

---

## 权限

应用需要三项权限，缺一不可。首次启动会自动检测，有缺失就弹出「设置」窗口的「权限」页。

| 权限 | 用途 | 检测 API | 缺失时的表现 |
| --- | --- | --- | --- |
| **麦克风** | 录音 | `AVCaptureDevice.authorizationStatus` | 按下热键报「麦克风权限被拒绝」 |
| **辅助功能** | 文本插入（AX 直写 + 模拟粘贴） | `AXIsProcessTrusted()` | 识别成功但文字插不进去 |
| **输入监控** | 全局热键，尤其是 Fn 键 | `CGPreflightListenEventAccess()` | 热键完全无响应 |

设置页的「授权」按钮走系统请求弹窗（`AVCaptureDevice.requestAccess` / `AXIsProcessTrustedWithOptions` / `CGRequestListenEventAccess`）；已被拒绝过的权限系统不会二次弹窗，此时按钮改为直接跳转到对应的系统设置面板。

**权限没生效时的常规手段**：在「系统设置 → 隐私与安全性 → 对应项」里把 VoiceTyperClient 移除再重新添加，然后**完全退出并重启应用**。辅助功能和输入监控的授权状态是按可执行文件签名记录的，替换 app 后（比如覆盖安装新版本）经常需要重来一次。

彻底重置某项授权：

```bash
tccutil reset Microphone com.voicetyper.client
tccutil reset Accessibility com.voicetyper.client
tccutil reset ListenEvent com.voicetyper.client
```

---

## 使用

1. 启动后菜单栏出现 VoiceTyperClient 图标。
2. **按住热键**（默认 `Fn` / 地球仪键）开始录音，HUD 浮窗出现。
3. 说话。流式模式下 HUD 会实时显示识别文本，并随着你继续说而自我修正。
4. **松开热键**，服务端做一次整段复识别，最终文本插入当前光标位置。
5. 录音中随时按 **Esc** 取消，不会识别也不会插入。

录音不足 **0.3 秒**视为误触，直接丢弃：流式模式下关闭 WebSocket 不发 `finalize`，非流式模式下不发 HTTP 请求（`VoiceTyperController.minimumRecordingDuration`）。

菜单栏图标状态：

| 状态 | 图标 | 含义 |
| --- | --- | --- |
| 就绪 | `mic` | 等待热键 |
| 录音中 | `mic.fill` | 正在录音 |
| 识别中 | `waveform` | 已松手，等服务端返回 |
| 输入中 | `character.cursor.ibeam` | 正在插入文本 |
| 连接服务中 | 旋转箭头 | 服务端未就绪，后台退避重连 |
| 需要授权 | 警告三角 | 有权限未授予 |
| 已暂停 | `mic.slash` | 从菜单主动暂停 |

---

## 设置与配置文件

常用配置都在「设置」窗口里，不需要手工编辑 YAML。窗口分四页：

| 页 | 内容 |
| --- | --- |
| **权限** | 三项权限状态、授权按钮、跳转系统设置 |
| **连接** | 服务地址、端口、API Key、流式开关、LLM 纠错开关、测试连接 |
| **热键** | Fn 模式或组合键模式，支持按键录制 |
| **通用** | 开机自启、HUD 背景不透明度（拖动时实时预览） |

配置文件仍写在：

```
~/.config/voice_typer/config.yaml
```

完整字段：

```yaml
server:
  scheme: "http"        # http / https；ws/wss 由它派生。仅 macOS 客户端支持
  host: "127.0.0.1"
  port: 6008
  timeout: 60           # 秒
  api_key: ""           # 远程服务端且服务端配了 --api-keys 时必填
  llm_recorrect: true   # 客户端侧开关，服务端也得配好 LLM 才生效
  streaming: true       # true=WebSocket 流式（默认）；false=HTTP 非流式
hotkey:
  modifiers: []         # ctrl / cmd / alt / shift；Fn 模式下留空
  key: "fn"             # fn，或 a-z / 0-9 / space / tab / enter / f1-f12
ui:
  opacity: 0.85         # HUD 背景不透明度
  width: 240            # 已废弃，仅为跨平台兼容保留
  height: 70            # 已废弃
```

几点说明：

- **`streaming` 必须和服务端模式一致**。服务端跑流式（默认）时这里填 `true`；服务端加了 `--no-streaming` 就得改成 `false`，否则连不上。
- **`scheme` 的派生是强制的**。代码里禁止硬编码 `http://` / `ws://`，一律经 `ServerConfig.httpScheme` / `wsScheme` 派生，见 [`PROTOCOL.md`](../PROTOCOL.md) §5.2。
- **客户端不配置 `device`**。跑在 CPU 还是 CUDA 由服务端决定。
- 配置读取全部走 `decodeIfPresent` + 默认值，缺字段或字段写错不会导致崩溃，只会回落到默认值。

---

## 架构

```
Sources/VoiceTyperApp/
├── App/
│   ├── VoiceTyperAppMain.swift      入口
│   ├── AppDelegate.swift            NSApplicationDelegate
│   └── AppCoordinator.swift         中央调度：装配、状态回调、权限/服务就绪判定、生命周期
├── Core/
│   ├── AppConfig.swift              配置模型（Codable + snake_case 映射）
│   ├── AppState.swift               状态枚举 + 菜单文案 + SF Symbol
│   ├── ConfigStore.swift            YAML 读写（Yams）
│   ├── PermissionCenter.swift       三项权限的查询 / 请求 / 跳转系统设置
│   └── VoiceTyperController.swift   核心状态机 + 流式/非流式双路径
├── Services/
│   ├── HotkeyService.swift          CGEventTap（含 Fn 与 Esc 取消）
│   ├── AudioCaptureService.swift    AVAudioEngine + AVAudioConverter，600ms 分帧
│   ├── StreamingASRClient.swift     WebSocket（URLSessionWebSocketTask）
│   ├── ASRClient.swift              HTTP POST /recognize
│   ├── ServerHealthProbe.swift      统一的 /health 探测入口
│   └── TextInsertionService.swift   AX 直写 + 剪贴板兜底
├── UI/
│   ├── StatusBarController.swift    菜单栏图标与菜单
│   ├── StatusMenuHeaderView.swift   菜单头部自定义视图
│   ├── RecordingHUDController.swift 录音 HUD（胶囊浮窗 + 流式预览）
│   ├── SetupWindowController.swift  设置窗口（NSTabViewController，toolbar 样式）
│   └── Settings/                    四页 SwiftUI 视图 + SettingsViewModel + 热键录制控件
└── Support/
    ├── Constants.swift              版本、bundle id、采样率、配置路径、系统设置 URL
    ├── LaunchAtLogin.swift          开机自启
    └── Logger.swift                 os.Logger，按 category 分类
```

### 状态机

```
booting ──▶ setupRequired ──(权限齐全)──▶ connecting ──(/health ready)──▶ idle
                  ▲                            │                          │
                  └────(权限被撤销)─────────────┘                    按住热键│
                                                                           ▼
                                                    idle ◀── inserting ◀── recording
                                                                  ▲          │ 松手
                                                                  └ recognizing
```

`paused`（菜单主动暂停）和 `error(String)` 可以从多数状态进入。所有状态迁移都收敛在 `VoiceTyperController` 和 `AppCoordinator` 两处，服务（热键 / 录音 / ASR / 插入）之间彼此不感知。

`connecting` 状态下 `AppCoordinator` 会按 2 → 3 → 5 → 8 → 10 秒的退避轮询 `/health`，直到 `ready: true`。这是为了兼顾服务端首次启动要下载模型的场景——用户不需要手动重试。

---

## 关键实现

### 热键：为什么是 CGEventTap

`CGEventTap` 是 Fn 键的唯一可行路径。Carbon 的 `RegisterEventHotKey` 只认「修饰键 + 主键」的组合，而 Fn 在系统里既不是普通按键也不是标准修饰键——它体现为 `CGEventFlags` 上的 `.maskSecondaryFn` 位，只能通过监听 `flagsChanged` 事件拿到。

实现要点：

- tap 类型为 `.cgSessionEventTap`，**listenOnly**（不吞事件）。因此 Esc 取消录音时，Esc 仍会照常传给前台应用。
- 监听 `keyDown` / `keyUp` / `flagsChanged` 三类事件。
- 事件回调跑在独立 worker 线程的 run loop 上，所有 `onPress` / `onRelease` / `onCancel` 回调都 `DispatchQueue.main.async` 派回主线程。
- 回调上下文用 `Unmanaged.passRetained` 管理生命周期，内部对 service 持 weak 引用，避免悬空指针崩溃。
- **系统会在回调超时时禁用 tap**。收到 `tapDisabledByTimeout` / `tapDisabledByUserInput` 时立即重新启用，否则全局热键会静默失效——这是这类实现最常见的坑。
- 组合键模式做**精确**修饰键匹配：期望的四个修饰键状态必须与事件 flags 完全一致，`Ctrl+F2` 不会被 `Ctrl+Shift+F2` 触发。

### 音频采集

`AVAudioEngine` 装 tap 拿输入节点的原始格式，再用 `AVAudioConverter` 转成 16kHz / float32 / mono，按 **9600 samples（600ms）**分帧回调。流式路径下每帧直接送进 WebSocket；非流式路径下累积到松手再一次性 POST。

监听了 `AVAudioEngineConfigurationChange`——换耳机、切音频设备时重建链路，不会静默变哑。

### 文本插入：两级策略

**第一级：Accessibility 直写**（`insertUsingAccessibility`）

1. 从 system-wide 元素取 `kAXFocusedUIElementAttribute` 拿到当前焦点控件；
2. 确认 `kAXValueAttribute` 可写；
3. 读出当前完整文本和 `kAXSelectedTextRange`；
4. 在选区位置替换出新文本并整体写回；
5. 把光标移到插入内容之后。

好处是**完全不碰剪贴板**。任一步失败就落到第二级。

**第二级：剪贴板 + 模拟 Cmd+V**（`insertUsingPasteboard`）

1. 快照当前剪贴板的**全部 item 和全部数据类型**（不只是纯文本）；
2. 写入识别文本，记下 `changeCount`；
3. 用 `CGEvent` 模拟 Cmd+V（`.cghidEventTap`）；
4. 500ms 后还原剪贴板——但**只在 `changeCount` 和内容都没被别人改过时**才还原，避免覆盖用户在这期间的复制操作。

插入彻底失败时还有 `copyToClipboard` 兜底，至少让长听写的内容留在剪贴板里，不会凭空消失。

### 流式双通道

录音时客户端每 600ms 发一帧音频，服务端回 `partial` 帧。**`partial.text` 是全量文本，客户端直接替换而不是拼接**：

```swift
self.accumulatedPreview = text   // 直接替换
```

因为服务端的预览是对已累积音频整段重跑，后一次结果会**修正**前一次的文字（`识别功能和并` → `识别功能合并`），增量语义表达不了这种回溯修改。详见 [`PROTOCOL.md`](../PROTOCOL.md) §4.3。

上屏的文本永远来自 `final` 帧，预览只影响 HUD 显示。

---

## 构建

### Xcode

```bash
cd client-server/client_macos_swift
open VoiceTyperClient.xcodeproj
```

依赖只有一个：[Yams](https://github.com/jpsim/Yams)（YAML 解析），走 SwiftPM。

### 命令行

```bash
cd client-server/client_macos_swift
./build_xcode.sh
```

脚本流程：解析 SwiftPM 依赖 → 构建 Universal Binary（arm64 + x86_64，Release，关掉代码签名）→ 从 Info.plist 读版本号 → 为三种架构分别打包。

非 universal 的变体用 `lipo -thin` 从 universal 产物里抽取单架构。`lipo` 会破坏 linker 签名，而 Apple Silicon 强制要求有效签名，所以每个变体都会 `codesign --force --deep -s -` 重做一次 ad-hoc 签名。

产物（`dist/`，共 6 个文件）：

```
VoiceTyperClient-<版本>-macOS-arm64.zip / .dmg
VoiceTyperClient-<版本>-macOS-x86_64.zip / .dmg
VoiceTyperClient-<版本>-macOS-universal.zip / .dmg
```

每个 DMG 内含 `VoiceTyperClient.app`、指向 `/Applications` 的软链，以及一份 `INSTALL.txt`（源文件在 `packaging/INSTALL.txt`）。

### 改版本号

版本号的唯一来源是 Xcode 工程的 `MARKETING_VERSION`（三个 configuration 都要改）。`Info.plist` 的 `CFBundleShortVersionString` 引用它，`AppConstants.version` 再从 bundle 读回来。打包脚本也从构建产物的 Info.plist 里取，所以只要改工程配置一处即可。

---

## 日志与排障

日志走统一日志系统（`os.Logger`），subsystem 为 `com.voicetyper.client`，分 `app` / `permissions` / `hotkey` / `audio` / `network` / `input` 六个 category。

```bash
# 实时看
log stream --predicate 'subsystem == "com.voicetyper.client"' --level debug

# 只看热键
log stream --predicate 'subsystem == "com.voicetyper.client" AND category == "hotkey"'

# 回看最近 10 分钟
log show --predicate 'subsystem == "com.voicetyper.client"' --last 10m
```

也可以在「控制台.app」里按 subsystem 过滤。

### 热键完全没反应

1. 检查「输入监控」权限（设置窗口的权限页会显示）。
2. 换过 app 版本后重新授权一次：系统设置里移除再添加，然后完全退出重启应用。
3. 看 `hotkey` category 的日志有没有 tap 被禁用又重启的记录。
4. 组合键模式下确认没有多按修饰键——匹配是精确的。

### 菜单栏一直显示「连接服务中…」

服务端 `/health` 没返回 `ready: true`。可能是服务端还在下模型（首次启动），也可能地址填错了。在设置窗口的「连接」页点「测试连接」看具体结果，或直接 `curl http://127.0.0.1:6008/health`。

### 识别成功但文字没插进去

先看是不是「辅助功能」权限的问题。目标应用同时拒绝 AX 直写和模拟粘贴时（少数沙箱严格的应用、部分终端模拟器、密码框），文本会留在剪贴板里，手动 `Cmd+V` 即可。

### HUD 显示的文字和最终上屏的不一致

正常情况下不会——默认配置下预览和终稿出自同一个模型。如果服务端切到了 `--offline-model paraformer-zh` 的双模型模式，预览由流式 paraformer 产出、终稿由离线 paraformer + ct-punc 产出，措辞和标点确实会有差异。

### 录音一开始就断

看 `audio` category 日志。常见原因是麦克风被其他应用独占，或者刚切换了音频设备。

---

## 相关链接

- [VoiceTyper 主项目](../../README.md)
- [一体化 macOS App（推荐）](../../macos/README.md)
- [服务端文档](../server/README.md)
- [客户端 ↔ 服务端协议](../PROTOCOL.md)
- [Windows 原生客户端](../client_windows_native/README.md)
- [架构笔记（历史稿）](docs/architecture.md) · [流式设计（历史稿）](docs/streaming-design.md)
