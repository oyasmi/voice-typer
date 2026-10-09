# VoiceTyper Windows 一体化应用 · 设计方案

> **2026-09-30 更新**：用户已确认 Windows 版本初步可用；本次在 Windows 本机完成真实下载器的
> 四文件完整下载与固定 SHA256 校验（约 37 秒）。设置窗口改为五项侧栏导航、跨页草稿与统一保存，
> 详情见本文 §16。下方「仍未在真实 Windows 设备上运行」属于历史状态，不再作为当前事实。
> 性能、arm64 真机、实际系统 DPI 跨屏切换与长期稳定性仍需验证。

> **2026-09-06 审查补充**：[Windows 审查与修复计划](REVIEW_AND_REPAIR_PLAN.md)记录了当前实现的
> 构建、音频、输入注入和生命周期缺陷，并给出严重程度、修复难度、任务依赖与验收标准。
> 修复工作以该清单核对当前代码；下文历史性的“已对齐”“代码审查通过”不代表这些缺陷已经修复。
>
> **2026-09-06 修复进度**：R0–R4 全部工单已完成代码与静态验证（见
> [REPAIR_DELIVERY.md](REPAIR_DELIVERY.md)），**尚未在带 .NET SDK 的环境编译、未在 Windows 真机
> 验证**。合入后第一步是 `dotnet build` / `dotnet test` 跑绿并处理清单外的编译错误。

> **2026-09-29 状态更新（3.5.0）**：Windows 已对齐 macOS 3.5.0 的行为基线，并**首次在带 .NET SDK 的环境里被
> 编译、跑测试、发布**（macOS 主机上的交叉编译；280 项测试通过，含端到端识别；x64 / arm64 发布成功）。
> 详见 [CHANGELOG.md](CHANGELOG.md) 与本文 §15。**仍未在真实 Windows 设备上运行过**——凡涉及热键钩子、麦克风、
> 剪贴板、浮窗观感、DPI、性能的结论都只是「代码审查通过」，真机验证清单见
> [CHANGELOG.md](CHANGELOG.md#真机验证清单)。下文与此状态矛盾的旧表述（"从未编译""待跑 dotnet build"等）
> 以本段为准；[Windows 审查与修复计划](REVIEW_AND_REPAIR_PLAN.md)与其后续文档保留为历史记录。

> 目标产物：`windows/` 下一个**前后端一体**的 Windows 桌面应用，安装即用，不需要单独跑 Python 服务端。
> 它以 `client-server/client_windows_native/` 为蓝本，把 `macos/` 已经验证过的 SenseVoice 推理链路从 Swift 直译为 C#。
>
> 本文只覆盖 Windows。`macos/`、`client-server/client_linux/`、`client-server/server/` 保持现状不动。
>
> **本文档的证据分级**：标注「✅ 实测」的结论是在本次调研中真实验证过的（NuGet 包内容、
> ModelScope 端点、托管 API 表面、金标准夹具）；标注「⚠️ 估算」的是从 macOS/M4 实测数据外推的，
> **必须在 P0 阶段用真实 Windows 硬件复测**——本次调研环境是 macOS，无 .NET SDK、无 Windows 机器，
> 无法编译也无法跑分。凡是与性能、内存、产物体积相关的数字，都属于后者。
>
> **当前状态（2026-09，v3.2.1）**：本文 P1–P8 描述的工程已全部编码完成并合入仓库
> （测试文件以 §8 更新后的清单为准），与 macOS 之间逐条盘点的 35 项差异也已完成代码层面的拉齐。
> 但截至本文更新时：① 尚未在真实 Windows 设备上完成编译、安装与运行验证（见根 README「Windows」一节），
> `dotnet build` / `dotnet test` 也尚无在任何环境跑通的记录；② §4.5 的全部性能/内存数字仍是外推估算；
> ③ 需真机验证的具体清单见 §13.2。下文保留撰写时的调研口吻与 ✅/⚠️ 标注，作为决策依据的
> 历史记录；当前事实以上述状态为准。

---

## 1. 目标与非目标

### 1.1 目标

| # | 目标 | 验收标准 |
| --- | --- | --- |
| G1 | 单进程、零外部依赖 | 全新 Windows 上装完 → 打开 → **App 内引导下载一次模型** → 按热键即可听写。全程不接触命令行、不装 Python，此后完全离线 |
| G2 | 识别质量与现有链路一致 | 同一段音频，C# 特征提取（fbank/LFR/CMVN）与 `client-server/server/` 输出逐点误差 < 1e-3（复用 `macos/` 已入库的金标准夹具）；完整识别文本编辑距离 ≤ 2 |
| G3 | 延迟不劣化 | 松手到上屏 ≤ 现有本地 server 方案（去掉了 WS 往返 + Python 解释器开销） |
| G4 | 配置面收敛 | 用户可见配置项从 12 项降到 6 项左右，且没有一项与"服务端在哪"有关 |
| G5 | LLM 纠错开箱可配 | 设置面板里填 base_url / api_key / model 即可启用 |
| G6 | 架构覆盖不留缺口 | 同时出 **x64 与 arm64**（Snapdragon X 笔记本）——ORT 两个 RID 的原生库都齐备，没有 macOS 侧"放弃 Intel"那种取舍 |

### 1.2 非目标（本轮明确不做）

- 不支持 paraformer / ct-punc / 流式 paraformer——**只留 SenseVoice-Small**。
- 不做 API Key 鉴权（进程内直调，没有攻击面）。
- 不做 GPU / DirectML / NPU 加速（理由见 §4.2），不做远程服务端连接。
- 不改动 `client-server/server/`、`macos/`、`client-server/client_linux/` 的行为（仅同步文档表述）。
- 不做自动更新、不做代码签名/MSIX（沿用现有免签分发方式，SmartScreen 提示作为已知问题记录）。
- 不做 UI Automation 直插文本（沿用剪贴板 + SendInput，理由见 §4.6）。

### 1.3 命名与版本

| 项目 | 产品名 | 配置目录 | 版本 |
| --- | --- | --- | --- |
| `client-server/client_windows_native/`（现有，降级为次要） | `VoiceTyper` → **`VoiceTyperClient`** | `%APPDATA%\voice_typer\`（**不变**） | 3.0.0（**不变**） |
| `windows/`（新，主分发版本） | **`VoiceTyper`** | **`%APPDATA%\VoiceTyper\`** | **3.2.1**（3.0.0 首发；3.1.0 完成向 macOS 3.1.9 的行为对齐；3.2.1 补齐 macOS 4c33be2 本地听写恢复加固，见 §13、§14 与 D3） |

> 与 macOS 同构：老客户端改名让位，新一体化 App 接管 `VoiceTyper` 这个名字。
> Windows 上没有 Bundle ID / TCC 那套以标识符为键的权限体系，改名的代价比 macOS 更低——
> 只是可执行文件名、托盘提示、开始菜单项和配置目录变了，用户不需要重新授权任何东西。

---

## 2. 现状盘点

### 2.1 客户端侧（`client-server/client_windows_native/`，3,426 行 C#）

| 模块 | 行数 | 处理 |
| --- | ---: | --- |
| `Services/HotkeyService.cs` | 254 | **原样复制**，零改动（`WH_KEYBOARD_LL` 低级钩子） |
| `Services/AudioCaptureService.cs` | 250 | **原样复制**，零改动（WASAPI → 16k/mono/f32，600ms 分帧） |
| `Services/TextInsertionService.cs` | 120 | **原样复制**，零改动（剪贴板 + SendInput Ctrl+V） |
| `Support/{NativeMethods,UiDispatcher,AppLog}.cs` | 268 | 原样复制 |
| `Support/Constants.cs` | 40 | 改配置目录名、拆出模型目录 |
| `UI/RecordingHud.cs` | 236 | 原样复制，仅改文案 |
| `UI/TrayController.cs` | 236 | 删「重新连接服务」，加「开机自启」「关于」 |
| `App/{Program,TrayApplicationContext}.cs` | 107 | 近乎原样 |
| `Core/AppState.cs` | 35 | **新增** `ModelMissing` / `DownloadingModel` / `ModelLoading` 三态 |
| `Core/VoiceTyperController.cs` | 397 | **保留状态机，替换网络层**（详见 §5.3），删非流式路径 → 约 250 行 |
| `App/AppCoordinator.cs` | 238 | 删服务端探活/重连，改为模型就绪编排 |
| `Services/{StreamingASRClient,ASRClient}.cs` | 422 | **删除**，由本地 `LocalAsrSession` 顶替 |
| `Core/AppConfig.cs` + `ConfigStore.cs` | 273 | 重写 schema（`asr`/`llm`/`hotkey`/`ui`，无 `server` 段） |
| `UI/SetupForm.cs` | 550 | 「连接」Tab → 「识别」Tab；新增「通用」Tab |

**结论**：约 **1,500 行近乎原样搬运**、**1,100 行改造**、**420 行删除**。
与 macOS 的 "~85% 零改动" 是同一个量级，同一个策略。

### 2.2 推理侧（从 `macos/Sources/VoiceTyper/` 直译）

**这次不用再从 Python 移植了**——`macos/` 已经把 `client-server/server/recognizer.py` 的链路翻成 Swift 并用
金标准测试验证过。Windows 侧只需 **Swift → C# 的机械直译**，参考实现和期望输出都是现成的：

| macOS 源文件 | 行数 | C# 目标 | 直译难度 |
| --- | ---: | --- | --- |
| `ASR/FbankFrontend.swift` | 196 | `Asr/FbankFrontend.cs` | **中**——vDSP 换自写 FFT（§4.3） |
| `ASR/LFRCMVN.swift` | 87 | `Asr/LfrCmvn.cs` | 低 |
| `ASR/SenseVoiceEngine.swift` | 132 | `Asr/SenseVoiceEngine.cs` | 低——ORT 两侧 API 一一对应 |
| `ASR/CTCDecoder.swift` | 42 | `Asr/CtcDecoder.cs` | 低——`vDSP_maxvi` → `TensorPrimitives.IndexOfMax` |
| `ASR/TextPostprocessor.swift` | 49 | `Asr/TextPostprocessor.cs` | 低——`NSRegularExpression` → `Regex` |
| `ASR/RecognitionBuffer.swift` | 120 | `Asr/RecognitionBuffer.cs` | 低 |
| `ASR/LocalASRSession.swift` | 203 | `Asr/LocalAsrSession.cs` | **中**——`@MainActor` → `UiDispatcher` |
| `ASR/ASRService.swift` | 146 | `Asr/AsrService.cs` | **中**——串行队列模型（§4.4） |
| `ASR/ModelLocator.swift` | 130 | `Asr/ModelLocator.cs` | 低 |
| `ASR/ModelDownloader.swift` | 230 | `Asr/ModelDownloader.cs` | **中**——改用 `HttpClient` + Range（比 macOS 版更简单） |
| `LLM/LLMCorrector.swift` | 145 | `Llm/LlmCorrector.cs` | 低 |
| `Core/KeychainStore.swift` | 58 | `Core/SecretStore.cs` | 低——Keychain → DPAPI |

移植总量估算：**~1,300 行 C#**。

---

## 3. 总体架构

```
┌──────────────────────── VoiceTyper.exe（单进程，.NET 10 / WinForms）────────────────────┐
│                                                                                        │
│  ┌─── UI 线程（STA，WinForms 消息泵）──────────────────────────────┐                    │
│  │  AppCoordinator                                                 │                    │
│  │    ├ TrayController / RecordingHud / SetupForm                  │                    │
│  │    ├ MicPermissionProbe                                         │                    │
│  │    └ VoiceTyperController ← 状态机 Idle→Recording→Recognizing→Inserting              │
│  │          ├ HotkeyService       (WH_KEYBOARD_LL)                 │                    │
│  │          ├ AudioCaptureService (WASAPI, 16k/f32/mono)           │                    │
│  │          ├ TextInsertionService(剪贴板 + SendInput)              │                    │
│  │          └ LocalAsrSession ─────┐  ← 接口与旧 StreamingASRClient 一致                 │
│  └──────────────────────────────────┼─────────────────────────────┘                    │
│                                     │ OnPartial / OnFinal / OnWarning / OnError         │
│                                     │      （全部经 UiDispatcher.Post 回 UI 线程）        │
│  ┌─── asrPump（专用后台线程 + BlockingCollection，串行）────────────┐                    │
│  │  AsrService                                                     │                    │
│  │    └ SenseVoiceEngine                                           │                    │
│  │         ├ FbankFrontend    (自写 radix-2 FFT + TensorPrimitives) │                    │
│  │         ├ LfrCmvn                                               │                    │
│  │         ├ InferenceSession (Microsoft.ML.OnnxRuntime, CPU EP)   │                    │
│  │         └ CtcDecoder + TextPostprocessor                        │                    │
│  └─────────────────────────────────────────────────────────────────┘                    │
│                                     │ 最终文本                                          │
│  ┌─── 线程池 Task ────────────────────┴────────────────────────────┐                    │
│  │  LlmCorrector (HttpClient → OpenAI 兼容 /chat/completions)      │                    │
│  └─────────────────────────────────────────────────────────────────┘                    │
│                                                                                        │
│  %LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\                                     │
│      {model_quant.onnx, am.mvn, config.yaml, tokens.json}                               │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

**核心设计原则与 macOS 完全一致：不发明新的状态机。**
`VoiceTyperController` 保持原样，只是它手里的 `StreamingASRClient`（WebSocket）换成了
`LocalAsrSession`（本地引擎），两者**回调签名完全一致**。

---

## 4. 关键技术选型

### 4.1 运行时与 UI 栈 —— .NET 10 + WinForms

**.NET 版本：`net10.0-windows`（不是 net8.0）**

✅ 实测/查证：.NET 8 与 .NET 9 的支持均于 **2026-11-10 终止**（距今约 3 个月）；
.NET 10 是 LTS，支持到 2028-11。新建工程直接落在 net8.0 等于开局就欠债。
ORT 托管程序集提供 `lib/net8.0/` 与 `lib/netstandard2.0/` 资产（✅ 实测，见 §4.2），
在 `net10.0-windows` 下走 `net8.0` 资产，兼容无碍；NAudio 2.2.1 / YamlDotNet 16.x 同理。

> `client-server/client_windows_native/` 是否也升 .NET 10：本轮**不动**。它已进入维护期，
> 升级要重新回归全部 UI，收益不抵风险。在其 README 里记一笔 EOL 时间即可。

**UI 栈：继续 WinForms**

| 方案 | 结论 | 理由 |
| --- | --- | --- |
| **WinForms**（选中） | ✅ | `RecordingHud` / `TrayController` / `SetupForm` 共 1,022 行经过实战的代码可直接复用；`WS_EX_NOACTIVATE` + `ShowWithoutActivation` 的"不抢焦点悬浮窗"已经调通——这是这个 App 最难缠的一个 UI 需求 |
| WPF | ❌ | 悬浮 HUD 能做得更漂亮（真透明/亚克力/Per-Monitor DPI），但要重写全部 UI 约 1,000 行，功能零增量，还要重新踩一遍"不抢焦点"的坑 |
| WinUI 3 / Windows App SDK | ❌ | 额外的 SDK 运行时依赖；托盘图标与全局悬浮窗支持一直是弱项；打包模型（MSIX）与"下载 240MB 模型到用户目录"的诉求相互别扭 |
| C++/WinRT | ❌ | ORT 的 C++ API 更直接，但要重写全部 UI 与业务逻辑，且失去 `macos/` Swift 参考实现的一一对应关系（直译价值归零） |

WinForms 的两个已知短板，在本轮一并处理掉：
- **Per-Monitor DPI**：现有 `PositionToTopCenter()` 只看 `Screen.PrimaryScreen`。改为跟随
  **当前前台窗口所在屏幕**（`MonitorFromWindow(GetForegroundWindow(), ...)`），并在
  `app.manifest` 里声明 `PerMonitorV2`。听写目标窗口在哪块屏，HUD 就在哪块屏——这比现状更对。
- **HUD 圆角**：现有 `Region` 裁剪是硬边锯齿。改用 `DwmSetWindowAttribute` 的
  `DWMWA_WINDOW_CORNER_PREFERENCE`（Win11）+ Region 回退（Win10），成本 ~20 行。

### 4.2 ONNX Runtime 集成 —— `Microsoft.ML.OnnxRuntime` 1.24.2（CPU EP）

✅ **实测确认**（本次直接下载 nupkg 解包核对）：

| 事实 | 数值 |
| --- | --- |
| 包 `Microsoft.ML.OnnxRuntime` 1.24.2 含 `runtimes/win-x64/native/onnxruntime.dll` | 14,148,680 B |
| 含 `runtimes/win-arm64/native/onnxruntime.dll` | 14,161,952 B |
| 另含 `onnxruntime_providers_shared.dll` | ~22 KB |
| 托管程序集来自依赖包 `Microsoft.ML.OnnxRuntime.Managed` 1.24.2 | `lib/net8.0/…dll` 234,568 B |
| 托管 API 表面（strings 核验） | `AddSessionConfigEntry` / `GetSessionConfigEntry` / `HasSessionConfigEntry`、`IntraOpNumThreads` / `InterOpNumThreads`、`GraphOptimizationLevel`、`LogSeverityLevel`、`OrtValue.CreateTensorValueFromMemory`、`GetTensorDataAsSpan` 全部存在 |
| `session.disable_prepacking` 配置键 | 在 ORT 的 `onnxruntime_session_options_config_keys.h` 中定义，.NET 侧通过 `AddSessionConfigEntry("session.disable_prepacking","1")` 设置 |

**版本选 1.24.2 而不是最新的 1.29.0**：与 `macos/` 钉的 ORT 版本**完全一致**。
G2 要求两个平台的识别结果都对齐 Python 参考；ORT 版本一致能消掉一整类
"不同版本的算子实现/图优化导致 argmax 翻转"的变量。升级 ORT 应作为一次独立的、
带金标准复测的变更，而不是新项目开局就引入的差异。

**关键会话选项（与 macOS 逐条对齐）：**

```csharp
var so = new SessionOptions();
so.IntraOpNumThreads = threads > 0 ? threads : Math.Min(4, Environment.ProcessorCount);
so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
so.AddSessionConfigEntry("session.disable_prepacking", "1"); // macOS 实测省 290MB，零性能代价
so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
```

**Windows 特有的一项追加实验**（P0 阶段测，不预设结论）：
`so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0")`。
预览是"每 600ms 跑一次、跑完就闲着"的突发负载，ORT 线程池默认会自旋等待下一批活，
在笔记本上表现为持续的 CPU 占用与风扇噪音。关掉自旋通常牺牲个位数百分比延迟、
换掉大部分空转开销。macOS 没测过这项，Windows 上值得测。

**被否决的加速路径：**

| 方案 | 否决理由 |
| --- | --- |
| **DirectML EP** | 我们分发的是 `model_quant.onnx`——**动态 int8**（`DynamicQuantizeLinear` / `MatMulInteger` 系）。DML 对这类动态量化算子支持很差，实际结果多半是逐节点回落到 CPU（还多一份显存拷贝开销），或者被迫改用 fp32 模型（~940MB，首启下载体验直接崩掉）。且 `Microsoft.ML.OnnxRuntime.DirectML` 与 `Microsoft.ML.OnnxRuntime` 互斥，是构建期二选一 |
| **Windows ML**（Windows App SDK 1.8.1+，`Microsoft.Windows.AI.MachineLearning`） | 这是"最 Windows 原生"的路子，也是未来 NPU（Snapdragon 的 QNN、酷睿 Ultra 的 OpenVINO）的正道。但它要求 **Windows 11 24H2（build 26100）及以上**，把 Win10 用户整片切掉；而且走 Windows ML 就**不能再用 `Microsoft.ML.OnnxRuntime` API**，与 §2.2 的 Swift→C# 直译策略正面冲突。**记为 P2**：等 Win11 24H2 覆盖率上来、且有真实 NPU 收益数据后再评估 |
| CUDA EP | 只服务 N 卡用户，包体 +1GB 级，与"轻量常驻工具"定位不符 |

**模型 I/O 契约**（沿用 macOS 已实测的结论，两侧共用同一份模型文件）：

```
IN   speech          float32  [1, feats_length, 560]
IN   speech_lengths  int32    [1]
IN   language        int32    [1]      auto=0 zh=3 en=4 yue=7 ja=11 ko=12
IN   textnorm        int32    [1]      withitn=14  woitn=15
OUT  ctc_logits      float32  [1, logits_length, 25055]
OUT  encoder_out_lens int32   [1]
```

C# 侧用 `OrtValue.CreateTensorValueFromMemory`（钉住托管数组，零拷贝）+
`session.Run(runOptions, inputs, outputNames)`，与 Swift 侧 `ORTValue(tensorData:...)` 一一对应。
**注意与 macOS 同样的生命周期约束**：`OrtValue` 不持有数据所有权，输入数组必须活到 `Run` 返回；
C# 里用 `using` 作用域覆盖即可，比 Swift 侧手动保持 `NSMutableData` 强引用更不容易出错。

### 4.3 特征前端 —— 没有 Accelerate，自写 FFT

这是 Windows 侧**唯一有真实工作量**的一块：macOS 用了 `vDSP_fft_zrip`，.NET 没有内置 FFT。

**选型：自写 512 点 radix-2 复数 FFT（虚部置零），不引第三方数学库。**

理由：
- 帧长 400 → `round_to_power_of_two` → **512 点，固定不变**。预计算一次旋转因子表即可。
- 单帧约 512×9 ≈ 4.6k 次复数蝶形运算。15 秒音频 = 1,500 帧，⚠️ 估算 **5–15 ms**——
  相对 ORT 推理的几百毫秒可以忽略。**先要正确，再谈优化。**
- 直接算复数 FFT（虚部填 0）比 macOS 侧的实数 FFT 打包省心得多：
  vDSP 的 `zrip` 输出是标准 DFT 的 2 倍、且把 Nyquist 分量塞在 `imagp[0]`，
  macOS 代码里那段 `scale = 0.5` + `realp[0]`/`imagp[0]` 特判在 C# 版**根本不需要存在**。
  这一处 C# 版比 Swift 版更简单、更不容易错。
- MathNet.Numerics：为一个固定 512 点的变换引一个通用数值库，不划算。

**逐点规格**（照抄 macOS DESIGN §4.2，参数来自 knf 1.22.3 的生效配置）：

```
frame_opts: samp_freq=16000  frame_length=400  frame_shift=160
            dither=0  preemph_coeff=0.97  remove_dc_offset=true
            window=hamming  round_to_power_of_two=true(→512)  snip_edges=true
mel_opts:   num_bins=80  low_freq=20  high_freq=8000  is_librosa=0（经典 Kaldi HTK mel，不做 slaney 归一化）
```

流程：`x *= 32768` → 分帧 → 去直流 → 预加重（**倒序原地**）→ hamming → 补零到 512 →
FFT → 功率谱 `|X[k]|²`（k=0…256）→ 80 个三角 mel 滤波 → `log(max(e, FLT_MIN))`。

**⚠️ 三个 Swift→C# 的具体陷阱，必须写进代码注释：**

1. **`FLT_MIN` 不是 `float.Epsilon`。** Swift 的 `Float.leastNormalMagnitude` = 1.17549435e-38
   （最小**正规格化**数）；C# 的 `float.Epsilon` = 1.401298e-45（最小**次正规**数），
   两者差 7 个数量级。必须写成 `const float FloatMin = 1.17549435E-38f;`。
   写错不会崩，只会让静音帧的 log 能量偏低几十——这正是最难在测试里发现的那种 bug。
   （金标准测试的静音段能抓到，所以夹具里那 0.62s 合成信号是有价值的。）
2. **预加重必须倒序。** `for (i = n-1; i > 0; i--) x[i] -= 0.97f*x[i-1];` 然后单独
   `x[0] -= 0.97f*x[0]`。顺序颠倒会用到已修改的"未来"值。
3. **mel 滤波器构造用 `double`，只有最终权重降到 `float`**，与 Swift 侧一致。

**可选的 SIMD 提速**（.NET 原生、非第三方）：`System.Numerics.Tensors.TensorPrimitives`
在 .NET 9+ 已内置，`TensorPrimitives.Dot` 可以吃掉 mel 三角滤波的点积，
`TensorPrimitives.IndexOfMax` 正好顶替 CTC 解码里的 `vDSP_maxvi`（25,055 维 × N 帧的 argmax）。
这两处是唯一值得 SIMD 的热点，其余保持标量以维持可读性与数值可预期性。

**验证方式 —— 这是本方案信心的来源：**
`macos/Tests/VoiceTyperTests/Fixtures/` 下的 `fbank_input.f32` / `fbank_reference.f32` /
`lfrcmvn_reference.f32` / `fbank_parity_shapes.json` **已经入库**（✅ 实测，`git ls-files` 确认）。
Windows 测试工程直接以链接文件引用同一份夹具，用同样的 1e-3 阈值比对。
**在 Windows 上跑 fbank 一致性测试不需要 Python 环境、不需要下载模型**（LFR/CMVN 那条需要
`am.mvn`，缺失时跳过）。同一份 Python 金标准同时约束 Swift 与 C# 两个实现——
这比 macOS 当初的处境好得多，macOS 是先造夹具再追平，Windows 是拿着现成答案对。

**退路**：若 C# fbank 在合理努力内追不平（可能性很低），退路不是 vendoring C++ knf
（.NET 侧要 P/Invoke + 双架构构建，代价远高于 macOS），而是**逐帧二分定位**——
夹具粒度到帧、到维度，可以精确定位是分帧、预加重、窗、FFT 还是 mel 出的偏差。

### 4.4 线程模型 —— 专用串行线程替代 `DispatchQueue`

macOS 靠 `@MainActor` + 一条串行 `DispatchQueue` 划出两个执行域。C# 侧的对应物：

| macOS | Windows |
| --- | --- |
| `@MainActor` | WinForms UI 线程 + 现成的 `UiDispatcher.Post`（`Control.BeginInvoke`） |
| `asrQueue`（串行 `DispatchQueue`，`.userInitiated`） | **专用后台 `Thread` + `BlockingCollection<Action>`**（`AsrPump`） |
| `Task { @MainActor in ... }` 回调 | `UiDispatcher.Post(() => ...)` |

为什么是专用线程而不是 `ConcurrentExclusiveSchedulerPair.ExclusiveScheduler`：
后者能保证串行，但每次调度可能落在不同的线程池线程上。ORT session 本身不要求线程亲和性，
但（a）我们要设 `Thread.Priority`/线程名以便在任务管理器和日志里定位，
（b）推理是几百毫秒级的阻塞工作，占着线程池线程会干扰 `HttpClient`、定时器等其它任务，
（c）与服务端"单 worker executor"、macOS"单串行队列"的心智模型一一对应。
一个 `AsrPump` 类约 50 行。

**并发不变量**（与 macOS 逐条相同）：
`SenseVoiceEngine` / `FbankFrontend` 有可变缓冲，**只允许在 AsrPump 线程上访问**；
`RecognitionBuffer.Append` 允许任意线程（内部 `lock`），`Preview`/`Finalize` 只在 AsrPump 上；
`LocalAsrSession` 的所有公共方法与回调都在 UI 线程。

### 4.5 性能与内存 —— ⚠️ 全部待实测，且这是本方案最大的不确定性

macOS/M4 的实测基线（`model_quant.onnx` int8，intra_op=4）：

| 指标 | M4 实测 |
| --- | --- |
| 模型加载 | 0.85 ~ 0.98 s |
| 15s 音频识别（= 预览窗口满载） | 166 ms |
| 30s 音频识别（finalize） | 262 ~ 397 ms |
| 常驻内存（`disable_prepacking=1`） | ~510 MB |

**⚠️ Windows 外推与风险**：典型 x64 笔记本 CPU 在 int8 GEMM 上大致是 M4 的 1/2 ~ 1/4
（有 AVX512-VNNI 的新 Intel/AMD 会好些，老 U 系列会更差）。即 **15s 预览可能落在 300–700ms**。
客户端每 600ms 送一帧音频，如果单次预览要 700ms，预览就吃满了。

**这个风险其实已经被设计本身兜住了**：`LocalAsrSession` 从服务端继承的
`previewInFlight` 跳过机制——有预览在跑就跳过本次调度，跳过的音频在下次预览一并处理。
所以最坏情况不是卡死或堆积，而是**预览刷新变慢**（从 0.6s 一次退化到机器能撑住的频率），
finalize 完全不受影响。这是优雅降级，不是故障。

在此之上加一层 **Windows 特有的自适应**（macOS 没有，因为不需要）：

- 新增配置 `asr.preview_window`（秒，默认 15）——服务端本来就有这个参数，只是 macOS 没暴露。
- **首次加载模型后做一次校准**：用 5 秒静音张量跑一次推理，测出本机 RTF，
  据此把 `preview_window` 自动选到 {15, 10, 6} 之一并落盘 + 记日志。
  代价约 40 行，**不改动识别算法本身，只是调一个服务端早就存在的参数**。
- 用户可在设置里覆盖（进阶项）。

**内存**：⚠️ 估算与 macOS 同量级（~500MB）。Windows 的差别在于**用户更容易看见**——
任务管理器就在那儿。除了沿用「空闲 N 分钟卸载」（默认值曾按更激进的 5 分钟设计，3.1.0 起与
macOS 统一为 **10 分钟**，见 D13——此前的分歧没有实测支撑的理由），再加一个 Windows 原生动作：
卸载 engine 后调用 `SetProcessWorkingSetSize(hProcess, -1, -1)` 把工作集真正还给系统，
否则任务管理器里的数字要等系统内存压力才会降下来。~10 行 P/Invoke。

### 4.6 文本插入 —— 维持剪贴板 + SendInput

macOS 优先走 Accessibility 直写（`AXValue`/`AXSelectedTextRange`），不污染剪贴板。
Windows 有没有对等物？调研结论：**没有可靠的对等物**。

- **UI Automation `ValuePattern.SetValue`**：会**整体替换**控件内容，不能在光标处插入，
  也不能替换选区——对听写场景是错的语义。
- **`TextPattern`**：文档明确说明它**只读**，不提供插入/修改文本的能力。
- **`EM_REPLACESEL` 窗口消息**：只对标准 Win32 Edit 控件有效。Chrome/Electron/VS Code/
  现代 UWP 应用全都不是标准 Edit 控件，覆盖率太低，不值得为它引入一条第二路径。

因此 `TextInsertionService` **原样保留**（备份剪贴板 → 写入 → SendInput Ctrl+V → 500ms 后
若剪贴板未被他人改写则恢复）。这段代码在现有客户端已经稳定运行，零改动搬过来。

一个必须写进 README 的已知限制：**UIPI**——非提权进程无法向提权窗口发送 `SendInput`。
在以管理员身份运行的记事本/终端里听写会静默失败。缓解：检测到目标窗口提权时给出明确提示
（`GetWindowThreadProcessId` + 打开进程失败即判定），而不是让用户以为是识别坏了。

---

## 5. 模块设计

### 5.1 目录结构

```
windows/
├── README.md                          # 面向用户：安装、隐私设置、配置
├── DESIGN.md                          # 本文
├── build.bat                          # 构建 → dist/（x64 + arm64，portable + installer）
├── VoiceTyper.sln
├── VoiceTyper.csproj                  # net10.0-windows
├── app.manifest                       # PerMonitorV2 DPI 感知
├── Assets/icon.ico
├── Resources/correction.md            # 校对提示词（与 macOS 同一份）
├── installer/VoiceTyper.iss           # Inno Setup 脚本
├── scripts/fetch_model.ps1            # 开发/测试用：命令行下载同一份模型
├── App/            Program, TrayApplicationContext, AppCoordinator
├── Core/           AppConfig, AppState, ConfigStore, ConfigMigrator,
│                   SecretStore, VoiceTyperController, MicPermissionProbe
├── Asr/            AsrService, AsrPump, SenseVoiceEngine, FbankFrontend, Rfft,
│                   LfrCmvn, CtcDecoder, TextPostprocessor, ModelLocator,
│                   ModelDownloader, RecognitionBuffer, LocalAsrSession
├── Llm/            LlmCorrector
├── Services/       HotkeyService, AudioCaptureService, TextInsertionService
├── UI/             TrayController, RecordingHud,
│                   SetupForm + SetupForm.Layout（侧栏布局与状态逻辑拆分，见 §16）
├── Support/        Constants, AppLog, NativeMethods, UiDispatcher, StartupRegistration
└── Tests/VoiceTyper.Tests/            # xUnit（9 个文件，实际清单见 §8）
        FftTests, FbankParityTests, TextPostprocessorTests, RecognitionBufferTests,
        ConfigStoreTests, AppConfigValidationTests, LlmCorrectorTests,
        LlmEndpointTests, AudioChunkerTests
```

**关于与 `client-server/client_windows_native/` 的代码重复**：约 1,500 行会被复制一份。
与 macOS 的判断相同，这是**有意的**——两个工程的配置模型、状态机、UI 结构都会分叉，
抽公共类库会过早冻结接口，还要反向改造已进入维护期的老客户端。

### 5.2 ASR 子系统

#### `ModelLocator`
按优先级定位模型目录，第一个通过校验的立即返回：
1. `asr.model_dir`（用户显式指定）
2. `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\`（App 下载落点）
3. `%USERPROFILE%\.cache\modelscope\hub\models\iic\SenseVoiceSmall-onnx\`
   （跑过 Python 服务端的机器**零下载**；同时探测旧版布局 `hub\iic\SenseVoiceSmall-onnx\`）
4. 都没有 → `AppState.ModelMissing`，触发首启下载引导

校验：`model_quant.onnx` 或 `model.onnx` + `am.mvn` + (`tokens.json` | `tokens.txt`) 必须存在；
`config.yaml` 用 YamlDotNet 解析取 `frontend_conf`，缺失时回落硬编码默认值。

> **为什么模型放 `%LOCALAPPDATA%` 而配置放 `%APPDATA%`**：`%APPDATA%`（Roaming）在域环境下
> 会随用户配置文件漫游，往里塞 240MB 模型会让登录变成灾难。配置（几 KB）漫游是**对的**——
> 换台机器热键设置跟着走；模型（240MB）漫游是**错的**。这是 Windows 特有的正确做法，
> macOS 的 `Application Support` 单目录方案不能照抄。

#### `SenseVoiceEngine`
```csharp
sealed class SenseVoiceEngine : ISenseVoiceRecognizing, IDisposable {
    public SenseVoiceEngine(ModelBundle bundle, AsrLanguage lang, int threads); // 加载 = 构造
    public void SetLanguage(AsrLanguage lang);
    public string Recognize(ReadOnlySpan<float> samples);
}
```
会话选项见 §4.2。输出：读 `encoder_out_lens[0]` → 有效帧数 → 切片 `ctc_logits`。
`GetTensorDataAsSpan<float>()` 直接取输出，不做多余拷贝。

抽出 `ISenseVoiceRecognizing` 接口（与 Swift 侧 `SenseVoiceRecognizing` protocol 同理），
让 `RecognitionBufferTests` 能用假引擎测滑窗逻辑，不必加载真模型。

#### `RecognitionBuffer`（服务端 `SenseVoiceSession` 的二次移植）
**原样搬运，不做"改进"**：15 秒预览滑窗、窗口左侧文本固化进 `_committedText`、
滚动时在目标切点 ±100ms 内按 10ms 粒度找能量最低点下刀、`Finalize()` 对完整音频整段重跑。
唯一的 Windows 增量是 `previewWindowSamples` 从常量变成构造参数（§4.5 的自适应）。

#### `AsrService`
```csharp
sealed class AsrService {                      // 所有公共方法在 UI 线程调用
    public AsrState State { get; }             // Unloaded/Loading/Ready/ModelMissing/Failed
    public Action<AsrState>? OnStateChange;
    public Task PreloadAsync();
    public Task ReloadAsync();
    public LocalAsrSession MakeSession(LlmCorrector? corrector);
    public void SessionEnded();                // 重新安排空闲卸载计时
}
```
持有 `AsrPump`。**空闲卸载**：`asr.idle_unload_minutes`（默认 0=永不，与 macOS 一致），
用 `System.Windows.Forms.Timer`（UI 线程，与状态机同域）。卸载时 `engine.Dispose()` +
`SetProcessWorkingSetSize(-1,-1)`。`MakeSession()` 发现未加载时**异步重新加载并与录音并行**。

#### `LocalAsrSession`（接缝层，接口与旧 `StreamingASRClient` 逐字一致）
```csharp
sealed class LocalAsrSession {
    public Action<string>? OnPartial, OnFinal, OnWarning, OnError;
    public void SendAudio(byte[] data);
    public void FinalizeStream(TimeSpan timeout);
    public void Close();
}
```
职责映射（对照服务端 `StreamRecognizeHandler`，与 macOS 的 `LocalASRSession` 一一对应）：
`previewInFlight` 跳过、partial 只在文本变化时下发、`isFinalizing` 后不再补发 partial、
预览异常 → `OnWarning` 且会话继续、300 秒会话上限一次性告警、finalize 看门狗（默认 30s）。

保留 macOS 那处相对服务端的增强：finalize 拿到 ASR 原文后、调 LLM 之前先 `OnPartial(原文)`，
HUD 立刻显示识别结果并切到「纠错中…」。

> **注意一处 C# 特有的坑**：Swift 侧靠 `[weak self]` + `@MainActor` 天然避免了
> 回调在会话已关闭后触发。C# 没有 weak capture 语法糖，`LocalAsrSession` 必须在每个
> `UiDispatcher.Post` 的闭包里**重新检查 `_closed`**（macOS 代码里那些 `guard !self.closed`
> 一个都不能省），并且 `VoiceTyperController` 里"对象引用比较判断是否当前会话"的那套逻辑
> （`ReferenceEquals(_streamingClient, client)`）必须原样保留——它对本地会话同样必要。

#### `ModelDownloader`
✅ **本次重新实测确认端点契约仍然有效**：

| 事实 | 验证结果 |
| --- | --- |
| `GET .../repo?Revision=master&FilePath=config.yaml` | 200, 1,855 B, sha256 `f71e239b…` ✅ 与钉的常量一致 |
| 同上 `am.mvn` | 200, 11,203 B, sha256 `29b3c740…` ✅ |
| 同上 `tokens.json` | 200, 352,064 B, sha256 `a2594fc1…` ✅ |
| `model_quant.onnx` + `Range: bytes=1000-1999` | 302 → OSS → **206 Partial Content**，`content-range: bytes 1000-1999/241216270` ✅ **断点续传可用**，总长与钉的常量一致 |
| OSS 响应头 `x-linked-etag` | `21dc965f…` == 钉的 sha256 ✅（可用于早期校验，但不作为信任源） |
| **`HEAD` 请求** | ⚠️ **返回 404**——ModelScope 这个 API 不支持 HEAD。**下载器绝不能用 HEAD 探测大小**，必须从 GET 响应的 `Content-Length` 读 |

C# 实现比 macOS 版**更简单**：macOS 用 `URLSessionDownloadTask` 的 `resumeData` blob 做续传，
C# 直接用 `HttpClient` + `HttpCompletionOption.ResponseHeadersRead` 流式写入 `<name>.part`，
续传时看 `.part` 的现有长度、发 `Range: bytes=<len>-`。**`.part` 文件本身就是续传状态**，
不需要额外的 `.resume` 副文件，App 退出后重开天然可续。

其余要点与 macOS 一致：4 个文件串行、**先下三个小文件再下 onnx**（网络问题在花掉 240MB 前暴露）、
每个文件下完立即流式 sha256 校验、不匹配则删除重下一次、校验通过后 `File.Move(part, dest, overwrite:true)`。
失败文案给出手动放置路径与 `scripts/fetch_model.ps1`。

### 5.3 与 `VoiceTyperController` 的接缝

改动清单（**只有 4 处**，与 macOS 完全同构）：

1. `BeginBatchRecording` / `PerformBatchRecognitionAsync` **整段删除**（不再有非流式路径），
   `_config.Server.Streaming` 分支随之消失，`BeginRecording` 直接走单一路径。
2. `StreamingASRClient` → `LocalAsrSession`：`ConnectAndStartCaptureAsync`（异步连接 + 连接成功
   才启动录音）整段删除，改为**直接启动录音**——本地引擎没有"连接"这个概念。
   这顺带消掉了现有代码里最绕的一段（连接期间用户连按两次热键导致会话被覆盖的处理）。
   其余 `OnPartial` / `OnFinal` / `OnError` 闭包**一字不改**。
3. `HealthCheckAsync()` 删除，改为观察 `AsrService.State`。
4. `MinimumRecordingDuration = 300ms` **保留**。它现在纯粹是"防误触"，不再是"省流量"。

`AppCoordinator` 改动：
- 删除 `RefreshServerStatusAsync` / `_serverReady` / `ASRClient.HealthCheckAsync` 相关全部逻辑
- `ReevaluateReadinessAsync()` 中 `_serverReady` → `_engineReady = (asrService.State == Ready)`
- 启动时**并行**发起模型预加载与麦克风可用性探测

### 5.4 LLM 纠错（`LlmCorrector`）

`macos/Sources/VoiceTyper/LLM/LLMCorrector.swift` 的直译，逻辑不变：
system prompt 从 `Resources/correction.md` 读（**与 macOS 同一份文件，不改一个字**）、
8 组固定 few-shot（与 macOS 逐字一致）、`<asr_text>` 标签包裹、`maxTokens = max(configured, len*2+128)`、
`finish_reason == "length"` → 放弃修正返回原文、防御性剥离回显标签、
任何失败 → 记日志 + `OnWarning` + **使用 ASR 原文**。

用 `HttpClient`（单例，`Timeout` 按配置）+ `System.Text.Json`。
设置页保留「测试纠错」按钮。

> 小注：`text.count`（Swift，按字符）→ `text.Length`（C#，按 UTF-16 码元）。
> 对 BMP 内的中英文两者相同；emoji / 生僻字（代理对）时 C# 数值偏大，只会让 max_tokens 更宽松，无害。

### 5.5 配置与密钥

| 项 | 路径 |
| --- | --- |
| 配置 | `%APPDATA%\VoiceTyper\config.yaml` |
| 日志 | `%APPDATA%\VoiceTyper\logs\` |
| 模型 | `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\` |
| LLM API Key | `%APPDATA%\VoiceTyper\llm_api_key.dat`（DPAPI 加密） |

**首次启动一次性迁移**（`ConfigMigrator`）：若新路径无配置而 `%APPDATA%\voice_typer\config.yaml`
存在，则继承其中的 `hotkey` 与 `ui.opacity`，`server` 段丢弃。老用户换过来热键不用重设。

**新 schema**（与 macOS 同构，仅 `preview_window` 为 Windows 独有字段；各默认值自 3.1.0 起与 macOS 一致）：

```yaml
asr:
  language: "auto"          # auto / zh / en / yue / ja / ko
  threads: 0                # 0 = 自动（min(4, 核数)）
  model_dir: ""             # 留空 = 按 ModelLocator 优先级自动定位
  preview_window: 0         # 秒；0 = 首次加载后按实测 RTF 自动校准（§4.5）
  idle_unload_minutes: 10   # 0 = 常驻不卸载（与 macOS 一致）
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key 不在此文件，见下
hotkey:
  modifiers: ["ctrl"]
  key: "f2"
ui:
  opacity: 0.85
  interface_language: "zh"   # zh / en；界面语言（默认中文），重启后全面生效
```

**API Key 存 DPAPI**，不落 YAML：
`ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser)`，
Base64 后写入 `llm_api_key.dat`。约 40 行，依赖 `System.Security.Cryptography.ProtectedData` 包。

> 备选是 **Windows 凭据管理器**（`CredWrite`/`CredRead` P/Invoke）——它是 Keychain 更"对等"的
> 类比，好处是密钥会出现在系统的凭据管理器 UI 里、用户可自行查看删除。代价是约 120 行互操作
> 且底层同样是 DPAPI 保护，安全性无实质差别。**选 DPAPI**：代码量 1/3，行为可预测。
> 若将来要做"多设备同步"或"用户自助清除"，再切凭据管理器。

### 5.6 状态机与 UI 变更

**`AppState`**：新增 `ModelMissing` / `DownloadingModel` / `ModelLoading` 三态。
`AppStateInfo` 是 `readonly record struct`，加一个 `double Progress` 字段承载下载进度。

```
Booting ──┬─→ SetupRequired（麦克风不可用）─────────────────┐
          ├─→ ModelMissing ──→ DownloadingModel(0…1) ──────┤
          └─→ ModelLoading ────────────────────────────────┴─→ Idle → Recording
                    └─(失败)→ Error（设置页可「重新加载模型」/「重新下载」）  → Recognizing
                                                                            → Inserting → Idle
```

麦克风与模型是**两条互不依赖的准备线**，可并行推进。只有两条线都就绪才进 `Idle`。

**托盘菜单**（删掉「重新连接服务」）：

```
┌ 就绪 · CTRL+F2 · 引擎已就绪         ← 三行只读 header
├──────────────────────
│ 设置...
├──────────────────────
│ ✅ 开机自启                          ← 新增
│ 关于 VoiceTyper                      ← 新增
├──────────────────────
│ 退出
```

第三行从「服务：已连接 127.0.0.1:6008」改为「引擎：已就绪 / 模型加载中… / 下载模型 42%」。
下载中时托盘图标叠加进度环（`TrayController` 已有 `Graphics` 绘图代码可复用）。

**设置窗口（初版历史设计）**：Tab 从 2 个（连接/热键）变为 **4 个**；当前五项导航见 §16。

| Tab | 内容 |
| --- | --- |
| 识别 | ① **模型卡片**：未就绪时「需要下载语音模型 · 240 MB」+「开始下载」+ 进度条（已下/总量、速度）+「取消」；就绪时「SenseVoice-Small · int8 · 已就绪」+ 路径 +「重新加载」<br>② 识别语言 下拉<br>③ 智能纠错：开关 + Base URL + API Key + 模型 + 温度 + 超时 +「测试纠错」 |
| 热键 | **原样保留** |
| 权限 | 麦克风可用性检测 +「打开 Windows 隐私设置」（`ms-settings:privacy-microphone`）+ UIPI 提权限制说明 |
| 通用 | 开机自启、HUD 不透明度、空闲 N 分钟后卸载模型、预览窗口（进阶） |

2026-09-30 已拆为 `SetupForm.cs`（状态、热键录制、协调器接口）与
`SetupForm.Layout.cs`（五项导航、布局组件、跨页草稿与统一保存）。上表保留初版结构的历史记录；
当前页面分组见 [Windows README 的设置说明](README.md#设置)。

**麦克风权限探测**（`MicPermissionProbe`）：Windows 对非打包桌面应用的麦克风管控在
设置 → 隐私和安全性 → 麦克风 → "让桌面应用访问你的麦克风"。程序侧只能通过
**实际尝试打开设备**来判断（现有 `AudioCaptureService` 已经在捕获 `COMException 0x80070005`
并区分 `IsAccessDenied`）。做一次极短的静默打开-关闭探测即可，无需新的 API。首次启动（引导未完成）时在启动阶段探测；
引导完成后不再每次开机探测（会让任务栏的麦克风使用指示闪一下），改为打开设置页 / 引导页时按需探测。

**开机自启**（`StartupRegistration`）：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
的 `VoiceTyper` 值。约 30 行。不用计划任务（需提权）、不用启动文件夹（用户容易误删且无法程序化查询状态）。

---

## 6. 配置项取舍对照

| 现有配置项 | 去向 |
| --- | --- |
| `server.scheme` / `host` / `port` / `timeout` | **删除** |
| `server.api_key` | **删除** |
| `server.streaming` | **删除**（本地只有一条路径，天然流式） |
| `server.llm_recorrect` | → `llm.enabled` |
| 服务端 `--model` / `--offline-model` / `--punc-model` | **删除**（只留 SenseVoice-Small） |
| 服务端 `--device` / `--chunk-size` / `--onnx-threads` | `device` 删除；`threads` 降级为 YAML 进阶项 |
| 服务端 `--sensevoice-language` | → `asr.language`，**提升到设置面板** |
| 服务端 `--llm-*` 六个启动参数 | → `llm.*`，**全部提升到设置面板** |
| `hotkey.*` / `ui.opacity` | 保留 |
| `ui.width` / `ui.height` | **删除**（HUD 尺寸自适应内容） |
| —— | **新增** `asr.model_dir`、`asr.idle_unload_minutes`、`asr.preview_window` |

用户可见配置项：**12 → 6**，其中"必须配置才能用"的项：**0**。

---

## 7. 构建与分发

`windows/build.bat` 基于现有脚本改造，产物矩阵变化较大：

| 产物 | 形态 | 体积证据 | 说明 |
| --- | --- | ---: | --- |
| `VoiceTyper-<ver>-win-x64-setup.exe` | Inno Setup 安装包（**主推**） | 尚未实测安装器压缩体积 | 装到 `%LOCALAPPDATA%\Programs\VoiceTyper`；安装前检查目标架构的 .NET 10 Desktop Runtime |
| `VoiceTyper-<ver>-win-x64-portable.zip` | 依赖框架的目录式发布 | 见 [体积实测](PACKAGE_SIZE_AUDIT.md) | 解压后使用系统安装的 .NET 10 Desktop Runtime |
| `VoiceTyper-<ver>-win-arm64-setup.exe` | 同上，arm64 | 尚未实测安装器压缩体积 | Snapdragon X 笔记本；需要 arm64 桌面运行时 |
| `VoiceTyper-<ver>-win-arm64-portable.zip` | 同上，arm64 | 见 [体积实测](PACKAGE_SIZE_AUDIT.md) | arm64 发布成功不代表 arm64 真机运行通过 |

**关键决策：放弃 `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`。**

现有 `client-server/client_windows_native/build.bat` 用的就是这套。加进 14MB 的 `onnxruntime.dll` 之后它变成负担：
自解压模式会在每个新版本首次启动时把原生库解压到 `%TEMP%\.net\...`，
对一个**开机自启的常驻工具**来说，这既拖慢启动，又是杀毒软件误报的高发路径。
改为**目录式部署 + 安装包**：Inno Setup 生成的安装器同样是"双击就装完"的体验，
但文件老实躺在磁盘上，启动零解压、AV 友好、增量更新也更容易做。

其余构建要点：
- `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false`
- **不携带共享运行时**：2026-09-30 用户明确选择使用系统已有的 .NET 10 Desktop Runtime；
  安装器按目标架构检查官方安装记录，便携版保留 .NET 启动器自带的缺失框架提示。
  升级时根据 `installer/legacy-self-contained-files.iss` 中的已知历史文件名清理旧运行时；
  不使用通配符删除用户额外文件。安装器编译与升级行为的验证状态见体积检查记录。
- **不开 ReadyToRun**：此前仅推测登录后启动收益，未实测；当前优先缩小分发体积。
  该设置不会改动 ONNX Runtime 的原生推理实现。
- **不开 `PublishTrimmed`**：WinForms 依赖内建 COM 封送，当前 SDK 不支持裁剪该应用类型；
  不绕过 SDK 限制手动删除框架 DLL（[微软说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)）。
- NAudio 仅引用 `NAudio.Wasapi` 与传递依赖 `NAudio.Core`；DPAPI 使用共享框架提供的程序集，
  不再显式引用编译器提示冗余的 ProtectedData NuGet 包。
- 发布不含 PDB / 原生 `.lib`；调试符号保留在构建目录。`scripts/verify_publish.ps1` 由构建与 CI
  共用，防止把共享运行时、模型、无用音频模块或测试文件再次打进包。
- `Version` 从 csproj 单点读取，安装包版本号与之同步
- **构建脚本的可移植性约束**（2026-10-08 真机踩坑后固化）：
  - `.bat` 必须以 **CRLF** 检出。本仓库常见 `core.autocrlf=input`，会把 LF 行尾的批处理
    检出成 cmd.exe 无法可靠解析的形态（吃行首字符、把注释当命令执行）。根目录
    `.gitattributes` 强制 `*.bat`/`*.cmd` 为 `eol=crlf`，新增批处理脚本自动受保护。
  - `build.bat` 内容保持**纯 ASCII**。`chcp 65001` 下 cmd.exe 在执行外部命令后回读文件时，
    会因多字节 UTF-8 字符算错偏移、从字符中间重新进入脚本（实测注释被切成两半当命令执行）。
    中文解释写到 README/DESIGN，不进 `.bat`。
  - 脚本内调用 `powershell -File` 一律带 `-ExecutionPolicy Bypass`：多数 Windows 默认
    Restricted 策略会拒绝加载 .ps1，导致打包误报失败。`fetch_model.ps1` 带 UTF-8 BOM，
    保证 Windows PowerShell 5.1 正确按 UTF-8 读取中文提示。
- **代码签名**：本轮不做。SmartScreen 会对未签名安装包显示"Windows 已保护你的电脑"，
  README 需给出"更多信息 → 仍要运行"的截图说明。与 macOS 不做公证是同一个取舍位。

`scripts/fetch_model.ps1`：开发与测试辅助，用同一组 URL 和 sha256 把模型拉到
`%LOCALAPPDATA%\VoiceTyper\models\`，供跑金标准测试或跳过首启引导用。

---

## 8. 测试策略

新增 `windows/Tests/VoiceTyper.Tests/`（xUnit）。现有 Windows 客户端**没有任何测试**，这次补上。

当前实际落地的测试如下（9 个文件）。除标注外都不需要真实模型、不需要 Python、不需要网络，
可直接跑在 CI（如 GitHub Actions 的 `windows-latest`）上——前提是先有一台能装 .NET SDK 的
机器把工程编译起来（见顶部状态框）：

| 测试 | 内容 | 通过标准 | 需要模型？ |
| --- | --- | --- | --- |
| **FftTests** | 自写 FFT vs 朴素 DFT（512 点，随机输入） | `max‖Δ‖∞ < 1e-4` | 否 |
| **FbankParityTests** | 链接引用 `macos/Tests/VoiceTyperTests/Fixtures/fbank_input.f32` → 比对 `fbank_reference.f32` 与 `lfrcmvn_reference.f32`；另含全零输入的对数下限回归 | 帧数**完全相同**；`max‖Δ‖∞ < 1e-3` | 仅 LFR+CMVN 部分需 `am.mvn`，缺失自动跳过 |
| **TextPostprocessorTests** | `<\|...\|>` 剥离、中英间距、纯标点丢弃、`▁` 还原 | 覆盖每条规则；用例与 macOS 测试同源 | 否 |
| **RecognitionBufferTests** | 假引擎驱动滑窗：窗口滚动、切点能量最小、`Finalize` 走完整音频 | 预览单调增长不回退为空；finalize 输入长度 == 总样本数 | 否 |
| **ConfigStoreTests** | YAML 读写往返、缺字段回落默认、`voice_typer` 目录迁移 | 往返幂等；迁移只带走 hotkey/opacity | 否 |
| **AppConfigValidationTests** | 配置字段越界夹逼、非有限浮点重置、裸键热键回落默认值 | 直译 macOS `AppConfigTests` 用例 | 否 |
| **LlmCorrectorTests** | `HttpMessageHandler` 打桩：正常 / `finish_reason=length` / 5xx / 超时；tags-only 不丢文本；异常不含响应正文；`TestAsync` 抛真实错误 | 失败路径均返回**原文**且不泄露响应体 | 否 |
| **LlmEndpointTests** | scheme/host 白名单、明文 HTTP 限回环私网、后缀去重 | 直译 macOS `LLMEndpointTests` 用例 | 否 |
| **AudioChunkerTests** | 定长分帧、跨调用累积余量、`Drain()` 尾音、空输入 | 直译 macOS `AudioChunkerTests` 用例 | 否 |

以下两个测试在初版方案中规划，**尚未实现**——补齐之前，不能视为 G2 在 Windows 侧已有
端到端验收：

- `ModelDownloaderTests`：进度节流上界、HTTP 非 2xx、Range 续传（206）、sha256 不匹配。
- `EndToEndRecognitionTests`：G2 的正式验收载体（完整链路跑 `speech_zh_en_mixed.wav`，
  与 `.reference.txt` 编辑距离 ≤ 2）。当前 Windows 识别正确性由特征级金标准测试间接保障，
  文本级端到端验收仍由 macOS 侧承载。

> **已采纳的仓库改动**（初版方案中的建议）：`.gitignore` 已加否定规则
> `!macos/Tests/VoiceTyperTests/Fixtures/*.wav`，`speech_zh_en_mixed.wav`（974 KB）已入库，
> 两个平台的 E2E 测试夹具齐备。

实施顺序沿用了 macOS 的经验：先让 `FbankParityTests` 追平已有夹具，再写后续模块，
每一步都有明确的数值反馈，不靠"看起来对"。

---

## 9. 老客户端改名（`client-server/client_windows_native/`）

| 文件 | 改动 |
| --- | --- |
| `VoiceTyper.csproj` | `AssemblyName` / `Product` → `VoiceTyperClient`；`Description` 加"分体部署"；**版本 3.0.0 不变** |
| `VoiceTyper.sln` | 项目名同步 |
| `Support/Constants.cs` | `AppName` → `"VoiceTyperClient"`；**`ConfigDirectoryName` 保持 `voice_typer` 不动**（老用户配置零迁移） |
| `build.bat` | 产物名 → `VoiceTyperClient-<ver>-win-x64*.exe` |
| `UI/TrayController.cs` | 托盘提示文案 |
| `README.md` | 文案替换，补一条 .NET 8 于 2026-11-10 EOL 的提示 |

Windows 没有 Bundle ID / TCC，改名**不需要用户重新授权任何东西**，代价低于 macOS 侧。

---

## 10. 实施阶段

| 阶段 | 内容 | 产出与验收 |
| --- | --- | --- |
| **P0 探针** | 在**真实 Windows 机器**上建最小工程：`Microsoft.ML.OnnxRuntime` 1.24.2 + 加载 `model_quant.onnx` + 跑一次固定输入。测：加载耗时、1s/5s/15s/30s 推理耗时、常驻内存（开/关 `disable_prepacking`、开/关 `allow_spinning`）、自包含产物体积 | **§4.5 的全部 ⚠️ 数字换成实测值**；据此定 `preview_window` 默认档位与 `idle_unload_minutes` 默认值。**这是唯一有真实不确定性的阶段，必须先做** |
| **P1 骨架** | 建 `windows/` 工程（net10.0-windows、x64+arm64）；搬运 §2.1 的 1,500 行；ASR 层先用假实现（返回固定文本） | 能编译、能起托盘、按热键能把固定文本上屏 |
| **P2 引擎** | `Rfft` + `FbankFrontend` + `LfrCmvn` + `SenseVoiceEngine` + `CtcDecoder` + `TextPostprocessor`；链接 macOS 夹具 | **FftTests / FbankParityTests（含 LFR+CMVN 部分）全绿**（G2 的特征级验收达成，最关键的一步） |
| **P3 会话** | `AsrPump` + `RecognitionBuffer` + `LocalAsrSession` + `AsrService`；接进 `VoiceTyperController` | 真实录音 → 实时预览 → 松手上屏，端到端跑通 |
| **P4 配置与 UI** | 新 `AppConfig` schema、`ConfigStore`、`ConfigMigrator`、DPAPI；设置窗 4 Tab；托盘精简；三个新状态；开机自启；DPI/圆角改进 | 全新用户路径：打开 → 可用；老用户热键自动继承 |
| **P5 模型获取** | `ModelDownloader`（串行、Range 续传、sha256、原子落盘）+ 模型卡片 UI + `ModelMissing`/`DownloadingModel` 状态 + 预览窗口自校准 | 删掉本机所有模型副本后重走一遍：下载 → 校验 → 加载 → 可听写；中途断网可续 |
| **P6 纠错** | `LlmCorrector` + 设置页 +「测试纠错」 | 开关纠错前后差异符合预期；断网/错 key 不丢文本 |
| **P7 打包** | `build.bat`（x64 + arm64 × portable + installer）、Inno Setup 脚本、图标、许可证声明、`fetch_model.ps1` | 在**干净的另一台 Windows** 上装安装包走通 G1（含首启下载） |
| **P8 收尾** | 老客户端改名；根 `README.md` / `CLAUDE.md` 与 `client-server/PROTOCOL.md` 同步；`windows/README.md` | 文档与实现一致，`client-server/PROTOCOL.md` 标注适用范围不含 `windows/` 与 `macos/` |

---

## 11. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| **⚠️ Windows CPU 上预览跟不上 600ms 节奏** | 预览刷新变慢、CPU/风扇吵 | 已被 `previewInFlight` 跳过机制兜住（优雅降级，非故障）；再加 `preview_window` 自校准（§4.5）与 `allow_spinning=0`。**P0 必须先测出真实数字** |
| **fbank 数值对不齐** | 识别质量下降且难察觉 | 夹具已现成（✅ 已入库），1e-3 逐点比对；三个已知陷阱（`FLT_MIN`、预加重倒序、double 构造滤波器）已写进 §4.3；退路是逐帧二分定位 |
| **常驻内存 ~500MB** | 任务管理器里显眼，用户投诉 | `disable_prepacking`（macOS 实测省 290MB 零代价）+ 空闲卸载（可配置，默认从不，D13）+ 卸载后 `SetProcessWorkingSetSize` 归还工作集 |
| **首启下载失败**（断网、ModelScope 抽风、磁盘满） | 新用户第一印象直接卡死 | Range 续传（✅ 本次实测 206 可用）+ sha256 校验（✅ 三个小文件本次实测全对）+ 小文件先行；失败文案给手动放置路径与 `fetch_model.ps1`；`ModelLocator` 复用 `~/.cache/modelscope/` |
| **未签名安装包被 SmartScreen 拦** | 新用户装不上 | README 图文说明"更多信息 → 仍要运行"；长期解法是买 EV 证书（与 macOS 公证同一个决策位，本轮都不做） |
| **UIPI：无法向提权窗口插入文本** | 在管理员终端里听写静默失败 | 检测目标窗口提权并给明确提示，而不是让用户以为识别坏了；README 记为已知限制 |
| **AV 误报**（全局键盘钩子 + 剪贴板 + SendInput + 下载大文件） | 用户被吓退 | 目录式部署代替自解压单文件（减少一大类启发式命中）；README 说明各权限用途；必要时向主流 AV 提交白名单 |
| **ORT NuGet 版本漂移** | 构建不可复现 | csproj 钉死 `1.24.2`；`packages.lock.json` 尚未启用（仓库中没有该文件，csproj 也未设置 `RestorePackagesWithLockFile`），待在 Windows 环境生成 |
| **arm64 变体缺乏验证** | Snapdragon 机器上出问题无人知 | ORT 官方提供 win-arm64 原生库（✅ 实测存在）；若手上无 arm64 设备，README 标注"arm64 构建未经实机验证"，不假装它测过 |
| **无法在本机验证任何 Windows 行为** | 方案里的估算可能全错 | 已在文档顶部与各处显式标注 ✅/⚠️；P0 探针阶段就是为消除这一整类不确定性而存在的 |
| 两个 App 同时安装 | 热键互抢、双份内存 | 配置目录已隔离（`VoiceTyper` vs `voice_typer`）；安装包检测到旧版时提示卸载 |

---

## 12. 决策记录

| # | 决策 | 结论 | 说明 |
| --- | --- | --- | --- |
| D1 | UI 栈 | ✅ **WinForms**（不是 WPF / WinUI 3） | 复用 1,022 行经过实战的 UI；"不抢焦点悬浮窗"已调通，不值得为观感重来一遍 |
| D2 | .NET 版本 | ✅ **net10.0-windows** | .NET 8/9 于 2026-11-10 EOL；ORT 托管包提供 net8.0 资产，兼容无碍 |
| D3 | 新 App 版本号 | ✅ 首发 3.0.0（与 `macos/` 对齐），现为 **3.2.1** | 老客户端改名为 `VoiceTyperClient` 后，其 3.0.0 归属另一条产品线，不冲突。3.1.0 承载 §13 的 35 项行为对齐（W-32）；3.2.1 承载 §14 对 macOS 4c33be2 的补齐。版本号与 macOS 保持拉齐 |
| D4 | ORT 版本 | ✅ **钉 1.24.2**，与 `macos/` 一致 | 消掉"不同 ORT 版本导致 argmax 翻转"这一整类变量；升级另开一次带金标准复测的变更 |
| D5 | 执行提供者 | ✅ **仅 CPU EP** | DirectML 对动态 int8 支持差；Windows ML 要 Win11 24H2 且与直译策略冲突（记为 P2） |
| D6 | FFT 实现 | ✅ **自写 512 点 radix-2**，不引数学库 | 固定尺寸、算法确定、有金标准兜底；比 macOS 的 vDSP 打包方案更简单 |
| D7 | 目录布局 | ✅ **配置 `%APPDATA%\VoiceTyper\`，模型 `%LOCALAPPDATA%\VoiceTyper\models\`** | 配置（KB 级）该漫游，模型（240MB）绝不能漫游。这是 Windows 特有的正确做法，不能照抄 macOS 单目录方案 |
| D8 | 密钥存储 | ✅ **DPAPI 文件** | 40 行 vs 凭据管理器的 120 行互操作，底层同样是 DPAPI，安全性无差别 |
| D9 | 分发形态 | ✅ **依赖系统 .NET 10 Desktop Runtime 的目录式 + Inno Setup**，放弃单文件自解压 | 不重复分发共享运行时；启动时不解压原生库；体积实测见 [PACKAGE_SIZE_AUDIT.md](PACKAGE_SIZE_AUDIT.md) |
| D10 | 架构覆盖 | ✅ **x64 + arm64** | ORT 两个 RID 原生库齐备，没有 macOS 侧"放弃 Intel"那种被迫取舍 |
| D11 | 文本插入 | ✅ **维持剪贴板 + SendInput** | Windows 没有 macOS AX 直写的可靠对等物（UIA `TextPattern` 只读、`ValuePattern` 整体替换语义错误） |
| D12 | Windows 版本下限 | 待定，暂按 **Win10 1809+** | 若接受只支持 Win11 24H2+，Windows ML/NPU 路线才成立。取决于目标用户构成，P0 前定即可 |
| D13 | 空闲卸载默认值 | ~~待定，暂按 5 分钟~~ → ~~10 分钟~~（3.1.0 起，见 §13）→ **0（从不）**（跟进 macOS 默认调整，见 REVIEW_2026-09-14.md VW-06） | 与 macOS 默认值一致；此前的分歧没有实测支撑的理由，直接统一 |

---

## 13. 与 macOS 3.1.9 的行为对齐（3.1.0）

089c2a6 一次性落地本文档描述的 P0 方案后，Windows 侧再未随 macOS 的 R2/R3/R4 三轮架构评审与
三轮首启/UI 体验优化同步演进，直到本次对齐逐条盘点并拉齐了 35 项差异。本节记录
对齐后仍然存在的**有意分歧**，以及**代码审查通过、尚未在真机上验证**的部分，避免未来又出现
"一边改了十几个提交、另一边完全不知情"的漂移。

### 13.1 有意保留的分歧

| 项 | macOS | Windows | 理由 |
| --- | --- | --- | --- |
| 默认热键 | `fn` | `Ctrl+F2` | fn 由键盘固件处理，不产生扫描码，Windows 拿不到 |
| 文本插入路径 | Accessibility（`kAXSelectedTextAttribute`/`kAXValue`）+ 剪贴板兜底 | 只有剪贴板 + SendInput | UI Automation 的 `TextPattern`/`ValuePattern` 在 Electron/Chromium/Qt 类应用上覆盖率远低于 macOS AX，`ValuePattern.SetValue` 还会整字段重建（丢 undo、丢富文本）；剪贴板 + SendInput 已是 Windows 上最可靠的通路 |
| 权限模型 | TCC 强制门（辅助功能/输入监控），未授权直接阻断热键监听 | 无对应机制；`WH_KEYBOARD_LL`/`SendInput` 不需要授权，只受 UIPI 提权边界限制 | Windows 无 TCC 概念；已由 `TextInsertionService.IsForegroundWindowElevated` 覆盖等价场景 |
| 状态栏视觉 | SF Symbol 动效 | 自绘状态色点 | 无直接对应物，色点已表达同等信息量 |
| 空闲卸载后 | 无对应概念 | `SetProcessWorkingSetSize` 归还工作集 | Windows 独有能力，macOS 没有等价 API |
| `preview_window` | 固定 15s，无自校准 | 首次加载后按实测 RTF 自动校准 15/10/6s | Windows 独有的更优设计（见下） |
| 断点续传 | `.resume` 副文件 | `.part` 文件自身长度即续传状态 | Windows 方案更简单，且不会踩到 macOS 侧记录过的"CFNetwork 对畸形 resume data 直接 abort 进程"那个坑 |
| 剪贴板隐私标记 | `org.nspasteboard.ConcealedType`/`TransientType`（社区约定） | `ExcludeClipboardContentFromMonitorProcessing`/`CanIncludeInClipboardHistory`/`CanUploadToCloudClipboard`（系统级） | Windows 的三个格式是系统本身承认的等价物，覆盖比社区约定更彻底（Win+V 历史 + 云剪贴板） |
| LLM 功能叫法 | 「智能校对」 | 「智能纠错」 | 有意的措辞分歧：旧分体式客户端两侧均用「纠错」，macOS 一体化版改用「校对」，Windows 维持「纠错」；各自平台内 UI、文档、日志保持一致即可，不构成行为差异 |

后两项（`preview_window` 自校准、`.part` 续传）是 Windows 优于 macOS 的设计，建议后续单独立项反向
移植回 macOS，不与本次对齐混在一起。

### 13.2 已代码审查、尚未真机验证

以下改动逻辑上正确（对照 macOS 实现逐行核对），但本轮对齐完全在没有 .NET SDK、没有 Windows
机器的环境下完成，**没有编译、没有运行过**。合入后第一件事必须是在真机上把 `dotnet build` /
`dotnet test` 跑绿，其中这几项需要额外的手工验证（见 `windows/README.md`「日志与排障」）：

- **全局键盘钩子存活性自愈**（`HotkeyService.CheckHookHealth`）：Windows 侧的新增设计，非直译。
  30 秒周期比对系统级"最近一次用户输入时间"与钩子自身最近一次被调用的时间，判定摘钩后重装。
  逻辑对齐 macOS tap 的 `tapDisabledByTimeout` 处理，但触发系统摘钩的真实场景（长任务阻塞 UI
  线程 5 秒+）需要真机验证。
- **CTC logits 零拷贝解码**（`SenseVoiceEngine.Recognize`）：优先走 `DenseTensor<float>.Buffer.Span`
  直接引用 ORT 输出的底层内存，避免 `ToArray()` 全量拷贝；仅在 ORT 返回非 `DenseTensor` 实现时
  才退化为拷贝。`Microsoft.ML.OnnxRuntime` 1.24.2 在 Windows 上是否总是返回 `DenseTensor<float>`
  需真机确认。
- **权限页轮询间隔放宽到 4 秒**（`SetupForm.RefreshPermissionPolling`）：`MicPermissionProbe` 靠真开
  一次 WASAPI 采集探测麦克风可用性，轮询过密会让系统托盘的"麦克风使用中"指示灯反复闪烁；
  4 秒是估算值，需真机确认观感是否可接受。
- **常驻内存/推理耗时的具体数字**：本节所有改动都不改变 §4.5 标注为"待实测"的结论——仍然需要
  P0 阶段的真机测量。
- **`INPUT` 原生布局**（`NativeMethods.InputUnion` 补 `MOUSEINPUT`/`HARDWAREINPUT`，R1-2）：结构体
  测试断言 `Marshal.SizeOf<INPUT>() == 40`，但"记事本真的收到粘贴文本"必须真机验证，两者分别记录。
- **前台窗口提权判定**（`TextInsertionService.CheckForegroundWindowElevation`，R3-2）：改为读
  `TokenElevation` 与本进程比较，无法判定时返回 `Unknown`；UIPI 拦截提示的实际触发需真机验证。
- **下载连接/无进度超时 30s**（`ModelDownloader`，R3-4）：取值理由见代码注释，真实网络下的观感待确认。
- **单实例 `Local\` 前缀**（`Program.cs`，R4-1）：多用户 / RDP / 快速用户切换下的互不误阻需真机验证。
- **卸载清理自启项**（`installer/VoiceTyper.iss` `[Registry]` 段，R4-2）：`uninsdeletevalue` 对运行期
  创建的注册表值的清理效果需在真机卸载时确认。

### 13.3 剪贴板 + Ctrl+V 方案的固有限制（非缺陷，不再挂待修）

- **`SendInput` 成功 ≠ 目标已粘贴**：`SendInput` 只表示事件已入队；目标控件是否接收、是否支持
  Ctrl+V 无法从注入侧确认。控制器只据此区分"已发送粘贴 / 结果已复制 / 复制失败"三态，不声称
  通用成功（R3-2）。
- **固定 1s 剪贴板恢复窗口**：对读取剪贴板特别慢的应用可能偏短。可靠的替代方案（UI Automation
  逐控件写入、完整输入法）超出当前架构边界。macOS 侧同样如此。
- 这两条与 macOS 的剪贴板兜底路径同源，作为已知限制记录，不作为待修缺陷。

---

## 14. 与 macOS 3.2.1 的对齐（3.2.x）

§13 把 Windows 拉齐到 macOS **3.1.9** 的行为基线。此后 macOS 到 3.2.1 之间只有一个提交带
真实行为变更：`4c33be2 fix(macos): harden local dictation recovery paths`（3.2.0→3.2.1 之间的
提交、以及 HUD 视觉层级、校对提示词等，均已随各自提交同步或不涉及 Windows）。本节把该提交的
4 处加固逐行直译到 Windows：

| # | 文件 | 加固点 | 直译要点 |
| --- | --- | --- | --- |
| 1 | `Asr/LocalAsrSession.cs` | 单段上限裁剪统一到 `AcceptWithinCap` | ① 引擎长时间未就绪时 `_pendingAudio` 也严格受 120s 上限约束，不再无限积累；② 跨界 chunk（`currentCount + chunk > 上限`）只追加剩余容量内的前缀，不让计数跨过上限；③ `TriggerCapOnce` 统一 warning + `OnSessionCapped`，恰好填满不触发（保留"下一入口才触发"的既有语义） |
| 2 | `Llm/LlmCorrector.cs` | `CorrectAsync` 返回 `CorrectionOutcome` | 区分「纠错成功（文本可能与原文相同）」与「回落原文」（网络/超时/鉴权/解析失败，或 `finish_reason==length`/空 content/剥标签后为空的语义回落）。`TestAsync` 契约不变（返回 `.Text`，失败仍抛 `LlmException`） |
| 3 | `Asr/LocalAsrSession.cs` | 纠错回落时发非致命提示 | `CorrectAndFinishAsync` 在 `outcome.DidFallBack` 时 `OnWarning?.Invoke("智能纠错未成功，已使用识别原文")`，再以原文 `OnFinal`。措辞沿用 Windows 的「纠错」（§13.1 有意分歧） |
| 4 | `Services/TextInsertionService.cs` | 连续粘贴兜底继承 pending 快照 | 新增 `PendingRestore`（原始快照 + 写入的临时文本 + 写入后的剪贴板序列号）。临时恢复窗口内再次兜底、且剪贴板仍是上一段听写写入的临时文本（序列号 + 文本双重比对，纯函数 `ShouldInheritPendingSnapshot`）时，**继承**上一次的「用户原始快照」，避免连续两次兜底把用户最初的剪贴板永久覆盖 |

附带：`Core/VoiceTyperController.cs` 的 `ResumeHotkeyListening` 在 `_hotkeyService.Start` 抛异常时
复位 `_isRunning = false` 再抛出，对齐 macOS `resumeHotkeyListening` 的"恢复失败不留下
自认为在跑、实际热键已死的状态"。Windows 当前经由 `AppCoordinator.ReloadAndReevaluateAsync`
的整体 `Stop/Dispose/重建` 路径，未直接调用该方法（macOS 用 Suspend/Resume，Windows 用重建），
此处修改为直译保真、防止未来接线时踩坑。

macOS 4c33be2 里的 `build_xcode.sh`（`ditto` 替代 `zip -r` 保 framework 符号链接）纯属 macOS
打包问题，Windows 走 Inno Setup 目录式部署、无 framework bundle，不涉及。

### 14.1 测试

- `Tests/VoiceTyper.Tests/LlmCorrectorTests.cs`：断言改为 `CorrectionOutcome`；新增「模型返回与原文一致的文本仍报 `Corrected` 而非 `FellBack`」。
- `Tests/VoiceTyper.Tests/LocalAsrSessionCapTests.cs`（新）：`_pendingAudio` 上限裁剪与一次性触发（引擎返回 `null` 的同步路径，无需真实模型）。
- `Tests/VoiceTyper.Tests/TextInsertionServiceTests.cs`（新）：`ShouldInheritPendingSnapshot` 的继承/不继承判定。
- 未在真机上跑过 `dotnet test`（见 §13.2 的既有说明）。


---

## 15. 与 macOS 3.5.0 的对齐（3.5.0）

§13 / §14 把 Windows 拉到 macOS 3.2.1。此后 macOS 经历了 R5（首启引导、热键触发方式与就绪反馈、
HUD 反馈与性能优化）、3.3.x 的默认值与加载期修复、3.4.0 的双语界面（已同步）以及 R7（耗时度量、LLM 提速、
输入设备、修饰键热键、动效）。本节记录 Windows 侧的落地方式与**有意的差异**；逐项变更清单见 [CHANGELOG.md](CHANGELOG.md)。

### 15.1 控制器：一次听写 = 一个 `Utterance`

`VoiceTyperController` 重写为与 macOS 同构的结构：

- 同一时刻至多一个 `Utterance`（会话 + 前台窗口 + 起点时间 + 阶段 + 耗时度量），所有终止路径
  （识别完成 / 出错 / Esc 取消 / 短录音丢弃 / 组合手势作废 / `Stop()`）只走 `Finish()`，
  靠「先取走 `_active` 再处理」保证幂等。§13/§14 里为「旧会话在后台完成」写的
  `ReferenceEquals` 分支和「复制到剪贴板」兜底因此被取代：不再允许听写重叠，新的一次按键在上一段未结束时被拒绝并提示。
- 外部依赖经 `Core/DictationAbstractions.cs` 的接口注入（`IHotkeyListening` / `IAudioCapturing` /
  `ITextInserting` / `IDictationSession(Factory)`），时钟与静音探测调度可注入，状态机可脱离真实钩子与麦克风单测。
- **门禁**：`BlockedReason` 非空时热键照常监听，按下只提示不开始录音。门禁只拦「新开一段听写」——已有听写进行时
  松键 / 切换模式的第二次按键必须放行（空闲卸载后首次按热键会触发模型加载，协调器随后置上门禁，这是真实会发生的时序）。
- **触发方式**：`hotkey.mode = hold | toggle`。单独修饰键的 toggle 以「干净单击的松开」为切换点。
- **Esc 窗口**：`IHotkeyListening.AcceptsCancelWhenInactive`。默认 Esc 只在按住热键期间受理（否则会吞掉用户正常使用的
  Esc）；有听写进行时（识别阶段、切换模式的录音阶段）打开，收尾时关闭。窗口内的 Esc 被消费。

### 15.2 热键：单独修饰键支持右 Ctrl 与右 Alt

macOS 支持右 ⌘ / 右 ⌥ / 左 ⌥ / 右 ⌃。Windows 上低级钩子**不能吞掉修饰键事件**（吞掉会破坏其他应用看到的 down/up 配对），
所以只有「单击的系统副作用可以被绕开」的修饰键才能用。左 Alt 单击会激活菜单栏（随后的 `Ctrl+V` 粘贴落空）且是近一半
快捷键的前缀、Win 单击弹开始菜单、Shift 单击在中文输入法里切换中英文、左 Ctrl 是几乎所有快捷键的前缀——均不支持；
**右 Ctrl** 单击没有任何系统行为；**右 Alt** 的菜单栏激活副作用用「哑键掩码」绕开（见下）。

`HotkeyStateMachine.ForModifierOnly` 实现「干净单击」：目标键按下且此刻没有别的修饰键 → `Press`；按住期间出现任何非修饰键、
另一个修饰键或鼠标按下（`WH_MOUSE_LL`，仅此类热键才安装，覆盖 Ctrl+点击 / Ctrl+滚轮）→ `GestureCancel`，每次手势最多一次；
干净抬起 → `Release`。Esc 是用户明确的取消意图，走 `Cancel` 而不是静默的 `GestureCancel`。右 Alt 走同一状态机、事件同样
全部放行（`RightAlt_CleanTap_NeverConsumes_LikeRightCtrl`）。

**右 Alt 的菜单栏掩码**（2026-10-08 设计，依据同日本机 Win10 19045 探针实验）：Windows 在**松开**孤立 Alt 时才激活前台
窗口的菜单栏，且「按住期间出现过其他键」即按组合快捷键处理、不激活。因此 `HotkeyService` 在右 Alt 干净手势开始的
`Press` 时同步 `SendInput` 一对哑键（VK 0xFF down + up，不映射任何字符、不影响修饰键状态，AutoHotkey 的修饰键单击
热键即此手法）。掩码失败只记日志，退化为松开时菜单栏被激活（即没有右 Alt 热键之前的老行为），听写本身不受影响。

为什么不用另外两个候选方案（都有实测反证）：
- **吞掉 keyup**：低级钩子吞掉的事件不会更新 `GetAsyncKeyState` 的异步键状态表——实验里放行 down、吞掉 up 后
  `VK_MENU` 的异步状态**永久卡在按下**；`TextInsertionService.AreNonPasteModifiersHeld` 会一直误判 Alt 按住，所有
  `Ctrl+V` 插入降级为「已复制到剪贴板」，前台应用的按键状态同步也被污染。
- **不处理菜单栏**：孤立右 Alt down→up 实测（记事本 + `GetGUIThreadInfo` 的 `GUI_INMENUMODE`）确实进入菜单模式；
  掩码组（Alt down + 哑键对 + Alt up）实测不进入。

AltGr 兼容性：欧洲键盘布局按下 AltGr 时系统会先注入一个假的左 Ctrl（或右 Alt 按下时已有其他修饰键），「此刻没有别的
修饰键才触发」的判定会直接不接管，组合字符不受任何影响；少数无假 Ctrl 的布局上会短暂进入录音再被 `GestureCancel`
静默丢弃（HUD 闪烁已有抑制）。右 Alt+Tab、右 Alt+F4 等组合按住期间即作废手势，照常传递给系统。

⚠️ 待真机验证（本节掩码行为目前仅有注入式按键 + 记事本的自动化实验证据）：物理右 Alt 键在记事本 / Word / 浏览器里
单击触发听写且松开后菜单栏不激活；AltGr 布局键盘打字不受影响；右 Alt+Tab 切窗口正常；哑键对个别以原始输入扫描全部
按键的程序（游戏、远程桌面客户端）无可感知干扰。

### 15.3 输入设备

macOS 的问题是蓝牙耳机进入通话模式；Windows 的对应问题在于原实现取的是 `Role.Communications` 端点，蓝牙耳机连接时它几乎总是
「免提通话」端点。改为 `Role.Console`，并加策略 `audio.input_device`（`auto` / `system` / 端点 ID）：`auto` 仅在默认输入与默认播放
**都**是蓝牙、且存在「内置麦克风」（非蓝牙、非 USB、`PKEY_AudioEndpoint_FormFactor` 为麦克风）时改用它。

⚠️ 端点分类依据 `PKEY_Device_EnumeratorName`（BTHENUM / USB / HDAUDIO …），读不到时退回名称关键字（`Hands-Free` / 蓝牙 / 免提 / USB）。
**这套分类没有在真机上核对过**；纯函数部分（`AudioInputSelector`、`AudioDeviceCatalog.ClassifyTransport`）有单测，
每次录音的耗时日志会记录分类结果供真机核对。

### 15.4 模型下载

`ModelDownloader` 两条路径：单连接（不变，处理了 `416`）与四段并行（≥ 32MB，即 230MB 的权重）。分段用
`Range: bytes=0-0` 探测总长（该端点 `HEAD` 返回 404），各段写独立 `.segN` 文件（续传只看文件长度，不需要任何副文件），
最后按序拼成 `.part`；**任何异常都永久停用分段并落回单连接**——分段只是优化，不能让它把首次安装唯一的必经之路带崩。
最终以固定 sha256 为准。协调器负责失败后 5s / 20s / 60s 的自动退避重试。

`HttpMessageHandler`、文件清单、落点与地址可注入，`ModelDownloaderTests` 用内存里的 Range 服务覆盖了成功 / 忽略 Range / 续传 /
校验失败 / 取消，不联网。⚠️ 真实 ModelScope → OSS 的多段并行是否被接受仍需真机（真实网络）确认；不被接受时的回落路径已有测试。

### 15.5 HUD

`RecordingHud` 重写：电平波形（−50…0 dBFS 映射）、状态行含输入设备名（超 14 字截断）、两行尾部预览（`HudTextLayout.FitTail`，
纯逻辑、测量函数注入）、「纠错中…」、识别为空 / 未就绪 / 错误的一次性提示、三种落点、按 `DeviceDpi` 缩放全部像素尺寸、
系统关闭动画时不做呼吸与脉冲。宽度按档位（420 / 560 / 700 / 860px @96DPI，上限为屏幕宽度的一半）增长，避免一边识别一边窗口抖动。

有意的差异：不做「收声」呼气与结果图标弹入动效；「校对中」用文字亮度呼吸代替图层遮罩流光；WinForms 分层窗口只能整体调透明度，
所以文字与背景一起变淡（下限 40%）。

### 15.6 已修复的既有缺陷

- 原工程无法编译（`MemoryMarshal.Cast(byte[])` 在 C# 14 下的重载变化；xunit v3 才有的 `Assert.SkipWhen`）。
- `interface_language` 没有被序列化：保存任意设置后，界面语言在重启时回到中文。
- 热键保存路径用全新的 `HotkeyConfig` 覆盖整个热键配置，会丢掉触发方式。
- 录音使用 `Role.Communications`（见 §15.3）。

### 15.7 测试

主机为 macOS 时的做法：`dotnet build -r win-x64 -p:EnableWindowsTargeting=true` 交叉编译；把测试产物运行时配置里的
`Microsoft.WindowsDesktop.App` 框架依赖去掉即可在 macOS 上运行不触碰 WinForms 的用例（本地已有 SenseVoice 模型缓存时端到端识别用例
也会真跑，使用 osx-arm64 的 ONNX Runtime）。**触碰 WinForms / Win32 的部分（钩子、剪贴板、SendInput、窗体绘制）无法在这样的环境里运行，
它们的正确性只能靠真机验证。**

## 16. 2026-09-30 下载故障与设置窗口重做

### 16.1 故障证据与处理

本机旧日志记录：ModelScope 权重先返回 HTTP 403，两个备用源随后 SSL 握手失败。
原实现只保留最后一个来源的外层 Message，无法还原当时的证书/代理/握手原因，不能将旧故障
直接归因为 TLS 版本。本次在正常 Windows 网络环境探测到 ModelScope API 会以
`200 + Content-Range` 返回指定范围；同机官方 resolve 路径仍返回 403。
两个 Hugging Face 入口可能重定向至同一 CDN 主机，不是完全独立的故障域。

下载器现兼容 200/206，但必须验证 bytes 单位、起止偏移、总长和可用的 Content-Length。
不匹配时回退，固定 SHA256 保持不变。403/不可恢复 4xx、TLS/代理认证故障立即切源；
超时、断流、429/5xx 允许有限重试。所有来源均为不可恢复故障时停止自动重试，可手动重试。
断流保留 `.part`，校验失败/无效范围/越界时才丢弃。分段与跨源数据最终由固定哈希裁决。

`DownloadFailure` 汇总全部来源，记录脱敏异常链、HRESULT、Win32 错误码与响应主机。
证书回调记录 SslPolicyErrors 和证书链状态枚举，仍拒绝验证失败；系统 TLS 与默认代理策略不变。
签名 URL、URL 内凭据及查询参数不进入日志。诊断窗口可选中文字复制，不自动覆盖剪贴板。
本次未修改 macOS 或 fbank、LFR/CMVN、CTC、模型契约，ASR 金标准夹具继续共用。

### 16.2 设置与保存语义

五页分组见 [Windows README](README.md#设置)。`SetupForm.cs` 保留状态、录制热键和协调器接口，
`SetupForm.Layout.cs` 提供侧栏、公共布局、折叠参数、跨页草稿与统一操作区。
状态刷新与重复打开不能覆盖草稿；关闭保留草稿并恢复已保存透明度，重新打开继续预览；撤销恢复。
模型就绪不会自动隐藏含未保存修改的窗口。

统一保存先校验热键与启用的纠错地址/模型，在写密钥前拒绝活跃听写期间的破坏性修改。
热键和输入设备变更也参与控制器重建判断；保存期间禁止继续编辑。配置保存成功后更新开机自启，
自启失败明确提示部分成功并恢复实际注册表状态，不在拒绝保存时偷偷改变自启。
密钥与 YAML 分开写入，若密钥已更新但 YAML 写入失败，明确提示部分成功并保留草稿重试。

### 16.3 已完成验证

- Windows x64 本机构建、Release 发布成功。原有 NU1510 包裁剪提示和 HudTextLayoutTests 的
  xUnit2000 提示仍存在，本次无新增编译警告。
- 全套 290 项：通过 289 项，唯一跳过为默认关闭的真实联网验收，包含真实语音金标准推理。
- 单独启用联网验收：四文件完整下载、全部固定 SHA256、刚下载模型的 ONNX 会话加载通过。
  首次下载/校验约 37 秒；后续含加载约 30 秒，为本机当次观测，不是性能基准。
- STA 用例验证跨页草稿、后台载入不覆盖、保存失败保留、保存包含热键/设备/密钥与撤销恢复预览。
  真实 WinForms 渲染覆盖中英文五页及窄窗口权限/长错误状态，断言保存按钮未裁切。
- 可运行产物在 `windows/bin/preview-win-x64/`，预览在 `windows/obj/settings-preview/`，均不提交。
  未重装现有应用、未替换用户模型。arm64、实际 DPI 跨屏、热键/权限/剪贴板和长期运行仍需手工验收。

联网验收仅在显式设置 `VOICETYPER_VERIFY_DOWNLOAD=1` 后运行
`dotnet test windows/Tests/VoiceTyper.Tests/VoiceTyper.Tests.csproj --filter FullyQualifiedName~RealNetwork_AllPinnedFilesDownloadAndVerify`。
模型写临时测试目录，结束清理，不触碰用户模型。窗口预览显式设置 `VOICETYPER_SETTINGS_PREVIEW`
为输出目录，并用 `--filter FullyQualifiedName~RenderSettingsPreviews`；使用虚构数据，不读取 SecretStore。
平时 `dotnet test` 不联网、不写用户配置或密钥。

### 16.4 手工验收

1. 退出旧版后运行新 x64 产物。跨页编辑、关闭/重开，检查草稿保留；撤销恢复字段与浮窗透明度。
   保存并重启确认持久化，分别修改热键/麦克风，确认真实听写采用新值。
2. 在独立测试账户验证首次下载、下载中取消、断网/恢复、最终加载；不删除用户原模型。
   取消不触发自动重试，断流可续传。使用不可用代理检查分类与详情，恢复代理后手动重试。
3. 禁用/恢复系统麦克风权限，检查横幅、诊断与重新检测。录制热键时不触发听写，离页恢复监听；
   听写中修改热键/设备并保存应被拒绝，录音不中断。
4. 在记事本听写，确认文字插入与原剪贴板恢复；另测目标应用管理员权限造成的输入限制提示。
5. 检查中文/英文、长设备名称、最小窗口、100%/125%/150%/200% 系统缩放与跨 DPI 显示器移动：
   换行、滚动、Tab 焦点和保存按钮均可用，此项尚未宣称真机通过。
6. 核对开机自启注册表与重新登录、界面语言重启；测试纠错地址/错误密钥/超时，确认失败保留
   原识别文本且日志无密钥。

## 17. 2026-10-02 审查分诊修复批次

依据 [TRIAGE_2026-10-02.md](TRIAGE_2026-10-02.md) 的工单实施，原始审查见
[REVIEW_2026-10-02.md](REVIEW_2026-10-02.md)。

### 17.1 当前不变量与所有权

- **加载占位**：`AsrService._inFlightLoad` 是 `TaskCompletionSource`，在工作开始前发布，清理按身份匹配。
  合并重载完成后调用方的 Task 才完成。
- **会话租约**：`MakeSession` 递增 `_activeSessions`，`LocalAsrSession.Close()`（幂等）恰好释放一次。
  租约大于 0 时不安排空闲计时；已入队的卸载闭包在 `AsrPump` 上执行前再检查一次。
  引擎仍只在 `AsrPump` 上 Dispose。
- **预览窗口**：有效值在每次 `MakeSession` 时现算（显式配置 > 校准结果 > 15 秒）；校准结果按
  `(模型文件, 线程数)` 缓存，写回发生在 UI 线程续体。
- **门禁事实源**：破坏性操作用 `VoiceTyperController.HasActiveDictation`，不用会被错误提示覆盖的显示状态。
- **剪贴板**：应用级唯一 `TextInsertionService`（协调器持有，控制器借用）；恢复闭包只处理仍属于自己的 pending。
- **采集**：`AudioCaptureService.CaptureContext` 拥有单次采集的 capture/device/缓冲/重采样器与事件订阅，
  回调只操作自己捕获的上下文；`_current` 之外的旧上下文由各自的 `RecordingStopped` 释放。
- **配置范围**：`ConfigLimits` 是设置页控件与 `AppConfig.Validated()` 共用的唯一来源。

### 17.2 工单状态

| 工单 | 代码 | 自动化测试 | 备注 |
| --- | --- | --- | --- |
| T1-0 / T1-1 / T1-2 / T2-2 | 完成 | 已编写（`AsrServiceLifecycleTests`） | 经 `IAsrEngine` 与注入的 locate/build/schedule 接缝 |
| T1-3 | 完成 | 控制器部分已编写 | 协调器门禁无测试 harness，靠代码审查 |
| T1-4 | 完成 | 未新增 | 构造顺序调整，没有原生资源计数手段 |
| T1-5 | 完成 | 已编写（`AsrPumpTests`） | 采集服务的同类修改靠代码审查 |
| T1-6 | 完成 | 判定函数已编写 | 真实剪贴板路径靠手工验证 |
| T1-7 | 完成 | 已编写（`AppLogRotationTests`） | |
| T2-1 / T2-7 | 完成 | 已编写 | |
| T2-3 | 完成 | 控制器事件已编写 | HUD 绘制资源与副标题靠手工验证 |
| T2-4 | 完成 | 无 | 协调器无 harness，靠代码审查 |
| T2-5 | 完成 | 已编写 | |
| T2-6 | 完成 | 无 | `WasapiCapture` 无法替身，靠手工验证 |
| T3-1 第一步 | 测试已写 | 未运行 | 见 17.4 |
| T3-2 | **未做** | — | 需要用户在真机采集数据，agent 不得自行判定 |

### 17.3 验证状态

| 项 | 状态 |
| --- | --- |
| 代码完成 | 是（T3-2 除外，按分诊要求先采数据） |
| `dotnet build` / `dotnet test` | **未验证**：本批次在没有 .NET SDK 的 Linux 环境完成，一次都没有编译或运行 |
| Windows x64 真机 | **未验证** |
| Windows arm64 真机 | **未验证** |

合并前必须先在带 .NET 10 SDK 的环境运行 `dotnet test`，并修正可能的编译或用例问题。

### 17.4 T3-1 重采样测量

`ResamplerStreamingTests` 用生产同一条链（`AudioCaptureService.BuildResamplingChain` /
`DrainResampled`）对 48k/44.1k、单/双声道、10ms 固定与 7–13ms 随机分块做流式与一次性输出的对比，
指标写入测试输出（样本数偏差、SNR、最大相邻跳变）。常规运行只断言样本数偏差 < 1%；设置
`VOICETYPER_RESAMPLER_STRICT=1` 才断言 SNR ≥ 40dB。**测量结果尚未产生**。判定规则：达标则不改实现并在此记录
结论；不达标才改为 `WdlResampler` 输入驱动。

### 17.5 手工验证清单（Windows 真机）

1. 删除模型目录后启动 → 自动下载 → 下载完成后不重启即可听写；断网使下载失败 → 恢复后手动重试 → 就绪。
2. 空闲卸载设为 1 分钟，等待卸载 → 按热键说约 90 秒 → 正常出字。
3. 录音中连点两次「重新加载模型」、保存设备设置、尝试录制热键：当前听写都应正常上屏，并看到拒绝原因。
4. 先复制文本 X → 三次间隔小于 1 秒的短听写 → 最终剪贴板为 X；复制 X → 听写 → 1 秒内保存热键设置 → 再听写 → 最终为 X。
5. Esc 取消后立即重按热键，连续 20 次均正常录音；录音中拔掉 USB 麦克风 → 本次结束并提示 → 插回后正常。
6. 配置不可达的 LLM 地址后听写：成功提示带「已使用识别原文（纠错未成功）」。

## 18. 2026-10-09 按热键到可开口的延迟

### 18.1 证据（Windows 10 真机日志，3.5.1）

用户反馈每次按热键都要等 2–3 秒浮窗才出现、才能说话，不只是第一次。日志里 `capture_start`（控制器开始处理
热键 → 麦克风打开）按距上一次录音的间隔明显分成两档：

| 距上次录音 | `capture_start` | 样本 |
| --- | --- | --- |
| ≤ 约 9 秒 | 412–541 ms | 00:31:41 → 463，00:31:44 → 447，00:25:27（间隔 9.3 秒）→ 518 |
| ≥ 约 15 秒 | 917–1178 ms | 23:41:04（间隔 15.5 秒）→ 1006，00:12:36 → 1178 |

`first_buffer − capture_start` 稳定在 13–17 ms：采集一开始就有数据，耗时全在打开之前。单独修饰键热键另有
150 ms 浮窗防闪烁延迟。日志能解释的是 0.6–1.35 秒；与用户感知的 2–3 秒之间的差距当时没有数据（钩子到控制器的
排队、浮窗真正画出来的时刻都没记录），因此本批次先补计时。

### 18.2 原因与改动

1. **打开麦克风在 UI 线程上同步执行**（`VoiceTyperController.BeginDictationSession` → `AudioCaptureService.Start`）：
   每次按键都新建枚举器、「自动」策略下枚举全部采集端点并读属性、再读默认播放端点，然后激活 IAudioClient、
   `StartRecording`（NAudio 在调用线程上做 `IAudioClient.Initialize`，已反编译 NAudio.Wasapi 2.2.1 核实）。
   浮窗要等这一切做完、`StateChanged(Recording)` 之后才出现。
2. **两档差距约 0.5–0.6 秒**，分界在间隔 9.3–15 秒之间。推断是声卡或驱动空闲约 10 秒后进入低功耗、
   再次打开时要唤醒。**未验证**；本批次不处理这一项（见 18.6）。
3. **UI 线程同时承载全局键盘 / 鼠标低级钩子**：阻塞期间整机键鼠事件都要等钩子返回，按住热键时的自动重复
   还会撞上 `LowLevelHooksTimeout`。日志里的「键盘钩子长时间无回调」是否与此有关**未验证**。

改动：

- **采集服务控制线程**：`AudioCaptureService` 新增专用 MTA 线程，解析设备、构造 `WasapiCapture`、
  `StartRecording`、`StopRecording`、释放全部在它上面串行执行。`IAudioCapturing.Start` 改为
  `BeginStart(policy, completed)`；完成前 `StopWithoutResult` 即撤销（代号 `_pendingStart` 在锁内清零，
  控制线程在打开设备后、置 `_running` 前、`StartRecording` 后三处检查）。`_lock` 不再在任何 WASAPI 调用期间持有。
  `MicPermissionProbe` 走同步包装 `Start`。
- **`RecordingStopped` 线程变化**：采集器在没有 `SynchronizationContext` 的控制线程上构造后，NAudio 在采集线程上
  直接触发 `RecordingStopped`（此前经 UI 线程同步上下文投递）。设备意外断开时的收尾依赖 `_current` 仍指向本
  上下文，因此改为「收尾 → 释放」在同一个 UI 投递里依次执行；释放本身排回控制线程。
- **控制器 Starting 阶段**：`Phase { Starting, Recording, Recognizing }`。按下即 `StateChanged(Recording)`；
  启动完成进入 Recording 并开始静音探测；采集已开始且收到第一段电平后触发 `MicrophoneReady`（两者从不同线程
  投递到 UI 线程，先后不定，任一顺序都只触发一次）。Starting 期间松键 / 切换模式再按 / Esc / 组合手势 / Stop
  都会撤销启动；松键时按住不足 0.3 秒照旧静默丢弃，超过则 `Outcome.NotReady` → `ReleasedBeforeReady`。
  迟到的启动结果按 `ReferenceEquals(_active, utterance) && Phase == Starting` 作废。
- **HUD**：新增 `Phase.Preparing`（「麦克风启动中…」、灰色静止圆点、平直暗波形、不计时）；`ShowRecording` 从
  Preparing 原地切换时保留锚点。协调器在 Recording 时显示 Preparing，在 `MicrophoneReady` 时切到「录音中」；
  单独修饰键的 150 ms 防闪烁计时从按下算起，到点时按是否已就绪选择显示哪一种。引导页的「录音中」也改为以
  `MicrophoneReady` 为准。
- **输入端点缓存**：`OpenInputDevice` 按策略缓存解析结果（端点 ID + 设备描述），命中时只剩 `GetDevice(id)`；
  `IMMNotificationClient` 的增删、启停用、默认设备变化递增拓扑版本，并合并成一次后台重新预解析
  （属性变化不处理：驱动会频繁上报，且不影响选哪个端点）。通知注册失败则不缓存；缓存端点打开失败或启动失败时
  作废。控制器 `Start` 时调用 `PrepareInput` 预解析。

### 18.3 新增耗时字段

均为毫秒，`-` 表示本次没有该值。除 `key_lag`、`dispatch` 与 `AudioStartTimings` 的几段外，「距按下」均以
控制器开始处理热键（`PressedAt`）为起点，与旧数据的 `capture_start`、`first_buffer` 可比。

| 字段 | 含义 |
| --- | --- |
| `key_lag` | 系统给按键事件打的时间戳（`KBDLLHOOKSTRUCT.time`）到钩子回调；UI 线程被阻塞时变大 |
| `dispatch` | 钩子回调到控制器处理热键（投递到 UI 消息队列的排队时间） |
| `start_queue` | 发起启动到控制线程开始处理（控制线程正在后台预解析时才不为 0） |
| `dev_resolve` | 选定并打开输入端点 |
| `dev_cached` | 1 = 端点命中缓存 |
| `activate` | 构造 `WasapiCapture`：激活 IAudioClient、读混音格式 |
| `init` | `StartRecording`：`IAudioClient.Initialize` 与采集线程启动 |
| `capture_start` | 距按下：麦克风启动完成（控制器在 UI 线程上收到结果） |
| `first_buffer` | 距按下：第一段音频到达（在音频线程上取时） |
| `hud_shown` | 距按下：浮窗第一次画到屏幕上（`OnPaint` 执行完） |
| `hud_ready` | 距按下：浮窗第一次画出「录音中」，即用户被告知可以开口 |

「录音启动」日志行同时给出 `queue/resolve(cached)/activate/init`，覆盖不进耗时摘要的麦克风探测。
用户感知的等待 ≈ `key_lag + dispatch + hud_ready`。

### 18.4 验证状态

| 项 | 状态 |
| --- | --- |
| macOS 主机交叉编译（win-x64） | 通过，0 警告 |
| `dotnet test`（macOS 主机） | 343 通过、1 跳过；5 项 `SetupFormTests` 因需要 WinForms STA 线程在 macOS 上无法运行，改动前同样失败 |
| NAudio 线程行为 | 反编译 NAudio.Wasapi 2.2.1 核实：构造时捕获 `SynchronizationContext.Current`；`StartRecording` 在调用线程上 `Initialize`；`StopRecording` 只置标记 |
| 非公开类实现 `IMMNotificationClient` 的 COM 回调 | **未验证**；失败时日志有「注册音频端点变化通知失败」，缓存自动停用 |
| Windows 真机 | **未验证** |

### 18.5 真机采集步骤

1. 安装本版本，确认设置里输入设备为「自动」。打开日志：
   `Get-Content "$env:APPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50`。
2. 启动后确认没有「注册音频端点变化通知失败」。
3. 两档各做 5 次以上：连续听写（间隔 < 5 秒）与间隔 > 20 秒的听写。每次说一句话即可。
4. 观感：按下后浮窗是否立刻出现「麦克风启动中…」，变红「录音中」后再开口，开头的字是否完整。
5. 按下后立刻松开（< 0.3 秒）：应无提示；按住约 0.5 秒、在变红之前松开：应提示「麦克风还没准备好」
   （若麦克风打开很快、来不及在变红前松开，这一项可跳过）。
6. 录音中拔插 USB / 蓝牙麦克风：本次结束并提示「输入设备已变化」，已录内容正常上屏；随后一次听写
   `dev_cached=0`，再下一次 `dev_cached=1`。
7. 按住右 Ctrl 时移动鼠标、在别的窗口打字：不应再出现卡顿。
8. 把 `[metrics]` 与「录音启动」两类日志行发回，用于判断 18.6 的下一步。

### 18.6 留待数据决定

- 若 `init` 仍占大头（预期如此，尤其是空闲后的那一档），下一步是 **预初始化 IAudioClient**：空闲时完成
  `Initialize` 但不 `Start`，按键时只 `Start`。需要先在真机确认已初始化未启动的流**不会**点亮任务栏麦克风图标、
  不会出现在「隐私 → 麦克风 → 正在使用」里，否则与「不做常驻监听」的定位冲突（macOS 同理否决过 pre-roll，
  见 `macos/DESIGN.md` §4.3）；还要确认它能否阻止空闲约 10 秒后的低功耗。
- 若 `dev_resolve` 在命中缓存后仍明显，检查 `GetDevice` 或 `device.State` 本身的耗时。
- 若 `key_lag` / `dispatch` 偏大，说明 UI 线程还有别的阻塞来源，需要再排查。

## 19. 2026-10-09 交互与性能修正后的遗留项

本批次的改动见 [CHANGELOG.md](CHANGELOG.md)。以下是同一轮代码审查中**没有做**的项目，及原因，供后续接手：

| 项 | 为什么没做 |
| --- | --- |
| 含 Alt / Win 的组合热键，主键被吞后系统看到孤立的 Alt / Win 松开，可能弹出菜单栏 / 开始菜单 | 推断出的问题，需要真机确认；修法是复用 §15.2 右 Alt 的哑键掩码，对 Alt / Win 修饰键都注入 |
| ORT 内存池（长听写 `ctc_logits` 约 200MB）用完不还给系统；Windows 11 对后台进程的 EcoQoS 降频 | 都要真机对比常驻内存与推理耗时（`preview_max`、`asr`）后再决定 |
| 剪贴板快照在 UI 线程上逐格式读取，延迟渲染的来源（Excel / Word）可能很慢 | 先看耗时摘要里 `insert` 的 p95 |
| 改任何设置都重建整个控制器（采集服务、热键钩子、HTTP 客户端） | 收益小（设置极少改）、改动面大；若要做，让采集 / 热键服务跟随应用生命周期 |
| 热键自检把鼠标输入也算作「有输入」，只用鼠标时每约 2.5 分钟误重装一次钩子 | 修法是用 Raw Input 区分键盘，需真机验证 |
| 浮窗字体按固定磅值、尺寸按显示器 DPI，混合 DPI 多屏下文字大小可能不一致；设置页配色 / 浮窗逐像素半透明（a9cb02f，已回退） | 需要多显示器真机；半透明渲染建议拆小再做 |
| 界面语言保存后无需重启即生效 | 托盘菜单、设置窗口文案都在构造时取，要重建；风险与收益不成比例 |
| 切换模式下目标窗口改在停止录音时记录 | 行为变化，需和 macOS 一起评估 |


## 20. 2026-10-09 Win11 真机「麦克风设备打开失败」排查

### 20.1 现象与根因

Win11 真机上安装后，设置页横幅报「麦克风设备打开失败：可能被其他应用独占，或驱动异常」，按热键只出现
HUD 外框、麦克风从未打开；其他应用录音正常。代码审查定位到两层问题：

1. **多声道端点被采集链路直接拒绝**（最可能的根因，待真机日志确认）：NAudio `WasapiCapture` 构造时用
   `IAudioClient.GetMixFormat` 的返回格式初始化共享模式流。Win11 麦克风阵列（Intel Smart Sound 等）的
   混音格式可能是 **4 声道**；`BuildResamplingChain` 此前对 >2 声道抛
   「暂不支持 N 声道的输入设备」。该异常的 Kind 是 `DeviceFailure`，被设置页映射成笼统的
   「可能被其他应用独占，或驱动异常」。其他应用正常，是因为它们显式请求单 / 立体声格式，由音频引擎
   自动转换——恰好构成「别的应用都好、就本应用坏」的表象。
2. **诊断信息在两层被丢弃**：`MicPermissionProbe` 把 `AudioStartException` 只按 Kind 归类，真实消息与
   HRESULT 既不落日志也不上界面；「被独占 / 驱动异常」是按分类的猜测文案，不是测得的事实。

（用户侧的两个表象均为预期行为：HUD 先进「录音中」再异步开麦克风，失败才收尾，所以会出现一个空外框；
Win32 桌面应用不出现在「隐私 → 麦克风」的应用列表里，也 没有"添加"按钮——该列表只列打包应用，桌面应用
只受全局「让桌面应用访问你的麦克风」开关约束。按 §MicProbeResult.Silent 的既定结论，隐私开关关闭的
表现为静音而非打开失败，可据此排除。）

### 20.2 修复

- `BuildResamplingChain`：新增 `MultichannelToMonoSampleProvider`（按帧取各声道平均，内部缓冲只增不减），
  3 声道及以上走它下混，不再拒绝设备。单测：`ResamplerStreamingTests`（3/4 声道纳入流式对比 +
  按帧平均的确定性用例）。**真实 4 声道设备的录音验证仍待真机**。
- `MicPermissionProbe`：返回 `MicProbeOutcome`（分类 + 失败详情），失败以 Error 级落完整异常
  （`permission` 类目）；`AudioCaptureService` 的通用失败包装拼入 `0x{HResult:X8}`。
- 设置页横幅与首启引导页：`DeviceFailure` 时把真实原因（HRESULT / 消息）拼在猜测文案后，便于对照
  `%APPDATA%\VoiceTyper\logs\app.log` 反馈。

### 20.3 手工验证步骤（真机，Win11 多声道阵列优先）

1. 设备管理器确认默认输入为 4 声道麦克风阵列（或在「声音设置 → 输入 → 格式」里能看到多声道格式），
   安装新包后打开设置页：横幅不应再出现「设备打开失败」；引导页麦克风检测应为「可用」。
2. 按热键说一句话：HUD 变红、出现波形，松开后正常插入文本；`app.log` 的「录音启动」行 format 应为
   `48kHz(或 44.1kHz) float 4ch` 一类，且链路按帧平均下混。
3. 若真机仍失败：横幅现在会带括号内的真实原因，同时 `%APPDATA%\VoiceTyper\logs\app.log` 有
   `permission: 麦克风探测失败（DeviceFailure）: …（…，0x…）` 完整一行——把它发回来即可定位
   （如 `0x88890008` 为设备被独占、`0x88890004` 为端点失效、`0x80070005` 为访问被拒）。

## 21. 2026-10-09 Win11 真机 HUD 空白透明（只剩顶部细线）

### 21.1 现象

§20 的麦克风问题修复后，同一台 Win11 真机上：按热键 HUD 只显示一个空白透明的框，窗口上侧有一条
细黑线，其余纯透明（背景、圆点、波形、文字全都不见了）；识别与文本上屏正常。Win10 机器上一切正常。

### 21.2 根因（高置信推断，待真机复验）

HUD 的整窗半透明由 WinForms `Form.Opacity`（默认 0.85）实现，即 `WS_EX_LAYERED` +
`SetLayeredWindowAttributes(LWA_ALPHA)`。`RecordingHud` 此前在句柄创建时无条件调用
`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)`——这是 HUD 渲染路径里
**唯一**的 Win11 / Win10 分叉：Win11 22000+ 上该调用成功并启用 DWM 圆角；Win10 上返回错误
HRESULT、静默回退 `SetWindowRgn` 圆角 Region。与「Win10 正常、Win11 客户区不合成」的表象吻合。

对 `SetLayeredWindowAttributes` 分层窗口设置角偏好，DWM 会按「自管理形状的应用窗口」处理该窗口
（合成路径改变，并补 1px 顶部 DWM 边框——即用户看到的细黑线）；分层窗口的客户区内容则不再被合成。
公开案例（Stack Overflow 79014559「Rounding the corners of a tooltip window with
DWM_WINDOW_CORNER_PREFERENCE …」、Microsoft Q&A 1283812）报告了同类现象，社区通行做法正是
对分层窗口显式 `DWMWCP_DONOTROUND` 或改由位图自带圆角。

### 21.3 修复（代码审查 + Win10 本机单测通过；Win11 真机待验证）

`RecordingHud.RefreshCornerStrategy()` 取代只在句柄创建时执行一次的 `TryEnableDwmRoundCorners`：

- **分层窗口（`Opacity < 1`，读实际 `GWL_EXSTYLE` 判定）**：不再设置 `DWMWCP_ROUND`，改为显式
  `DWMWCP_DONOTROUND` + `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`（消除 1px 顶线），圆角回到
  与 Win10 完全一致的 Region 裁剪 + 自绘 1px 边框路径。两个 DWM 调用失败无害（Win10 上必然失败）。
- **不透明窗口（`Opacity = 1`，非分层）**：仍优先 DWM 圆角（平滑无锯齿），失败回退 Region。
- 透明度可在设置中实时调整，分层状态随之切换，因此 `ApplyConfig` / `ApplyOpacity` 也会重跑策略，
  并保证「DWM 圆角与 Region 互斥」的不变式（双向切换都会清理另一侧）。

单测 `HudCornerStrategyTests`（STA）：分层窗口必须走 Region；不透明窗口两者互斥；
透明度来回切换时策略正确换边。本机 Win10 全量 384 项：382 过 / 2 跳过 / 0 失败。

### 21.4 手工验证步骤（真机 Win11）

1. 安装新包，按热键：HUD 应立刻显示深灰半透明圆角框 + 圆点 / 波形 / 状态文字，识别中预览文本展开，
   不再出现「空白透明 + 顶部细线」。与 Win10 机器的观感应一致（差别仅剩圆角边缘为 Region 硬边）。
2. 设置 → 外观把浮窗不透明度调到 100%：HUD 变为完全不透明，圆角边缘应更平滑（DWM 圆角路径）；
   再调回 85%：恢复半透明且内容仍正常显示（验证切换路径）。
3. 若真机仍空白透明：说明该机的 DWM 对 `SetLayeredWindowAttributes` 分层窗口本身有更深的问题
   （与本修复的角偏好路径无关），下一步改用 `UpdateLayeredWindow` 逐像素 alpha 方案
   （a9cb02f 有可参考的实现，当时因改动面大被整体回退，见 §19 遗留项表）。
