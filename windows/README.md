# VoiceTyper — 一体化 Windows 应用

**简体中文** | [English](README.en.md)

[← 返回主项目](../README.md) · [设计方案](DESIGN.md) · [变更记录](CHANGELOG.md) · [审查与修复计划](REVIEW_AND_REPAIR_PLAN.md) · [分体式客户端](../client-server/client_windows_native/README.md)

单进程 Windows 桌面应用：把 [`client-server/server/`](../client-server/server/README.md) 的 SenseVoice 识别链路用 C# 重写并
内联进客户端，安装即用，**不需要**单独部署 Python 服务端。当前版本 **3.5.1**（功能与 macOS 3.5.1 对等，见[变更记录](CHANGELOG.md)），应用名 **VoiceTyper**。

**本文适合**：想在自己 Windows 电脑上直接用的用户，以及要自行编译或二次开发的人。深入的架构
决策、实测数据（⚠️ 部分待真机复测）、逐项取舍见 [`DESIGN.md`](DESIGN.md)。

---

## 目录

- [功能与限制](#功能与限制)
- [系统要求](#系统要求)
- [安装](#安装)
- [模型下载](#模型下载)
- [使用](#使用)
- [设置](#设置)
- [架构](#架构)
- [构建](#构建)
- [测试](#测试)
- [日志与排障](#日志与排障)

---

## 功能与限制

**支持**

- 单进程运行，识别引擎（SenseVoice-Small）直接跑在 App 内，不连接任何服务端
- **首启四步引导**（欢迎 → 麦克风 → 语音模型 → 试一试）：最后一步在引导窗口里真跑一次听写，
  把「热键 → 麦克风 → 识别 → 文本插入」整条链验一遍，哪一环出问题当场指出
- 首次启动下载一次模型（约 240MB，大文件四段并行、断点续传、失败自动退避重试），此后完全离线
- 按住热键（默认 `Ctrl+F2`）录音，松开自动识别并插入文本；也可改为「按一次开始、再按一次结束」的
  切换模式；还能单独用**右 Ctrl** 或**右 Alt** 键作为热键（干净单击才触发，按住它再按别的键或点鼠标时仍是普通快捷键）
- 未就绪（模型下载中 / 加载中 / 加载失败）时按热键不再毫无反应：HUD 会说明缺什么
- 流式实时预览：录音时 HUD 浮窗持续显示识别文本（最多两行，只保留最新的尾部），且会自我修正；
  HUD 带实时波形与输入设备名，录音 1.5 秒仍没采到声音会提示检查麦克风，识别为空会明确告知
- 浮窗位置可选：底部居中 / 右下角 / 跟随光标 / 不显示（出错时仍会提示）；按当前屏幕 DPI 缩放
- 蓝牙耳机友好：默认输入设备是蓝牙耳机、且播放也走蓝牙时，自动改用内置麦克风，避免耳机切到电话音质
- 录音中或识别中按 `Esc` 都可以取消本次听写
- 可选的 LLM 智能纠错，配置项直接在设置面板里（Base URL / API Key / 模型 / 温度 / 超时）
- 识别语言可指定为自动 / 中文 / 英文 / 粤语 / 日语 / 韩语
- 界面语言可选中文（默认）或英文；托盘菜单、设置窗口与 HUD 文案对等，切换后重启生效
- 空闲一段时间后自动释放识别引擎内存，下次按热键与录音并行自动重新加载
- 开机自启、HUD 不透明度可调；托盘菜单「检查更新...」手动查询 GitHub 最新版本（不做后台联网）
- 每次听写在日志里留一行只含数字与枚举的耗时摘要（不含任何识别文本），便于排查「为什么慢」
- **同时支持 x64 与 arm64**（含 Snapdragon X 系列 Windows 笔记本）
- 支持 **Windows 10 与 Windows 11**

**不支持 / 已知限制**

- 热键主键限于字母、数字、`space`/`tab`/`enter`/`esc`、`F1`–`F12`、方向键等命名键（设置页可直接
  「录制热键」）；单独修饰键热键支持**右 Ctrl** 与**右 Alt**——右 Alt 松开时激活菜单栏的副作用由应用
  在干净单击开始时注入一对哑键掩掉，AltGr 组合字符与右 Alt+Tab 不受影响；左 Alt（同样激活菜单栏、
  且误触面大）、Win（弹开始菜单）、Shift（中文输入法里切换中英文）仍不支持，低级键盘钩子也无法
  吞掉修饰键事件
- 官方 Release 未做代码签名，首次运行可能被 SmartScreen 拦截，需要点「更多信息 → 仍要运行」；
  自行构建时可选签名，见[「构建 → 签名」](#签名可选)
- 只支持 SenseVoice-Small 模型，不支持更换为 paraformer；热词在两种形态里都已移除
  （如需 paraformer，用[分体式客户端](../client-server/client_windows_native/README.md) +
  [服务端](../client-server/server/README.md)）
- 不支持远程/共享服务端——识别永远在本机跑
- **UIPI 限制**：以管理员身份运行的窗口（记事本、终端等）不会响应文本插入，这是 Windows 安全
  机制的限制，不是识别故障。识别结果仍会写入剪贴板，可手动 `Ctrl+V`
- 不做 DirectML / NPU 硬件加速（原因见 [`DESIGN.md`](DESIGN.md) §4.2），只用 CPU 推理

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10（1809+）或 Windows 11 |
| 架构 | x64 或 arm64 |
| 磁盘 | 约 300MB（App 本体 + 首次下载的模型），另需已安装的 .NET 桌面运行时 |
| 运行时 | .NET 10 桌面运行时（Desktop Runtime，架构与应用一致）；发布包不包含 .NET |
| 网络 | 仅首次启动下载模型时需要；此后完全离线 |
| .NET SDK | 仅自行构建时需要（10.0+） |

---

## 安装

### 从 Release 安装

请先确认电脑已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
（选择 **Desktop Runtime** 与对应的 x64 / arm64 架构）。普通 .NET Runtime、ASP.NET Core Runtime、
.NET 8/9 或其他架构的运行时不能代替；.NET 10 SDK 已包含桌面运行时，无需重复安装。
安装程序会检查官方安装器登记的运行时；缺少时提示安装地址并停止，不捆绑或自动下载 .NET。

1. 从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载对应架构的安装包：
   `VoiceTyper-<版本>-win-x64-setup.exe`（多数电脑）或 `VoiceTyper-<版本>-win-arm64-setup.exe`
   （Snapdragon X 等 ARM 笔记本）。
2. 双击运行。安装到当前用户目录（`%LOCALAPPDATA%\Programs\VoiceTyper`），**不会**弹出 UAC 提权
   对话框。
3. 若出现"Windows 已保护你的电脑"提示：点「更多信息」→「仍要运行」。这是因为安装包未做
   代码签名（EV 证书成本较高，本项目暂不购买），不影响功能。
4. 安装完成后从开始菜单打开 VoiceTyper，或勾选"启动 VoiceTyper"直接运行。

也提供免安装的便携版 `VoiceTyper-<版本>-win-x64-portable.zip`：安装上述运行时后，解压并运行
`VoiceTyper.exe`，配置与模型仍然落在用户目录，行为与安装版一致。
缺少运行时时，便携版由 .NET 启动器显示安装提示。

### 卸载

「设置」→「应用」→ 找到 VoiceTyper → 卸载；或开始菜单「VoiceTyper」分组下的「卸载 VoiceTyper」。
卸载会删除安装目录，但**不会**删除配置与模型（`%APPDATA%\VoiceTyper\`、
`%LOCALAPPDATA%\VoiceTyper\`），需要手动删除。

### 和分体式客户端共存吗？

配置目录（`%APPDATA%\VoiceTyper\` vs `%APPDATA%\voice_typer\`）是隔离的，技术上可以同时安装。
但两者默认都注册 `Ctrl+F2`，**同时运行会抢热键**，建议只保留一个。若之前装过
[`client-server/client_windows_native/` 的 VoiceTyperClient](../client-server/client_windows_native/README.md)，迁移过来时
热键与 HUD 不透明度会在首次启动时自动继承。

---

## 模型下载

首次启动会检测本地是否已有 SenseVoice-Small 模型：

- 若这台机器之前跑过 [`client-server/server/`](../client-server/server/README.md)（`%USERPROFILE%\.cache\modelscope\` 下已有
  模型缓存），App 会自动复用，**零下载**。
- 否则设置窗口的「语音模型」页会显示模型卡片，点「开始下载模型」即可：显示进度条、已下载/总量，
  支持取消。下载的四个文件（`config.yaml`、`am.mvn`、`tokens.json`、`model_quant.onnx`）来自
  ModelScope，逐个校验 sha256，支持断点续传（中途断网重新点击即可从中断处继续）。

下载失败会自动切换 Hugging Face / HF Mirror 备用地址。超时、断流、HTTP 429/5xx 会重试；
HTTP 403、TLS 握手或证书信任失败不会对同一来源无限重试。模型页汇总各来源的失败原因，
「查看下载详情」提供内部异常与错误码，可选中文字复制；签名 URL 与 URL 内凭据不会进入诊断日志。
TLS 使用系统协议与证书信任，代理沿用 .NET 默认代理；不会关闭证书校验。

模型落在 `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\`——刻意放在**非漫游**的
`LocalAppData` 而不是 `AppData\Roaming`：域环境下 Roaming profile 会跟随登录漫游，塞进 240MB
模型会让域用户登录变慢。

---

## 使用

1. 启动后系统托盘出现 VoiceTyper 图标（Windows 11 默认把新图标收在任务栏右下角的「^」里，
   可以把它拖到外面常驻）。首次启动会先弹出四步引导。
2. **按住热键**（默认 `Ctrl+F2`）开始录音，HUD 浮窗出现在前台窗口所在的屏幕上。浮窗先显示灰色的
   「麦克风启动中…」，麦克风出声后变成红色「录音中」，**变红后再开口**；变红之前松开会提示本次没有录到声音。
   切换模式下改为按一次开始、再按一次结束。
3. 说话。HUD 会实时显示识别文本，并随着你继续说而自我修正；录音中或识别中按 `Esc` 可取消本次听写
   （识别中取消不会中断已经在跑的那次推理，但结果会被丢弃、不会插入）。
4. **松开热键**，本地引擎做一次整段复识别；启用智能纠错时 HUD 会显示「纠错中…」。最终文本插入当前光标位置。
5. 单段录音上限 **120 秒**：达到上限会自动结束当前听写并正常上屏，不会静默丢弃后续内容。

录音不足 **0.3 秒**视为误触，直接丢弃。上一段听写还在识别时再按热键，HUD 会提示「上一段听写尚未完成」，
不会叠起新的会话；若正在等智能纠错，再按一次热键会放弃等待、直接插入识别原文。热键含 Alt / Shift / Win 且识别完成时
仍未松开，会最多等约 0.4 秒再粘贴，仍未松开才改为只复制到剪贴板。插入前会校验前台窗口是否与录音开始时一致，若用户
在此期间切换了窗口，识别结果只会写入剪贴板，不会插入到意料之外的窗口。

托盘图标状态：

| 状态 | 含义 |
| --- | --- |
| 就绪（应用图标，无色点） | 等待热键 |
| 红色点 | 正在录音 |
| 黄色点 | 已松手，等本地引擎返回 / 正在插入文本 |
| 橙色点 | 需要下载或正在下载语音模型 |
| 灰色点 | 启动中 / 模型加载中 / 已暂停 |
| 深红色点 | 出错 |

右键托盘图标可打开菜单：设置、**暂停/恢复听写**（暂停后热键不再响应，直至从菜单恢复）、
开机自启开关、使用引导（重新打开首启引导）、检查更新、关于、退出。

---

## 设置

设置窗口使用五项侧栏导航，不需要手工编辑 YAML。跨页保留草稿，底部统一「保存并应用」或「撤销更改」。
关闭窗口保留草稿，下次打开可继续编辑；浮窗透明度的临时预览在关闭或撤销时恢复。

| 页面 | 内容 |
| --- | --- |
| **听写** | 热键录制、右 Ctrl / 右 Alt、触发方式、麦克风、识别语言；手动编辑快捷键默认折叠 |
| **语音模型** | 模型状态、下载/取消/重试/重新加载、下载诊断、预加载与空闲卸载；预览窗口参数默认折叠 |
| **智能纠错** | 开关、服务地址、API Key、模型名称、测试；温度/最大 Token/超时默认折叠，关闭功能时隐藏配置 |
| **外观与通用** | 浮窗位置、不透明度、开机自启、界面语言 |
| **诊断与帮助** | 麦克风检测、系统隐私设置、UIPI 说明、日志与配置目录入口 |

### 界面语言

默认中文。在「外观与通用」页切换为英文并保存后设置会立即落盘，但托盘菜单、设置窗口与 HUD 都是**按启动时的
语言构造**出来的，因此需要**重启 VoiceTyper** 才会全部改用新语言。界面语言与「听写」页的识别语言相互独立。

配置文件：

```
%APPDATA%\VoiceTyper\config.yaml
```

完整字段：

```yaml
asr:
  language: "auto"          # auto / zh / en / yue / ja / ko
  threads: 0                # 0 = 自动（min(4, 核数)）
  model_dir: ""              # 留空 = 自动定位（下载目录 / ModelScope 缓存）
  preview_window: 0          # 秒；0 = 首次加载后自动按本机性能校准
  idle_unload_minutes: 0     # 0 = 常驻不卸载（默认值，与 macOS 一致）
  preload_on_launch: true    # 启动时就把模型载入内存；false = 首次按热键才加载（与录音并行）
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key 不在这里，见下
hotkey:
  modifiers: ["ctrl"]
  key: "f2"                  # 也可以是 "right_ctrl" / "right_alt"（单独的右 Ctrl / 右 Alt 键，此时 modifiers 留空）
  mode: "hold"               # hold = 按住说话；toggle = 按一次开始、再按一次结束
audio:
  input_device: "auto"       # auto = 蓝牙耳机通话模式下改用内置麦克风；system = 严格跟随系统默认；或音频端点 ID
ui:
  opacity: 0.85
  hud_position: "bottom_center"  # bottom_center / bottom_right / near_cursor / hidden
  interface_language: "zh"   # zh / en；界面语言，重启后全面生效
```

LLM API Key 出于安全考虑不落配置文件，用 Windows DPAPI（`ProtectedData`，当前用户范围）加密后存
`%APPDATA%\VoiceTyper\llm_api_key.dat`。换用户或换机器都需要重新在设置页填写；若该文件损坏或
无法解密，设置页会明确提示"无法读取已保存的 API Key"，而不是让智能纠错静默地一直 401。

手改 `config.yaml` 时越界或非法的值（例如 `timeout: -1`、`hotkey.key` 不带任何修饰键）会在
下次加载/保存时被自动夹逼回合法范围并记一条 warning 日志，不会被静默接受、也不会拒绝整份配置。

---

## 架构

```
VoiceTyperController（状态机，Idle→Recording→Recognizing→Inserting，与分体式客户端同源）
  ├── HotkeyService / AudioCaptureService / TextInsertionService（经 IHotkeyListening / IAudioCapturing /
  │   ITextInserting 接口注入，控制器状态机因此可脱离真实钩子与麦克风单测）
  └── LocalAsrSession ── 接口与旧 StreamingASRClient（WebSocket）完全一致
         └── AsrService（AsrPump 专用串行线程）
                └── SenseVoiceEngine
                       ├── FbankFrontend（自写 512 点 radix-2 FFT，Kaldi 兼容 fbank）
                       ├── LfrCmvn
                       ├── InferenceSession（Microsoft.ML.OnnxRuntime，CPU EP）
                       └── CtcDecoder + TextPostprocessor
```

核心设计原则是**不发明新的状态机**：`VoiceTyperController` 沿用同一条主流程，一次听写对应一个
`Utterance`，所有终止路径（识别完成 / 出错 / Esc 取消 / 短录音丢弃 / 组合手势作废 / 停止）都只走一个幂等的
`Finish()`。识别管线（fbank → LFR/CMVN → CTC 解码）是
**从 [`macos/`](../macos/) 的 Swift 实现直译**而来（而不是从头对照 Python 移植）——两个平台的
fbank 实现共用同一份 Python 金标准夹具，Windows 侧的 `Tests/VoiceTyper.Tests/` 直接链接
`macos/Tests/VoiceTyperTests/Fixtures/` 下已入库的参考数据。

完整的设计决策、实测数据（⚠️ 标注为估算的部分需真机复测）、模块职责映射见 [`DESIGN.md`](DESIGN.md)。

---

## 构建

### 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- （可选，打包安装程序需要）[Inno Setup](https://jrsoftware.org/isdl.php)，确保 `ISCC.exe` 在 PATH 中

### 开发调试

```bat
cd windows
dotnet run
```

### 命令行构建全部产物

```bat
cd windows
build.bat
```

产出 `dist/`：

```
VoiceTyper-<版本>-win-x64-setup.exe        安装包（推荐，per-user，不弹 UAC）
VoiceTyper-<版本>-win-x64-portable.zip      免安装版
VoiceTyper-<版本>-win-arm64-setup.exe
VoiceTyper-<版本>-win-arm64-portable.zip
```

未安装 Inno Setup 时脚本会跳过安装包步骤，只产出便携版 zip。

两种架构均为依赖系统框架的目录式发布（`--self-contained false`），不开 ReadyToRun 或 IL 裁剪，
只引用 WASAPI 音频模块；调试符号与原生链接用 `.lib` 不随包分发。
构建与 CI 使用 `scripts/verify_publish.ps1` 检查必需文件，并拒绝包含 .NET 运行时、模型或测试程序集的产物。
实测体积与手工验收步骤见 [分发体积检查](PACKAGE_SIZE_AUDIT.md)。

模型不随构建产物打包，首次启动时应用会引导下载。如需离线预置模型用于测试或跳过下载引导：

```bat
powershell -ExecutionPolicy Bypass -File scripts\fetch_model.ps1
```

### 在 macOS / Linux 上开发（没有 Windows 机器时）

.NET SDK 支持针对 Windows 交叉编译，不需要 Windows 环境就能验证「能不能编译」和大部分纯逻辑：

```bash
cd windows
# 交叉编译（必须显式给 RID）
dotnet build VoiceTyper.csproj -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true
# 发布（依赖目标电脑的 .NET 10 Desktop Runtime，x64 / arm64 均可）
dotnet publish VoiceTyper.csproj -c Release -r win-arm64 --self-contained false -p:EnableWindowsTargeting=true -o /tmp/pub

# 或使用封装好的 Makefile 目标（产物在 windows/dist-cross/，只验证"能编译、产物结构对"，不能运行）：
make cross-check
make cross-publish

# 在非 Windows 主机上跑测试：不带 RID 构建，并把运行时配置里的 WindowsDesktop 框架依赖去掉
dotnet build Tests/VoiceTyper.Tests/VoiceTyper.Tests.csproj -c Debug -p:SelfContained=false -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=
#   编辑 Tests/VoiceTyper.Tests/bin/Debug/net10.0-windows/VoiceTyper.Tests.runtimeconfig.json，
#   删除 frameworks 里的 Microsoft.WindowsDesktop.App
dotnet test Tests/VoiceTyper.Tests/bin/Debug/net10.0-windows/VoiceTyper.Tests.dll
```

这样能跑：状态机、配置、下载器、LLM、引导流程、HUD 排版，以及（本机有 SenseVoice 模型缓存时）端到端识别。
**不能跑、也不能据此下结论**：低级键盘钩子、`SendInput`、剪贴板、WASAPI 采集、窗体绘制与 DPI——这些只能在真机验证，
清单见 [`CHANGELOG.md`](CHANGELOG.md#真机验证清单)。

### 改版本号

`VoiceTyper.csproj` 里的 `<Version>` / `<AssemblyVersion>` / `<FileVersion>`。

### 签名（可选）

不设置签名环境变量时 `build.bat` 行为完全不变（产物不签名）。要签名，先把证书导入本机
证书存储，再设置：

```bat
set VOICETYPER_SIGN_THUMBPRINT=<证书 SHA1 指纹>
set VOICETYPER_TIMESTAMP_URL=http://timestamp.digicert.com
build.bat
```

`build.bat` 会用 `signtool.exe` 依次对 `dist\<rid>\VoiceTyper.exe` 与 Inno Setup 产出的安装包
签名。不签名的可执行文件会触发 SmartScreen「未知发布者」警告，与 macOS 侧的 Gatekeeper 未公证
是同一类问题——`macos/build_xcode.sh` 的可选 Developer ID 签名 + 公证与此对称。

---

## 测试

```bat
cd windows
dotnet test
```

| 测试 | 内容 | 需要模型？ |
| --- | --- | --- |
| `FftTests` | 自写 FFT 与朴素 DFT 比对 | 否 |
| `FbankParityTests` | fbank / LFR+CMVN 特征逐点比对 `client-server/server/` 产出的金标准（阈值 1e-3，复用 `macos/` 已入库夹具），另含全零输入的对数下限回归 | LFR/CMVN 部分需要（缺失自动跳过） |
| `TextPostprocessorTests` | CTC 解码后文本清洗的各条规则 | 否 |
| `RecognitionBufferTests` | 滑窗预览调度逻辑（用假引擎，不依赖真实模型） | 否 |
| `ConfigStoreTests` | 配置模型与 YAML 序列化往返（不接触真实 `%APPDATA%`） | 否 |
| `AppConfigValidationTests` | 配置字段越界夹逼、非有限浮点重置、裸键热键回落默认值 | 否 |
| `LocalizationTests` | 中英双语覆盖率：源码里每条 `L10n.T(...)` / `L10n.F(...)` 都有英文翻译、占位符一致、查不到时回落中文、Bootstrap 能读出 `interface_language` | 否 |
| `LlmCorrectorTests` / `LlmThinkingTests` | 纠错客户端的失败兜底（网络错误/截断/格式错误都要原样返回原文）、`tags-only` 响应不丢文本、`TestAsync` 抛出真实错误且不含响应正文；默认关闭深度思考、服务拒绝该字段时去掉重发一次并按「地址+模型」缓存 | 否 |
| `LlmEndpointTests` | Base URL 结构化解析：scheme/host 白名单、明文 HTTP 限回环私网、`/chat/completions` 后缀去重 | 否 |
| `AudioChunkerTests` | 定长分帧、跨调用累积余量、`Drain` 尾音、空输入 | 否 |
| `VoiceTyperControllerTests` | 控制器状态机：按住 / 切换 / 单独修饰键三种触发方式、未就绪门禁（含「进行中的听写不被门禁吞掉松键」）、Esc 取消（录音中与识别中）、组合手势静默丢弃、连按拒绝、空识别、插入失败与提权提示、录音启动失败、静音探测、收尾幂等 | 否 |
| `ModifierOnlyHotkeyTests` / `HotkeyStateMachineTests` / `HotkeyRecordingTests` | 热键状态机（含右 Ctrl 干净单击识别、鼠标作废、Esc 受理窗口）与设置页录制热键的判定 | 否 |
| `AudioInputDeviceTests` | 输入设备选择策略与蓝牙 / USB / 内置端点分类 | 否 |
| `ModelDownloaderTests` | 单连接 / 四段并行 / 服务端忽略 Range 时回落 / 分段续传 / 校验失败 / 取消（内存里的 Range 服务，不联网） | 否 |
| `OnboardingModelTests` / `UpdateCheckerTests` / `DictationMetricsTests` / `HudTextLayoutTests` / `ConfigParityTests` | 引导流程与试用状态、版本号解析、耗时摘要（禁止携带用户文本）、HUD 预览排版、新增配置项的往返与校验 | 否 |
| `EndToEndRecognitionTests` | 完整识别链路（fbank → LFR/CMVN → 真实 ONNX → CTC → 后处理）对同一段真实语音的识别结果与 Python 参考的编辑距离 ≤ 2，以及分块预览流程收敛到同一最终文本 | 是（缺失自动跳过） |

xUnit 2.x 没有内置的运行期动态跳过 API，缺夹具 / 缺模型的测试用 `Xunit.SkippableFact`（`Skip.If`）
明确显示为「已跳过」并写清原因，不会变成假通过。

---

## 日志与排障

日志落地为文件：`%APPDATA%\VoiceTyper\logs\app.log`，超过 2MB 自动滚动（保留 3 份备份）。

```bat
:: 实时跟踪（PowerShell）
Get-Content "$env:APPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50
```

设置窗口「诊断与帮助」页可以快速打开日志目录或配置目录。

每次听写收尾会输出一行 `[metrics]` 摘要（只含数字与枚举，不含识别文本、设备名或窗口标题），例如：

```
dictation session=3fa1 outcome=inserted mode=hold hotkey=combo input=builtin capture_start=420 first_buffer=436
key_lag=0 dispatch=1 start_queue=0 dev_resolve=6 dev_cached=1 activate=31 init=380 hud_shown=18 hud_ready=452 audio=3.4s
release_to_finalize=8 engine_wait=0 asr=310 llm=- llm_result=off llm_retry=- insert=24 release_to_done=352 previews=5 previews_skipped=1 preview_max=290 cold=0
```

（实际是一行，这里为排版折开；数字仅为示意。）`input=bluetooth*` 末尾的星号表示这次是「自动」策略从系统默认
输入切换而来。`key_lag` 到 `hud_ready` 拆解了「按下热键到可以开口」的耗时，字段含义见
[DESIGN.md §18.3](DESIGN.md#183-新增耗时字段)。

### 模型下载失败

到「语音模型 → 查看下载详情」检查各来源的失败原因。HTTP 403 表示该来源拒绝请求；TLS 错误应检查
系统时间、系统/环境代理与证书信任。下载支持断点续传，处理原因后点「重试下载」。
若反复失败，可用 `scripts\fetch_model.ps1` 手动下载，或编辑 `config.yaml` 的 `asr.model_dir`
指向已有的模型文件夹；模型目录没有图形化编辑入口。

### 热键完全没反应

- 确认没有其它程序占用同一组合键。
- 部分以管理员身份运行的窗口（任务管理器、部分安全软件）在前台时，非提权进程的低级键盘钩子
  可能被系统限制——尝试切到普通窗口测试。
- 查看日志 `logs\app.log` 里 `hotkey` 分类的记录，确认 `SetWindowsHookExW` 是否安装成功。

### 识别成功但文字没插进去

- 检查目标窗口是否以管理员身份运行——这是 Windows UIPI 的已知限制（见上文"功能与限制"），
  文本已经在剪贴板里，手动 `Ctrl+V` 即可。
- 若 HUD/托盘提示"目标窗口已变化"：说明录音开始到识别完成之间切换了前台窗口，为避免误插入
  到意料之外的窗口（最坏情况是密码框），VoiceTyper 只会把结果写入剪贴板，不会自动插入。
- 其余情况参考[分体式客户端文档的对应章节](../client-server/client_windows_native/README.md)，文本插入实现
  代码原样搬运，行为一致。

### 录音中按 Esc 没反应 / 暂停后热键失灵

- `Esc` 在录音中与识别中都会取消本次听写（识别中取消不会中断已经在跑的那次推理，只丢弃它的结果）。
  没有听写在进行时，`Esc` 照常传给前台应用。
- 托盘菜单「暂停听写」会整体停掉热键监听，需要再次点击「恢复听写」才会响应热键——这是有意
  行为，不是故障。

### 全局热键突然失效，且没有任何错误提示

`WH_KEYBOARD_LL` 低级键盘钩子的回调若长时间未返回，系统可能静默把钩子摘除且不通知应用。
VoiceTyper 的钩子回调已做到只做按键判定与异步投递、立即返回，正常情况下不会触发摘除；
另有一个独立于钩子实例的健康定时器，检测到句柄丢失或长时间零回调时按指数退避重装，
重装持续失败会在托盘/设置里显示"热键监听已失效"。日志 `hotkey` 分类下有对应记录。
此项为代码审查通过、尚未在真机上验证过真实摘钩场景；若怀疑遇到此问题，重启应用可立即恢复。

---

## 相关链接

- [English version of this document](README.en.md)
- [VoiceTyper 主项目](../README.md)
- [设计方案](DESIGN.md)
- [分体式客户端（多设备共享服务端场景）](../client-server/client_windows_native/README.md)
- [服务端](../client-server/server/README.md)（本 App 不使用，但识别管线移植自此）
- [macOS 一体化应用](../macos/README.md)（同一套架构思路的姊妹实现，Windows 侧从其 Swift 代码直译而来）
