# VoiceTyper Windows 一体化应用 · 设计方案

> 目标产物：`windows/` 下一个**前后端一体**的 Windows 桌面应用，安装即用，不需要单独跑 Python 服务端。
> 它以 `client-server/client_windows_native/` 为蓝本，把 `macos/` 已经验证过的 SenseVoice 推理链路从 Swift 直译为 C#。
>
> 本文只覆盖 Windows。`macos/`、`client-server/client_linux/`、`client-server/server/` 保持现状不动。
>
> **本文档的证据分级**：标注「✅ 实测」的结论是在本次调研中真实验证过的（NuGet 包内容、
> ModelScope 端点、托管 API 表面、金标准夹具）；标注「⚠️ 估算」的是从 macOS/M4 实测数据外推的，
> **必须在 P0 阶段用真实 Windows 硬件复测**——本次调研环境是 macOS，无 .NET SDK、无 Windows 机器，
> 无法编译也无法跑分。凡是与性能、内存、产物体积相关的数字，都属于后者。

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
| `windows/`（新，主分发版本） | **`VoiceTyper`** | **`%APPDATA%\VoiceTyper\`** | **3.0.0**（与 `macos/` 对齐，见 D3） |

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
| WinUI 3 / Windows App SDK | ❌ | 额外的 SDK 运行时依赖；托盘图标与全局悬浮窗支持一直是弱项；打包模型（MSIX）与"下载 230MB 模型到用户目录"的诉求相互别扭 |
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
任务管理器就在那儿。除了沿用「空闲 N 分钟卸载」（Windows 默认建议 **5 分钟**，比 macOS 的 10 更激进，
因为 Windows 用户对常驻内存更敏感），再加一个 Windows 原生动作：
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
├── Resources/correction.md            # 从 client-server/server/voice_typer_server/prompts/ 搬运（与 macOS 同一份）
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
│                   SetupForm{,.Recognition,.Hotkey,.General} (partial class 拆页)
├── Support/        Constants, AppLog, NativeMethods, UiDispatcher, StartupRegistration
└── Tests/VoiceTyper.Tests/            # xUnit
        FbankParityTests, TextPostprocessorTests, RecognitionBufferTests,
        ConfigStoreTests, LlmCorrectorTests, ModelDownloaderTests,
        EndToEndRecognitionTests
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
> 会随用户配置文件漫游，往里塞 230MB 模型会让登录变成灾难。配置（几 KB）漫游是**对的**——
> 换台机器热键设置跟着走；模型（230MB）漫游是**错的**。这是 Windows 特有的正确做法，
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
持有 `AsrPump`。**空闲卸载**：`asr.idle_unload_minutes`（Windows 默认 5，0=永不），
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

其余要点与 macOS 一致：4 个文件串行、**先下三个小文件再下 onnx**（网络问题在花掉 230MB 前暴露）、
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
3 组固定 few-shot、`<asr_text>` 标签包裹、`maxTokens = max(configured, len*2+128)`、
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

**新 schema**（与 macOS 同构，仅 `preview_window` 与默认 `idle_unload_minutes` 不同）：

```yaml
asr:
  language: "auto"          # auto / zh / en / yue / ja / ko
  threads: 0                # 0 = 自动（min(4, 核数)）
  model_dir: ""             # 留空 = 按 ModelLocator 优先级自动定位
  preview_window: 0         # 秒；0 = 首次加载后按实测 RTF 自动校准（§4.5）
  idle_unload_minutes: 5    # 0 = 常驻不卸载
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
│ 打开配置目录
├──────────────────────
│ ✅ 开机自启                          ← 新增
│ 关于 VoiceTyper                      ← 新增
├──────────────────────
│ 退出
```

第三行从「服务：已连接 127.0.0.1:6008」改为「引擎：已就绪 / 模型加载中… / 下载模型 42%」。
下载中时托盘图标叠加进度环（`TrayController` 已有 `Graphics` 绘图代码可复用）。

**设置窗口**：Tab 从 2 个（连接/热键）变为 **4 个**

| Tab | 内容 |
| --- | --- |
| 识别 | ① **模型卡片**：未就绪时「需要下载语音模型 · 230 MB」+「开始下载」+ 进度条（已下/总量、速度）+「取消」；就绪时「SenseVoice-Small · int8 · 已就绪」+ 路径 +「重新加载」<br>② 识别语言 下拉<br>③ 智能纠错：开关 + Base URL + API Key + 模型 + 温度 + 超时 +「测试纠错」 |
| 热键 | **原样保留** |
| 权限 | 麦克风可用性检测 +「打开 Windows 隐私设置」（`ms-settings:privacy-microphone`）+ UIPI 提权限制说明 |
| 通用 | 开机自启、HUD 不透明度、空闲 N 分钟后卸载模型、预览窗口（进阶） |

`SetupForm.cs` 现在 550 行单文件，4 个 Tab 会撑到 900+。拆成 `partial class`
按 Tab 分文件（`SetupForm.Recognition.cs` 等），保持每个文件可读。

**麦克风权限探测**（`MicPermissionProbe`）：Windows 对非打包桌面应用的麦克风管控在
设置 → 隐私和安全性 → 麦克风 → "让桌面应用访问你的麦克风"。程序侧只能通过
**实际尝试打开设备**来判断（现有 `AudioCaptureService` 已经在捕获 `COMException 0x80070005`
并区分 `IsAccessDenied`）。启动时做一次极短的静默打开-关闭探测即可，无需新的 API。

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

| 产物 | 形态 | ⚠️ 体积估算 | 说明 |
| --- | --- | ---: | --- |
| `VoiceTyper-<ver>-win-x64-setup.exe` | Inno Setup 安装包（**主推**） | ~35 MB | 装到 `%LOCALAPPDATA%\Programs\VoiceTyper`，**per-user，不弹 UAC**；开始菜单快捷方式；可选开机自启 |
| `VoiceTyper-<ver>-win-x64-portable.zip` | 目录式自包含 | ~60 MB | 解压即用，不需要 .NET 运行时 |
| `VoiceTyper-<ver>-win-arm64-setup.exe` | 同上，arm64 | ~35 MB | Snapdragon X 笔记本 |
| `VoiceTyper-<ver>-win-arm64-portable.zip` | 同上，arm64 | ~60 MB | |

**关键决策：放弃 `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`。**

现有 `client-server/client_windows_native/build.bat` 用的就是这套。加进 14MB 的 `onnxruntime.dll` 之后它变成负担：
自解压模式会在每个新版本首次启动时把原生库解压到 `%TEMP%\.net\...`，
对一个**开机自启的常驻工具**来说，这既拖慢启动，又是杀毒软件误报的高发路径。
改为**目录式部署 + 安装包**：Inno Setup 生成的安装器同样是"双击就装完"的体验，
但文件老实躺在磁盘上，启动零解压、AV 友好、增量更新也更容易做。

其余构建要点：
- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false`
- **不开 `PublishTrimmed`**：WinForms 大量依赖反射，裁剪风险远大于省下的几十 MB
- `Version` 从 csproj 单点读取，安装包版本号与之同步
- **代码签名**：本轮不做。SmartScreen 会对未签名安装包显示"Windows 已保护你的电脑"，
  README 需给出"更多信息 → 仍要运行"的截图说明。与 macOS 不做公证是同一个取舍位。

`scripts/fetch_model.ps1`：开发与测试辅助，用同一组 URL 和 sha256 把模型拉到
`%LOCALAPPDATA%\VoiceTyper\models\`，供跑金标准测试或跳过首启引导用。

---

## 8. 测试策略

新增 `windows/Tests/VoiceTyper.Tests/`（xUnit）。现有 Windows 客户端**没有任何测试**，这次补上。

| 测试 | 内容 | 通过标准 | 需要模型？ |
| --- | --- | --- | --- |
| **FbankParityTests** | 链接引用 `macos/Tests/VoiceTyperTests/Fixtures/fbank_input.f32` → 比对 `fbank_reference.f32` | 帧数**完全相同**；`max‖Δ‖∞ < 1e-3` | **否** ✅ |
| **LfrCmvnParityTests** | 同上比对 `lfrcmvn_reference.f32` | 同上 | 是（需 `am.mvn`），缺失则跳过 |
| **TextPostprocessorTests** | `<\|...\|>` 剥离、中英间距、纯标点丢弃、`▁` 还原 | 覆盖每条规则；用例与 macOS 测试同源 | 否 |
| **RecognitionBufferTests** | 假引擎驱动滑窗：窗口滚动、切点能量最小、`Finalize` 走完整音频 | 预览单调增长不回退为空；finalize 输入长度 == 总样本数 | 否 |
| **ConfigStoreTests** | YAML 读写往返、缺字段回落默认、`voice_typer` 目录迁移 | 往返幂等；迁移只带走 hotkey/opacity | 否 |
| **LlmCorrectorTests** | `HttpMessageHandler` 打桩：正常 / `finish_reason=length` / 5xx / 超时 | 后三者均返回**原文** | 否 |
| **ModelDownloaderTests** | `HttpMessageHandler` 打桩：正常、中断后 Range 续传（206）、sha256 不匹配、磁盘满 | 续传不重下已完成部分；校验失败只留 `.part` | 否 |
| **EndToEndRecognitionTests** | 完整链路跑 `speech_zh_en_mixed.wav` | 与 `.reference.txt` 编辑距离 ≤ 2 | 是（模型 + wav），缺失则跳过 |
| **FftTests** | 自写 FFT vs 朴素 DFT（512 点，随机输入） | `max‖Δ‖∞ < 1e-4` | 否 |

**七个测试里有六个不需要模型、不需要 Python、不需要网络**——可以直接跑在
GitHub Actions 的 `windows-latest` 上。这是复用 macOS 已入库夹具带来的直接收益。

> **一个建议的仓库改动**：`.gitignore` 的 `*.wav` 规则让
> `macos/Tests/VoiceTyperTests/Fixtures/speech_zh_en_mixed.wav`（974 KB）没有入库，
> 导致两个平台的 E2E 测试都不可复现。建议加一条否定规则
> `!macos/Tests/VoiceTyperTests/Fixtures/*.wav` 把它收进来。974 KB 换两个平台的
> 端到端可复现性，很划算。

**P1 的推荐做法（沿用 macOS 的经验）**：先跑 `FbankParityTests`，让 C# 去追平已有夹具，
再写后面的东西。每一步都有明确的数值反馈，不靠"看起来对"。

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
| **P2 引擎** | `Rfft` + `FbankFrontend` + `LfrCmvn` + `SenseVoiceEngine` + `CtcDecoder` + `TextPostprocessor`；链接 macOS 夹具 | **FftTests / FbankParityTests / LfrCmvnParityTests 全绿**（G2 达成，最关键的一步） |
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
| **常驻内存 ~500MB** | 任务管理器里显眼，用户投诉 | `disable_prepacking`（macOS 实测省 290MB 零代价）+ 空闲卸载默认 5 分钟 + 卸载后 `SetProcessWorkingSetSize` 归还工作集 |
| **首启下载失败**（断网、ModelScope 抽风、磁盘满） | 新用户第一印象直接卡死 | Range 续传（✅ 本次实测 206 可用）+ sha256 校验（✅ 三个小文件本次实测全对）+ 小文件先行；失败文案给手动放置路径与 `fetch_model.ps1`；`ModelLocator` 复用 `~/.cache/modelscope/` |
| **未签名安装包被 SmartScreen 拦** | 新用户装不上 | README 图文说明"更多信息 → 仍要运行"；长期解法是买 EV 证书（与 macOS 公证同一个决策位，本轮都不做） |
| **UIPI：无法向提权窗口插入文本** | 在管理员终端里听写静默失败 | 检测目标窗口提权并给明确提示，而不是让用户以为识别坏了；README 记为已知限制 |
| **AV 误报**（全局键盘钩子 + 剪贴板 + SendInput + 下载大文件） | 用户被吓退 | 目录式部署代替自解压单文件（减少一大类启发式命中）；README 说明各权限用途；必要时向主流 AV 提交白名单 |
| **ORT NuGet 版本漂移** | 构建不可复现 | csproj 钉死 `1.24.2`；`packages.lock.json` 入库（`RestorePackagesWithLockFile=true`） |
| **arm64 变体缺乏验证** | Snapdragon 机器上出问题无人知 | ORT 官方提供 win-arm64 原生库（✅ 实测存在）；若手上无 arm64 设备，README 标注"arm64 构建未经实机验证"，不假装它测过 |
| **无法在本机验证任何 Windows 行为** | 方案里的估算可能全错 | 已在文档顶部与各处显式标注 ✅/⚠️；P0 探针阶段就是为消除这一整类不确定性而存在的 |
| 两个 App 同时安装 | 热键互抢、双份内存 | 配置目录已隔离（`VoiceTyper` vs `voice_typer`）；安装包检测到旧版时提示卸载 |

---

## 12. 决策记录

| # | 决策 | 结论 | 说明 |
| --- | --- | --- | --- |
| D1 | UI 栈 | ✅ **WinForms**（不是 WPF / WinUI 3） | 复用 1,022 行经过实战的 UI；"不抢焦点悬浮窗"已调通，不值得为观感重来一遍 |
| D2 | .NET 版本 | ✅ **net10.0-windows** | .NET 8/9 于 2026-11-10 EOL；ORT 托管包提供 net8.0 资产，兼容无碍 |
| D3 | 新 App 版本号 | ✅ **3.0.0**，与 `macos/` 对齐 | 老客户端改名为 `VoiceTyperClient` 后，其 3.0.0 归属另一条产品线，不冲突。备选是 4.0.0（更无歧义但与 macOS 脱节） |
| D4 | ORT 版本 | ✅ **钉 1.24.2**，与 `macos/` 一致 | 消掉"不同 ORT 版本导致 argmax 翻转"这一整类变量；升级另开一次带金标准复测的变更 |
| D5 | 执行提供者 | ✅ **仅 CPU EP** | DirectML 对动态 int8 支持差；Windows ML 要 Win11 24H2 且与直译策略冲突（记为 P2） |
| D6 | FFT 实现 | ✅ **自写 512 点 radix-2**，不引数学库 | 固定尺寸、算法确定、有金标准兜底；比 macOS 的 vDSP 打包方案更简单 |
| D7 | 目录布局 | ✅ **配置 `%APPDATA%\VoiceTyper\`，模型 `%LOCALAPPDATA%\VoiceTyper\models\`** | 配置（KB 级）该漫游，模型（230MB）绝不能漫游。这是 Windows 特有的正确做法，不能照抄 macOS 单目录方案 |
| D8 | 密钥存储 | ✅ **DPAPI 文件** | 40 行 vs 凭据管理器的 120 行互操作，底层同样是 DPAPI，安全性无差别 |
| D9 | 分发形态 | ✅ **目录式 + Inno Setup**，放弃单文件自解压 | 常驻自启工具不应每次新版本首启解压 14MB 原生库；AV 也更友好 |
| D10 | 架构覆盖 | ✅ **x64 + arm64** | ORT 两个 RID 原生库齐备，没有 macOS 侧"放弃 Intel"那种被迫取舍 |
| D11 | 文本插入 | ✅ **维持剪贴板 + SendInput** | Windows 没有 macOS AX 直写的可靠对等物（UIA `TextPattern` 只读、`ValuePattern` 整体替换语义错误） |
| D12 | Windows 版本下限 | 待定，暂按 **Win10 1809+** | 若接受只支持 Win11 24H2+，Windows ML/NPU 路线才成立。取决于目标用户构成，P0 前定即可 |
| D13 | 空闲卸载默认值 | ~~待定，暂按 5 分钟~~ → **10 分钟**（3.1.0 起，见 §13） | 与 macOS 默认值一致；此前的分歧没有实测支撑的理由，直接统一 |

---

## 13. 与 macOS 3.1.9 的行为对齐（3.1.0）

089c2a6 一次性落地本文档描述的 P0 方案后，Windows 侧再未随 macOS 的 R2/R3/R4 三轮架构评审与
三轮首启/UI 体验优化同步演进，直到本次对齐（详见仓库根目录
[`windows/ALIGNMENT_WITH_MACOS.md`](ALIGNMENT_WITH_MACOS.md) 逐条盘点的 35 项差异）。本节记录
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

后两项（`preview_window` 自校准、`.part` 续传）是 Windows 优于 macOS 的设计，建议后续单独立项反向
移植回 macOS，不与本次对齐混在一起。

### 13.2 已代码审查、尚未真机验证

以下改动逻辑上正确（对照 macOS 实现逐行核对），但本轮对齐完全在没有 .NET SDK、没有 Windows
机器的环境下完成，**没有编译、没有运行过**。合入后第一件事必须是在真机上把 `dotnet build` /
`dotnet test` 跑绿，其中这几项需要额外的手工验证（见 `windows/README.md`「日志与排障」与
`ALIGNMENT_WITH_MACOS.md` §10 手工验证清单）：

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
