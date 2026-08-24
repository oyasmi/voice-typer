# VoiceTyper — 一体化 Windows 应用

[← 返回主项目](../README.md) · [设计方案](DESIGN.md) · [分体式客户端](../client-server/client_windows_native/README.md)

单进程 Windows 桌面应用：把 [`client-server/server/`](../client-server/server/README.md) 的 SenseVoice 识别链路用 C# 重写并
内联进客户端，安装即用，**不需要**单独部署 Python 服务端。当前版本 **3.1.0**，应用名 **VoiceTyper**。

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
- 首次启动引导下载一次模型（约 240MB），此后完全离线
- 按住热键（默认 `Ctrl+F2`）录音，松开自动识别并插入文本
- 流式实时预览：录音时 HUD 浮窗持续显示识别文本，且会自我修正
- 可选的 LLM 智能纠错，配置项直接在设置面板里（Base URL / API Key / 模型 / 温度 / 超时）
- 识别语言可指定为自动 / 中文 / 英文 / 粤语 / 日语 / 韩语
- 空闲一段时间后自动释放识别引擎内存，下次按热键与录音并行自动重新加载
- 开机自启、HUD 不透明度可调
- **同时支持 x64 与 arm64**（含 Snapdragon X 系列 Windows 笔记本）
- 支持 **Windows 10 与 Windows 11**

**不支持 / 已知限制**

- 只能按住说话，没有「按一次开始、再按一次结束」的切换模式；录音中按 `Esc` 可取消本次听写
- 热键主键限于字母、数字、`space`/`tab`/`enter`/`esc`、`F1`–`F12`、方向键等命名键
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
| 磁盘 | 约 300MB（App 本体 + 首次下载的模型） |
| 运行时 | 无需单独安装 .NET——安装包为自包含（self-contained）发布 |
| 网络 | 仅首次启动下载模型时需要；此后完全离线 |
| .NET SDK | 仅自行构建时需要（10.0+） |

---

## 安装

### 从 Release 安装

1. 从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载对应架构的安装包：
   `VoiceTyper-<版本>-win-x64-setup.exe`（多数电脑）或 `VoiceTyper-<版本>-win-arm64-setup.exe`
   （Snapdragon X 等 ARM 笔记本）。
2. 双击运行。安装到当前用户目录（`%LOCALAPPDATA%\Programs\VoiceTyper`），**不会**弹出 UAC 提权
   对话框。
3. 若出现"Windows 已保护你的电脑"提示：点「更多信息」→「仍要运行」。这是因为安装包未做
   代码签名（EV 证书成本较高，本项目暂不购买），不影响功能。
4. 安装完成后从开始菜单打开 VoiceTyper，或勾选"启动 VoiceTyper"直接运行。

也提供免安装的便携版 `VoiceTyper-<版本>-win-x64-portable.zip`：解压后直接运行
`VoiceTyper.exe`，配置与模型仍然落在用户目录，行为与安装版一致。

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
- 否则设置窗口的「识别」页会显示模型卡片，点「开始下载模型」即可：显示进度条、已下载/总量，
  支持取消。下载的四个文件（`config.yaml`、`am.mvn`、`tokens.json`、`model_quant.onnx`）来自
  ModelScope，逐个校验 sha256，支持断点续传（中途断网重新点击即可从中断处继续）。

模型落在 `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\`——刻意放在**非漫游**的
`LocalAppData` 而不是 `AppData\Roaming`：域环境下 Roaming profile 会跟随登录漫游，塞进 240MB
模型会让域用户登录变慢。

---

## 使用

1. 启动后系统托盘出现 VoiceTyper 图标。
2. **按住热键**（默认 `Ctrl+F2`）开始录音，HUD 浮窗出现在前台窗口所在的屏幕上。
3. 说话。HUD 会实时显示识别文本，并随着你继续说而自我修正；录音中按 `Esc` 可取消本次听写。
4. **松开热键**，本地引擎做一次整段复识别，最终文本插入当前光标位置。
5. 单段录音上限 **120 秒**：达到上限会自动结束当前听写并正常上屏，不会静默丢弃后续内容。

录音不足 **0.3 秒**视为误触，直接丢弃。插入前会校验前台窗口是否与录音开始时一致，若用户
在此期间切换了窗口，识别结果只会写入剪贴板，不会插入到意料之外的窗口。

托盘图标状态：

| 状态 | 含义 |
| --- | --- |
| 就绪（麦克风图标，无色点） | 等待热键 |
| 红色点 | 正在录音 |
| 黄色点 | 已松手，等本地引擎返回 / 正在插入文本 |
| 橙色点 | 需要下载或正在下载语音模型 |
| 灰色点 | 启动中 / 模型加载中 / 已暂停 |
| 深红色点 | 出错 |

右键托盘图标可打开菜单：设置、**暂停/恢复听写**（暂停后热键不再响应，直至从菜单恢复）、
打开配置目录、开机自启开关、关于、退出。

---

## 设置

设置窗口分四个 Tab，全部 UI 化，不需要手工编辑 YAML：

| Tab | 内容 |
| --- | --- |
| **识别** | 模型状态卡片（下载/加载/就绪/失败 + 重新加载）、识别语言、智能纠错（开关 + Base URL + API Key + 模型 + 温度 + 最大 Token + 超时 + 测试纠错） |
| **热键** | 修饰键（Ctrl/Alt/Shift/Win）组合 + 主键，支持预览 |
| **权限** | 麦克风可用性检测、跳转 Windows 隐私设置、UIPI 限制说明 |
| **通用** | 开机自启、HUD 背景不透明度、空闲多久后卸载模型、预览窗口（进阶，0=自动按本机性能校准） |

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
  idle_unload_minutes: 10    # 0 = 常驻不卸载（与 macOS 默认值一致）
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
  key: "f2"
ui:
  opacity: 0.85
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
  ├── HotkeyService / AudioCaptureService / TextInsertionService（原样搬运）
  └── LocalAsrSession ── 接口与旧 StreamingASRClient（WebSocket）完全一致
         └── AsrService（AsrPump 专用串行线程）
                └── SenseVoiceEngine
                       ├── FbankFrontend（自写 512 点 radix-2 FFT，Kaldi 兼容 fbank）
                       ├── LfrCmvn
                       ├── InferenceSession（Microsoft.ML.OnnxRuntime，CPU EP）
                       └── CtcDecoder + TextPostprocessor
```

核心设计原则是**不发明新的状态机**：`VoiceTyperController` 与分体式客户端共享同一套状态机，
只是把网络客户端换成了接口相同的本地会话。识别管线（fbank → LFR/CMVN → CTC 解码）是
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

模型不随构建产物打包，首次启动时应用会引导下载。如需离线预置模型用于测试或跳过下载引导：

```bat
powershell -ExecutionPolicy Bypass -File scripts\fetch_model.ps1
```

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
| `LlmCorrectorTests` | 纠错客户端的失败兜底（网络错误/截断/格式错误都要原样返回原文）、`tags-only` 响应不丢文本、`TestAsync` 抛出真实错误且不含响应正文 | 否 |
| `LlmEndpointTests` | Base URL 结构化解析：scheme/host 白名单、明文 HTTP 限回环私网、`/chat/completions` 后缀去重 | 否 |
| `AudioChunkerTests` | 定长分帧、跨调用累积余量、`Drain` 尾音、空输入 | 否 |

xUnit 2.x 没有 macOS `XCTSkip` 那样干净的运行期动态跳过 API；缺夹具/缺模型的测试用提前 `return`
代替，效果上等价（不阻塞其余测试），但会显示为"通过"而非"已跳过"——属于已知的展示层面差异。

---

## 日志与排障

日志落地为文件：`%APPDATA%\VoiceTyper\logs\app.log`，超过 2MB 自动滚动（保留 3 份备份）。

```bat
:: 实时跟踪（PowerShell）
Get-Content "$env:APPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50
```

菜单「打开配置目录」可以快速定位到日志所在文件夹。

### 模型下载失败

检查网络能否访问 ModelScope；下载支持断点续传，重新点击「开始下载模型」即可从中断处继续。
若反复失败，可用 `scripts\fetch_model.ps1` 手动下载，或在设置页把「识别 → 模型目录」（需手动
编辑 `config.yaml` 的 `asr.model_dir`）指向已有的模型文件夹。

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

- `Esc` 只在**正在录音**（红色点）阶段生效；松开热键进入"识别中"后 Esc 不再取消该次听写。
- 托盘菜单「暂停听写」会整体停掉热键监听，需要再次点击「恢复听写」才会响应热键——这是有意
  行为，不是故障。

### 全局热键突然失效，且没有任何错误提示

`WH_KEYBOARD_LL` 低级键盘钩子的回调若长时间未返回，系统会静默把钩子摘除且不通知应用。
VoiceTyper 每 30 秒检查一次钩子存活性（比对系统级"最近一次用户输入时间"与钩子自身最近一次
被调用的时间），检测到摘钩会自动重新安装，日志 `hotkey` 分类下会有对应记录。此项为代码审查
通过、尚未在真机上验证过真实摘钩场景（见 `DESIGN.md`「已知风险/待办」）；若怀疑遇到此问题，
重启应用可立即恢复。

---

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [设计方案](DESIGN.md)
- [分体式客户端（多设备共享服务端场景）](../client-server/client_windows_native/README.md)
- [服务端](../client-server/server/README.md)（本 App 不使用，但识别管线移植自此）
- [macOS 一体化应用](../macos/README.md)（同一套架构思路的姊妹实现，Windows 侧从其 Swift 代码直译而来）
