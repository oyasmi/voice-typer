# VoiceTyper macOS 一体化应用 · 设计方案

> 目标产物：`macos/` 下一个**前后端一体**的 macOS 应用，拖进 `Applications` 打开即用，不需要单独跑 Python 服务端。
> 它以 `client-server/client_macos_swift/` 为蓝本，把 `client-server/server/` 的 SenseVoice 推理链路用 Swift 重写并内联进来。
>
> 本文只覆盖 macOS。Windows 现已有独立的一体化实现（`windows/`，见其 DESIGN.md）；
> Linux 一体化版本尚未实现，`client-server/`（含旧 Windows/Linux 客户端与服务端）
> 保留用于 Linux、远程部署和共享服务端场景。

---

## 1. 目标与非目标

### 1.1 目标

| # | 目标 | 验收标准 |
| --- | --- | --- |
| G1 | 单进程、零外部依赖 | 全新 Mac 上拖入 Applications → 打开 → 授权三项权限（App 同时在后台自动下载并部署模型）→ 按热键即可听写。全程不接触终端、不装 Python，此后完全离线 |
| G2 | 识别质量与现有链路一致 | 同一段音频，Swift 特征提取（fbank/LFR/CMVN）与 `client-server/server/` 输出逐点误差 < 1e-3；完整识别文本与 Python 输出编辑距离 ≤ 2（金标准测试，详见 §8 的实测结论） |
| G3 | 延迟不劣化 | 松手到上屏 ≤ 现有本地 server 方案（去掉了 WS 往返，理论上更快） |
| G4 | 配置面收敛 | 用户可见配置项从 12 项降到 6 项左右，且没有一项与"服务端在哪"有关 |
| G5 | LLM 校对开箱可配 | 在设置面板里填 base_url / api_key / model 即可启用，无需改服务端启动参数 |

### 1.2 非目标（本轮明确不做）

- 不支持 paraformer / ct-punc / 流式 paraformer——**只留 SenseVoice-Small**。
- 不做 API Key 鉴权（进程内直调，没有攻击面）。
- 不做 GPU / CUDA / 远程服务端连接。
- 不改动 `client-server/server/`、`client-server/client_windows_native/`、`client-server/client_linux/` 的行为（仅同步文档表述）。
- 不做自动更新（Sparkle）、不做公证（notarization），沿用现有 adhoc 签名分发方式。

### 1.3 命名与版本

| 项目 | App 名 | Bundle ID | 版本 |
| --- | --- | --- | --- |
| `client-server/client_macos_swift/`（现有，降级为次要） | `VoiceTyper` → **`VoiceTyperClient`** | `com.voicetyper.app` → **`com.voicetyper.client`** | 2.7.0（**不变**） |
| `macos/`（新，主分发版本） | **`VoiceTyper`** | **`com.voicetyper.app`** | **3.3.0**（当前版本，见 D3） |

> Bundle ID 必须区分：TCC 权限、`SMAppService` 开机自启、LaunchServices 都以 Bundle ID 为键。
> 代价：老客户端用户升级到改名版后需要**重新授权一次**三项权限。由于两个 App 都是 adhoc 签名、
> cdhash 必然变化，无论是否改 ID 都要重新授权，所以这个代价是"本来就要付"的。

---

## 2. 现状盘点

### 2.1 客户端侧（`client-server/client_macos_swift/`，~3.5k 行 Swift）

| 模块 | 处理 |
| --- | --- |
| `HotkeyService` / `AudioCaptureService` / `TextInsertionService` | **原样复制**，零改动 |
| `RecordingHUDController` / `StatusMenuHeaderView` / `HotkeyRecorderField` | 原样复制，仅改文案 |
| `PermissionCenter` / `LaunchAtLogin` / `Logger` | 原样复制 |
| `VoiceTyperController` | **保留状态机，替换网络层**（详见 §5.3） |
| `AppCoordinator` | 删掉服务端轮询/重连，改为模型加载编排 |
| `StreamingASRClient` / `ASRClient` / `ServerHealthProbe` | **删除**，由本地 `LocalASRSession` 顶替 |
| `AppConfig.ServerConfig` | **删除**，改为 `ASRConfig` + `LLMConfig` |
| `SetupWindowController` / `ConnectionSettingsView` | 「连接」页删除，替换为「识别」页 |

### 2.2 服务端侧（`client-server/server/`，需要用 Swift 重写的部分）

只有下面这条链路需要移植，其余（tornado、auth、CLI、Windows 服务、paraformer、ct-punc）全部丢弃：

```
recognizer.SenseVoiceRecognizer.initialize   → 读 config.yaml / am.mvn / tokens.json，建 ORT session
                     ├ WavFrontend.fbank      → kaldi 兼容 fbank（80 mel）
                     ├ WavFrontend.lfr_cmvn   → LFR(m=7,n=6) + CMVN
                     ├ session([...])         → ONNX 推理
                     └ _ctc_greedy_decode      → CTC 贪心解码 + 文本后处理
recognizer.SenseVoiceSession                 → 滑动窗口预览 + finalize 整段重跑
app.StreamRecognizeHandler                   → 预览调度、合并、warning/final 语义
llm_client.LLMClient                         → OpenAI 兼容校对
prompts/correction.md                        → 提示词（原样搬运为 bundle 资源）
```

移植总量估算：**~900 行 Swift**（前端 350 / 引擎 200 / 解码 120 / 会话 150 / LLM 100）。

---

## 3. 总体架构

```
┌──────────────────────────── VoiceTyper.app（单进程） ────────────────────────────┐
│                                                                                  │
│  ┌─── MainActor ────────────────────────────────────────────────────┐            │
│  │  AppCoordinator                                                   │            │
│  │    ├ StatusBarController / RecordingHUDController / SetupWindow    │            │
│  │    ├ PermissionCenter                                             │            │
│  │    └ VoiceTyperController  ← 状态机 Idle→Recording→Recognizing→Inserting        │
│  │          ├ HotkeyService (CGEventTap)                             │            │
│  │          ├ AudioCaptureService (AVAudioEngine, 16k/f32/mono)      │            │
│  │          ├ TextInsertionService (AX / 剪贴板)                      │            │
│  │          └ LocalASRSession ──────┐  ← 接口与旧 StreamingASRClient 一致          │
│  └───────────────────────────────────┼──────────────────────────────┘            │
│                                      │ onPartial / onFinal / onWarning / onError  │
│  ┌─── asrQueue（串行 DispatchQueue，QoS .userInitiated）──────────────┐            │
│  │  ASRService                                                       │            │
│  │    └ SenseVoiceEngine                                             │            │
│  │         ├ FbankFrontend   (Accelerate/vDSP)                       │            │
│  │         ├ LFRCMVN                                                 │            │
│  │         ├ ORTSession      (onnxruntime.framework, CPU EP)         │            │
│  │         └ CTCDecoder + TextPostprocessor                          │            │
│  └───────────────────────────────────────────────────────────────────┘            │
│                                      │ 最终文本                                    │
│  ┌─── 后台 Task ─────────────────────┴──────────────────────────────┐            │
│  │  LLMCorrector (URLSession → OpenAI 兼容 /chat/completions)        │            │
│  └───────────────────────────────────────────────────────────────────┘            │
│                                                                                  │
│  Resources/Models/sensevoice-small/{model_quant.onnx, am.mvn, config.yaml,       │
│                                     tokens.json}                                 │
└──────────────────────────────────────────────────────────────────────────────────┘
```

**核心设计原则：不发明新的状态机。** `VoiceTyperController` 保持原样，只是它手里的
`StreamingASRClient`（WebSocket）换成了 `LocalASRSession`（本地引擎），两者**回调签名完全一致**。
这样 §2.1 里 3.5k 行客户端代码有 ~85% 是零改动搬运，改动集中在一层薄接缝上。

---

## 4. 关键技术选型（含实测数据）

以下数据均在本机（**Apple M4，4 性能核**）实测，模型为 `iic/SenseVoiceSmall-onnx` 的
`model_quant.onnx`（int8，240MB），`intra_op_num_threads=4`。

### 4.1 ONNX Runtime 集成 —— 选 `onnxruntime-swift-package-manager`

已验证事实：

- 包地址 `https://github.com/microsoft/onnxruntime-swift-package-manager`，`Package.swift` 声明
  `platforms: [.iOS(.v15), .macOS(.v14)]`，与本项目现有的 `MACOSX_DEPLOYMENT_TARGET = 14.0` 吻合。
- 产品 `onnxruntime` 指向 target `OnnxRuntimeBindings`，即 **Objective-C API**（`ORTEnv` /
  `ORTSession` / `ORTValue` / `ORTSessionOptions`），Swift 直接 `import OnnxRuntimeBindings` 即可，
  不必手搓 C API 的函数指针包装。
- 二进制 target 是 `pod-archive-onnxruntime-c-1.24.2.zip`。已下载解包确认其中
  `onnxruntime.xcframework` 含 **`macos-arm64_x86_64`** 切片（arm64 + x86_64 通用），
  框架二进制 90MB（通用，单架构约 45MB），并带 `coreml_provider_factory.h`（CoreML EP 可用，留作后续）。
- API 覆盖度足够：`ORTValue(tensorData:elementType:shape:)` 支持 `Float` / `Int32`；
  `ORTSession.run(withInputs:outputNames:runOptions:)` 返回命名输出；
  `ORTSessionOptions.setIntraOpNumThreads` / `addConfigEntryWithKey:` 齐全。

被否决的备选：

| 方案 | 否决理由 |
| --- | --- |
| 直接引 GitHub Release 的 `onnxruntime-osx-*.tgz` | 1.29.0 起官方 Release **只提供 `osx-arm64`**，没有 universal2；且需自己管理 dylib 嵌入与签名 |
| `sherpa-onnx` | 能直接吃 SenseVoice，省掉 fbank 移植；但引入一整套 C++ 构建（CMake/xcframework 自制）、且输出后处理与现有服务端不一致，G2（逐字一致）反而更难保证 |
| CoreML 转换 | 转换链路长、int8 量化模型在 ANE 上收益不确定，属于 P2 优化而非首版方案 |

**模型 I/O 契约（已实测确认）：**

```
IN   speech          float32  [batch, feats_length, 560]
IN   speech_lengths  int32    [batch]
IN   language        int32    [batch]      auto=0 zh=3 en=4 yue=7 ja=11 ko=12
IN   textnorm        int32    [batch]      withitn=14  woitn=15
OUT  ctc_logits      float32  [batch, logits_length, 25055]
OUT  encoder_out_lens int32   [batch]      有效帧数，用于切片 ctc_logits
```

### 4.2 特征前端 —— 用 Swift + Accelerate 重写 kaldi fbank

服务端走的是 `funasr_onnx.utils.frontend.WavFrontend`，底层是 `kaldi_native_fbank`（knf 1.22.3）。
已实测导出其生效参数：

```
frame_opts: samp_freq=16000  frame_length_ms=25  frame_shift_ms=10
            dither=0（服务端显式置 0）  preemph_coeff=0.97  remove_dc_offset=true
            window_type=hamming  round_to_power_of_two=true  snip_edges=true
mel_opts:   num_bins=80  low_freq=20  high_freq=0(→nyquist)  is_librosa=0
fbank_opts: use_energy=false  energy_floor=0  use_log_fbank=true  use_power=true
```

`is_librosa=0` 已核对 knf 源码 → 走 `InitKaldiMelBanks`，即**经典 Kaldi HTK mel 尺度、三角滤波器、不做 slaney 归一化**。

**Swift 侧实现规格**（照此实现即可，无歧义）：

1. `x *= 32768.0`（knf 的 `waveform * (1 << 15)`）
2. 分帧：`frameLength=400`，`frameShift=160`，`snip_edges` ⇒ `numFrames = N < 400 ? 0 : (N-400)/160 + 1`
3. 每帧依次：
   a. 去直流：减去该帧均值
   b. 预加重：`for i in stride(from: n-1, through: 1, by: -1) { x[i] -= 0.97*x[i-1] }; x[0] -= 0.97*x[0]`
   c. 加窗：hamming，`w[i] = 0.54 - 0.46*cos(2πi/(n-1))`
4. 补零到 512（`round_to_power_of_two`），实数 FFT（`vDSP_DFT` / `vDSP_fft_zrip`），取功率谱 `|X[k]|²`，k=0…256
5. Mel 滤波：80 bin，`mel(f) = 1127*ln(1+f/700)`，`low=20Hz`，`high=8000Hz`，三角权重，不归一化
6. `log(max(energy, .ulpOfOne))`
7. 输出 `[T, 80]` float32

随后：

- **LFR**（`lfr_m=7, lfr_n=6`）：左侧用第一帧复制 `(7-1)/2 = 3` 份；`T_lfr = ceil(T/6)`；
  尾帧不足时用最后一帧补齐 → `[T_lfr, 560]`
- **CMVN**：解析 `am.mvn`，取 `<AddShift>` 后 `<LearnRateCoef>` 行的 560 个 means，
  `<Rescale>` 后的 560 个 vars；`out = (in + mean) * var`
- 硬下限：样本数 < 400 直接返回空文本（对应服务端 `_MIN_SAMPLES`，防止 fbank 出 0 帧后崩）

**为什么不 vendoring kaldi-native-fbank 的 C++ 源码？**
它是 Apache-2.0 的独立小库，直接嵌进 Xcode target 也可行，且能天然保证数值一致。但它带来
C++ 互操作、通用二进制构建、上游同步三重成本，而 fbank 本身算法确定、规格已完全导出，
且我们有**逐帧金标准测试**兜底（§8）。因此选纯 Swift 实现。
**风险对策**：如果金标准测试在合理努力内无法收敛到阈值内，退路是 vendoring knf 的
`feature-fbank / feature-window / mel-computations / rfft / online-feature` 五个文件——
这是一条明确的、可在半天内切换的退路，不构成阻塞风险。

### 4.3 实测性能与内存（M4）

| 指标 | 数值 |
| --- | --- |
| 模型加载 | **0.85 ~ 0.98 s** |
| 1s 音频识别 | 21 ms（RTF 0.021） |
| 5s 音频识别 | 61 ms（RTF 0.012） |
| 15s 音频识别（= 预览窗口满载） | **166 ms** |
| 30s 音频识别（finalize） | **262 ~ 397 ms** |

**内存是这次调研最重要的发现：**

| 配置 | 常驻内存（RSS，扣除 Python 基线约 45MB） |
| --- | --- |
| 默认 | **~800 MB** |
| `enable_cpu_mem_arena=false` | ~800 MB（无改善） |
| **`session.disable_prepacking=1`** | **~510 MB**，且 30s 推理耗时 262ms vs 260ms —— **零性能代价** |

⇒ **设计决策**：`ORTSessionOptions.addConfigEntry(key: "session.disable_prepacking", value: "1")`
必须打开。这一行省 290MB 常驻内存，对一个常驻菜单栏 App 是决定性的。

**后续复核（第二轮架构评审，R4）**：曾提议再测 `disable_mem_pattern` 这项独立于 arena 的优化
（内存模式优化本是为固定 shape 设计的，本项目每次推理输入长度都不同，理论上可能有额外收益）。
实测确认**这条路走不通**：用 `strings` 检出 `onnxruntime-swift-package-manager` 1.24.2 macOS
xcframework 二进制里全部 `session.*` 前缀的 `AddSessionConfigEntry` 识别键，`disable_mem_pattern`
不在其中——它在 ORT 的 C/C++ API 里是 `OrtSessionOptions` 上的独立方法
（`DisableMemPattern()`），不是字符串 config-entry，而 `ORTSessionOptions`（ObjC 绑定，见
`ort_session.h`）只包了 `setIntraOpNumThreads`/`setGraphOptimizationLevel`/
`setOptimizedModelFilePath`/`setLogID`/`setLogSeverityLevel`/`addConfigEntryWithKey` 六个方法，
没有暴露它。二进制里能找到 `arena_extend_strategy`/`enable_cpu_mem_arena` 字符串，但那对应的
是通过 `OrtArenaCfg` + `SessionOptionsAppendExecutionProvider_CPU` 配置 CPU EP 的路径，
同样没有对应的 ObjC 方法。要拿到这两项，只能绕开这个 SPM 包直接调 C API 函数指针——
正是 §4.1 选型时特意避开的成本，且与上面已实测的 `enable_cpu_mem_arena=false`（无改善）
结论方向一致，判断继续投入的性价比不高，本轮不再跟进。

即便如此，~510MB 常驻对菜单栏应用仍偏重，因此引入**空闲卸载**（§5.2）：
空闲 N 分钟后释放 ORT session；下次按下热键时**与录音并行**重新加载（0.85s），
用户通常在说第一句话，加载对感知延迟几乎不可见。

**录音启动延迟（第二轮架构评审，R4）**：此前这项完全没有实测数据。用一段独立 Swift 脚本
按 `AudioCaptureService.start()` 的真实调用序列（`inputFormat` 查询 → `AVAudioConverter`
构造 → `installTap` → `engine.prepare()` → `engine.start()`）在同一台 M4 MacBook Air 上
测了 8 次（间隔 3s，模拟正常听写节奏）：从调用 `engine.start()` 到第一个真实音频缓冲区
抵达 tap 回调，稳定在 **150 ~ 171ms**，且这个数字几乎全部来自 `engine.start()` 调用本身
与硬件音频流真正开始产出数据之间的间隙（CoreAudio HAL 的设备启动延迟），Swift 侧
`inputFormat`/`converter`/`installTap`/`prepare()` 几步加起来在热启动下也就 80~90ms、
冷启动下仅 47ms，都不是主要占比。

⚠️ 这组数字来自独立脚本、非签名打包的 `.app`、非目标 TCC/沙盒上下文，只是量级参考，
**不是**对真实 App 的实测验证（区分方式见 AGENTS.md）；但已经足以推翻两个候选缓解方案：
Pre-roll（麦克风常开、按键时把已录的缓冲一并送入）本可以完全盖掉这段延迟，但代价是麦克风
指示灯常亮，与本项目"音频仅在设备端处理、不做常驻监听"的隐私定位直接冲突，故不采纳；
"提前预热 `AVAudioConverter`/`engine.prepare()`"看似合理，但从上面的分解看，它最多只能省掉
Swift 侧那 47~90ms 里的一小部分，动不了真正的大头（`engine.start()` 之后的 HAL 启动延迟），
性价比不足以为此在 `.idle` 态常驻一份 converter/tap。结论：这是 CoreAudio 在这类设备上启动
音频流的固有延迟，本轮不再寻求代码层面的缓解，记入 §11.1 已知限制，等待真机走查（P5）时
用打包后的 App 复核这组数字是否随签名/沙盒环境变化。

### 4.4 模型分发 —— 首次启动下载（已定）

App 内**不含**模型，调研期估算体积约 **52MB**（ORT 框架 arm64 切片 45MB + 应用本体）；
实测见 §7——App bundle 解包后 35MB，压缩产物更小。
首启自动下载、校验并部署 SenseVoice-Small（约 240MB），落到用户目录；不等待用户点击，也不受 TCC 权限授权进度阻塞。此后完全离线，版本更新不重下模型。

**已实测验证的下载契约：**

- 端点 `https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo?Revision=master&FilePath=<file>`
  —— 小文件直接 200 返回。
- 大文件 302 跳转到 OSS，响应头带 `accept-ranges: bytes`，`Range` 请求实测返回 **206 Partial Content**
  ⇒ **断点续传可用**。
- 需要 4 个文件，sha256 全部 pin 进二进制：

| 文件 | 字节数 | sha256 |
| --- | ---: | --- |
| `model_quant.onnx` | 241,216,270 | `21dc965f689a78d1604717bf561e40d5a236087c85a95584567835750549e822` |
| `tokens.json` | ~352,000 | `a2594fc1474e78973149cba8cd1f603ebed8c39c7decb470631f66e70ce58e97` |
| `am.mvn` | 11,203 | `29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5` |
| `config.yaml` | 1,855 | `f71e239ba36705564b5bf2d2ffd07eece07b8e3f2bbf6d2c99d8df856339ac19` |

> 附注：OSS 响应头 `x-linked-etag` 实测**就是**文件 sha256（与本地文件一致），可用于下载途中的早期校验，
> 但**不作为信任源**——最终仍以 pin 在二进制里的常量为准。

**`ModelDownloader` 设计要点：**

- 落点 `~/Library/Application Support/VoiceTyper/models/sensevoice-small/`
- `URLSessionDownloadTask`，四个文件**串行**；**先下三个小文件、最后下 onnx**——
  网络/端点问题能在花掉 240MB 流量之前就暴露
- 失败用 `cancel(byProducingResumeData:)` 保存 resumeData 并落盘到同目录，重试即续传；
  App 退出后重开仍可续
- 每个文件下载完立即校验 sha256；不匹配则删除重下一次，再失败则报错并给出手动放置说明
- 写入 `<name>.part` 临时文件 + 校验通过后原子 move —— 中途退出绝不留下"看起来完整"的半个模型目录
- **不加 HuggingFace 镜像**：ModelScope 是本项目既有来源、在大陆可直连；多一条镜像分支只增加
  代码路径而没有实际收益。逃生口是 `asr.model_dir` 手动指定已有目录

**复用机器上已有的模型**：`ModelLocator` 会探测 `~/.cache/modelscope/hub/models/iic/SenseVoiceSmall-onnx/`。
跑过 Python 服务端的机器直接命中，**零下载**。

**许可证动作**：SenseVoice 代码为 MIT，**模型权重**适用
[FunASR Model Open Source License Agreement](https://github.com/modelscope/FunASR/blob/main/MODEL_LICENSE)，
其 §2.2 有署名/模型名要求。由于模型改为用户自行下载、App 不再分发权重，合规压力显著下降；
仍在「关于」面板与 `macos/README.md` 标注 "Powered by SenseVoice-Small (FunAudioLLM)"。

---

## 5. 模块设计

### 5.1 目录结构

```
macos/
├── README.md                          # 面向用户：安装、权限、配置
├── DESIGN.md                          # 本文
├── build_xcode.sh                     # 构建 → dist/VoiceTyper.app + .zip + .dmg
├── Resources/
│   ├── Info.plist                     # LSUIElement=1, NSMicrophoneUsageDescription
│   ├── AppIcon.icns
│   ├── correction.md                  # 从 client-server/server/voice_typer_server/prompts/ 搬运
│   └── THIRD_PARTY_LICENSES.txt
├── scripts/
│   ├── generate_xcodeproj.rb          # 沿用现有 xcodeproj 生成方式
│   ├── fetch_model.sh                 # 开发期/测试用：命令行下载同一份模型
│   └── dump_reference_fixtures.py     # 用 client-server/server/ 生成金标准测试数据
├── Sources/VoiceTyper/
│   ├── App/            VoiceTyperAppMain, AppDelegate, AppCoordinator
│   ├── Core/           AppConfig, AppState, ConfigStore, ConfigMigrator,
│   │                   PermissionCenter, VoiceTyperController, KeychainStore
│   ├── ASR/            ASRService, SenseVoiceEngine, FbankFrontend, LFRCMVN,
│   │                   CTCDecoder, TextPostprocessor, ModelLocator,
│   │                   ModelDownloader, RecognitionBuffer, LocalASRSession
│   ├── LLM/            LLMCorrector
│   ├── Services/       HotkeyService, AudioCaptureService, TextInsertionService
│   ├── UI/             StatusBarController, StatusMenuHeaderView,
│   │                   RecordingHUDController, SetupWindowController,
│   │                   Settings/{SettingsViewModel, PermissionsSettingsView,
│   │                             RecognitionSettingsView, HotkeySettingsView,
│   │                             GeneralSettingsView, HotkeyRecorderField}
│   └── Support/        Constants, Logger, LaunchAtLogin
└── Tests/VoiceTyperTests/
    ├── Fixtures/                      # 金标准音频与特征（gitignored 大文件除外）
    ├── FbankParityTests.swift
    ├── EndToEndRecognitionTests.swift
    ├── ModelDownloaderTests.swift
    ├── TextPostprocessorTests.swift
    ├── RecognitionBufferTests.swift
    └── ConfigStoreTests.swift
```

**关于与 `client-server/client_macos_swift/` 的代码重复**：`HotkeyService` / `AudioCaptureService` /
`TextInsertionService` / `RecordingHUDController` 等约 1600 行会被复制一份。这是**有意的**：
两个工程的配置模型、状态机、UI 结构都会分叉，抽公共 SwiftPM 包会过早冻结接口，
还要反向改造已进入维护期的老客户端。若老客户端在一年后仍在维护，再考虑抽 `VoiceTyperKit`。

### 5.2 ASR 子系统

#### `ModelLocator`
按优先级定位模型目录，**第一个命中即用**：
1. `config.asr.model_dir`（用户显式指定，用于换 fp32 版本或手动放置）
2. `~/Library/Application Support/VoiceTyper/models/sensevoice-small/`（App 下载落点）
3. `~/.cache/modelscope/hub/models/iic/SenseVoiceSmall-onnx/`（**复用 Python 服务端已下载的模型，零下载**）
4. 都没有 → `AppState.modelMissing`，触发首启下载引导（§4.4）

校验：`model_quant.onnx` 或 `model.onnx` + `am.mvn` + `tokens.json` 必须存在；
`config.yaml` 用 Yams 解析取 `frontend_conf`（`lfr_m` / `lfr_n` / `n_mels` / `window`），
缺失时回落到硬编码默认值。**保留读 config.yaml 是为了让侧载 fp32 变体开箱即用。**

#### `SenseVoiceEngine`
```swift
final class SenseVoiceEngine {           // 仅在 asrQueue 上使用，非线程安全
    init(modelDir: URL, language: String, threads: Int) throws   // 加载 = 构造
    func recognize(_ samples: UnsafeBufferPointer<Float>) throws -> String
}
```
- `ORTSessionOptions`：`setIntraOpNumThreads(threads)`、
  `setGraphOptimizationLevel(.all)`、**`addConfigEntry("session.disable_prepacking", "1")`**、
  `setLogSeverityLevel(.warning)`
- `threads` 默认 = `min(4, ProcessInfo.activeProcessorCount)`；配置可覆盖
- 输入张量用 `NSMutableData` 持有；**`ORTValue` 不拷贝数据，必须在 `run` 期间保持强引用**
- 输出：`encoder_out_lens[0]` → 有效帧数 → 切片 `ctc_logits`

#### `CTCDecoder` + `TextPostprocessor`
- 逐帧 `argmax`（`vDSP_maxvi`，25055 维 × 250 帧 ≈ 6.3M 次比较，亚毫秒）
- 去连续重复 → 去 blank（id=0）→ 拼接 tokens
- 后处理**逐条对齐 `recognizer._postprocess`**：
  `▁`→空格 → 剥 `<\|...\|>` → 空白收敛 → 去掉与 CJK 相邻的空格 → 无"实字"则返回空串
  （静音时 SenseVoice 常吐一个孤立句号，必须丢弃）

#### `RecognitionBuffer`（= 服务端 `SenseVoiceSession` 的移植）
**这是服务端最有价值的一段设计，原样搬运，不做"改进"：**

- `PREVIEW_WINDOW = 15 * 16000`：预览只对最近 15 秒重跑，窗口左侧文本固化进 `committedText`
- 滚动时在目标切点 ±100ms 内按 10ms 粒度找**能量最低点**下刀，避免把字切在正中间
- `finalize()` **对完整音频整段重跑**，绝不复用预览拼接结果
- 稳态预览 CPU ≈ RTF × 15s / 0.6s ≈ **27%**，与录音总时长无关（实测 166ms / 600ms）

线程模型：内部音频缓冲用 `NSLock` 保护（与现有 `AudioCaptureService` 风格一致）。
`append` 在 MainActor 调用、O(1) 且永不阻塞；`preview` / `finalize` 在 `asrQueue` 上读取。

#### `ASRService`（MainActor 门面）
```swift
@MainActor final class ASRService {
    enum State { case unloaded, loading, ready, failed(Error) }
    private(set) var state: State
    var onStateChange: ((State) -> Void)?

    func preload() async            // 启动时调用
    func reload() async             // 「重新加载模型」按钮
    func makeSession() -> LocalASRSession
}
```
- 持有 `asrQueue`（串行，`.userInitiated`）。**所有推理串行**，与服务端单 worker executor 一致。
- **空闲卸载**：`asr.idle_unload_minutes`（默认 0=永不卸载，可设 5/10/30 分钟）。计时器到点后在 `asrQueue` 上释放
  engine，`state = .suspendedForIdle`（与"尚未加载"的 `.unloaded` 区分，避免状态变化被无差别
  转发时又反向触发自动预加载）；热键监听不受影响，`makeSession()` 发现未加载时**异步重新
  加载并与录音并行**，首个 partial 延后约 0.85s，finalize 不受影响（除非用户只说了不到 1 秒）。

#### `LocalASRSession`（单次录音会话的本地识别接缝层）
```swift
@MainActor final class LocalASRSession {
    var onPartial: ((String) -> Void)?
    var onFinal:   ((String) -> Void)?
    var onWarning: ((String) -> Void)?
    var onError:   ((String) -> Void)?

    func sendAudio(_ data: Data)
    func finalize(timeout: TimeInterval)
    func close()
}
```
职责映射（对照 `app.StreamRecognizeHandler`）：

| 服务端行为 | `LocalASRSession` 实现 |
| --- | --- |
| `_schedule_preview`：有预览在跑就跳过 | `previewInFlight` 标志，跳过的音频在下次预览一并处理 |
| partial 只在文本变化时下发 | `lastPreview` 比对 |
| `finalizing` 后不再补发 partial | `isFinalizing` 标志 + 回调前二次校验 |
| 预览异常 → `warning(feed_failed)`，连接保留 | `onWarning`，会话继续 |
| `MAX_SESSION_SAMPLES` 后停止收音 | 实际单段上限为 **120 秒**（`maxSessionSamples`，非移植自服务端的 300 秒，见 §11.1 关于内存/耗时的权衡）。达到上限触发一次 `onWarning` 并调用 `onSessionCapped`，控制器据此走一次与松键完全相同的收尾路径（`audioCaptureService.stop()` → `onTailChunk` → finalize 上屏），而不是任由用户继续说下去、后续内容被静默丢弃（R3-03） |
| finalize → 离线整段识别 → LLM → `final` 帧 | `finalize()` → `RecognitionBuffer.finalize()` → `LLMCorrector` → `onFinal` |
| `finalize` 超时保护 | 保留看门狗（本地无网络，但防推理卡死），默认 30s |

**一处相对服务端的增强**：finalize 拿到 ASR 原文后、调用 LLM 之前，先 `onPartial(原文)`
让 HUD 立刻显示识别结果并切到「校对中…」，校对完成后再上屏。原来跨进程时这一步没有意义
（要多一次往返），进程内是**一行代码的免费收益**。

### 5.3 与 `VoiceTyperController` 的接缝

改动清单（**只有 4 处**）：

1. `beginBatchRecording` / `performBatchRecognition` **整段删除**（不再有非流式路径），
   `config.server.streaming` 分支随之消失，`beginRecording` 直接走单一路径。
2. `StreamingASRClient` → `LocalASRSession`：`client.connect(server:llmRecorrect:)` 换成
   `asrService.makeSession()`；其余 `onPartial` / `onFinal` / `onWarning` / `onError` 闭包**一字不改**
   （包括 `[weak client]` 身份比较那套并发会话处理——它对本地会话同样必要）。
3. `healthCheck()` 删除，改为观察 `ASRService.state`。
4. `minimumRecordingDuration = 0.3` **保留**。它现在纯粹是"防误触"，不再是"省流量"，
   但语义仍然成立（`client-server/PROTOCOL.md` §5.1 的约定在一体化 App 里变成内部约定）。

`AppCoordinator` 改动：
- 删除 `serverPollTask` / `startServerPolling` / `beginConnecting` / `ServerHealthProbe` 相关全部逻辑
- `reevaluateReadiness()` 中 `serverReady = await ServerHealthProbe.check(...)`
  → `engineReady = (asrService.state == .ready)`
- 启动时**并行**发起模型预加载与权限检查：权限没给全时模型照样在后台加载好，
  用户授权完立刻可用，不用再等 1 秒

### 5.4 LLM 校对（`LLMCorrector`）

`llm_client.py` 的直译，逻辑保持不变：

- system prompt 从 `Bundle.main` 的 `correction.md` 读取（**文件原样搬运，不改一个字**）
- 保留 3 组 few-shot 消息（内容固定，可命中 LLM 前缀缓存）
- 输入包 `<asr_text>…</asr_text>` 标签
- `max_tokens = max(configured, text.count * 2 + 128)`（防长听写被截断）
- `finish_reason == "length"` → **放弃修正、返回原文**
- 防御性剥离模型回显的标签
- 任何失败（网络/超时/鉴权/解析）→ 记日志 + `onWarning` + **使用 ASR 原文**，绝不丢文本

新增能力（服务端没有的）：设置页「测试校对」按钮，用一段固定的含错样例做真实往返，
把 base_url / key / model 三项配置的错误在**配置时**就暴露出来，而不是等到听写时。

### 5.5 配置与密钥

**配置位置迁移**：`~/.config/voice_typer/config.yaml` → **`~/Library/Application Support/VoiceTyper/config.yaml`**

理由：跨平台共享配置的初衷在 mac-only 一体化 App 里不再成立；`Application Support` 是 macOS 惯例；
同时天然避开与改名后的 `VoiceTyperClient`（继续用 `~/.config/voice_typer/`）互相覆写。
目录下同时放 `logs/`、可选的 `models/`。（曾经的菜单项「打开配置目录」已移除，见 §5.6。）

**首次启动一次性迁移**（`ConfigMigrator`）：若新路径无配置而 `~/.config/voice_typer/config.yaml` 存在，
则继承其中的 `hotkey` 与 `ui.opacity`，`server` 段丢弃。老用户换过来热键不用重设。

**新 schema**：

```yaml
asr:
  language: "auto"          # auto / zh / en / yue / ja / ko
  threads: 0                # 0 = 自动（min(4, 核数)）
  model_dir: ""             # 留空 = 按 ModelLocator 优先级自动定位（§5.2）
  idle_unload_minutes: 0    # 0 = 常驻不卸载（默认）
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key 不在此文件，见下
hotkey:
  modifiers: []
  key: "fn"
ui:
  opacity: 0.85
```

**LLM API Key 存 Keychain**，不落 YAML：
`kSecClassGenericPassword`，service `com.voicetyper.app`，account `llm_api_key`，
`kSecAttrAccessibleAfterFirstUnlock`。约 60 行 `SecItem*` 封装，无第三方依赖。
配置文件位于 `Application Support`（0644），把长期有效的 API Key 明文写进去不合适。

### 5.6 状态机与 UI 变更

**`AppState`**：`.connecting`（连服务端）删除，新增 **`.modelMissing`** / **`.downloadingModel(Double)`** /
**`.modelLoading`**。其余不变。

```
booting ──┬─→ setupRequired ──(授权完成)──────────────────┐
          ├─→ modelMissing ──→ downloadingModel(0…1) ─────┤
          └─→ modelLoading ───────────────────────────────┴─→ idle → recording
                    └─(失败)→ error（设置页可「重新加载模型」/「重新下载」）      → recognizing
                                                                              → inserting → idle
```

权限与模型是**两条互不依赖的准备线**，可并行推进：首次发现模型缺失时应用会在后台自动开始下载，权限没给全时也照常下载/加载。同一进程内失败后不无限重试，用户可在「识别」页手动重试，下次启动也会再自动续传。只有两条线都就绪才进 `.idle`。

菜单栏图标：`.modelMissing` → `arrow.down.circle`（橙）；`.downloadingModel` → 同符号 + `.pulse` 动效，
header 显示「下载模型 42% · 96/240 MB」。

**菜单栏菜单**（删掉「重新连接服务」）：

```
┌ [状态图标] 就绪 · Fn🌐 · 引擎已就绪      ← header
├──────────────────────
│ ⏸  暂停听写
│ ✅  开机自启
├──────────────────────
│ ⚙️  权限与设置…                    ⌘,
│ ✨  使用引导…
├──────────────────────
│ 🔄  检查更新…
│ ℹ️  关于 VoiceTyper
├──────────────────────
│ ⏻  退出                            ⌘Q
```

「打开配置目录」已移除（R5-B9）：`config.yaml` 是内部存储而不是配置入口——所有配置项都有界面，
运行期不监听该文件（改了要重启），手改还会绕过 UI 校验。菜单里给出这个入口只会把用户导向一条
只制造困惑的路径。

header 第三段从「已连接 127.0.0.1:6008」改为「引擎已就绪 / 模型加载中… / 模型加载失败」。

**设置窗口**：3 页（「热键」并入「通用」，识别引擎配置归入「识别」）

| 页 | 内容 |
| --- | --- |
| 权限 | 麦克风 / 辅助功能 / 输入监听（原样保留）。**删掉服务端连通性检查项** |
| 识别 | ① **模型卡片**：首启自动下载，显示百分比进度 +「取消」；失败后显示原因 +「重试下载」；就绪时显示「SenseVoice-Small · int8 · 已就绪」+「重新加载」；空闲卸载后下次录音自动重新加载<br>② 空闲 N 分钟后卸载模型<br>③ 识别语言 Picker（自动/中文/英文/粤语/日语/韩语）<br>④ 智能校对：开关 + Base URL + API Key(SecureField) + 模型 + 温度 + 超时 +「测试校对」 |
| 通用 | 热键（Fn 或组合键，支持按键录制）、触发方式（`hold` / `toggle`）、Fn 键与系统「按下🌐键」的冲突检测、开机自启、HUD 不透明度 |

---

## 6. 配置项取舍对照

| 现有配置项 | 去向 |
| --- | --- |
| `server.scheme` / `host` / `port` / `timeout` | **删除**（进程内无地址概念） |
| `server.api_key` | **删除**（用户明确要求） |
| `server.streaming` | **删除**（本地只有一条路径，天然流式） |
| `server.llm_recorrect` | → `llm.enabled` |
| 服务端 `--model` / `--offline-model` / `--punc-model` | **删除**（只留 SenseVoice-Small） |
| 服务端 `--device` / `--chunk-size` / `--onnx-threads` | `device` 删除；`threads` 降级为 YAML 进阶项；`chunk-size` 删除 |
| 服务端 `--sensevoice-language` | → `asr.language`，**提升到设置面板** |
| 服务端 `--llm-*` 六个启动参数 | → `llm.*`，**全部提升到设置面板** |
| `hotkey.*` / `ui.opacity` | 保留 |
| `ui.width` / `ui.height` | **删除**（现有实现已标注废弃、不再读取） |
| —— | **新增** `asr.model_dir`、`asr.idle_unload_minutes` |

用户可见配置项：**12 → 6**（识别语言、校对开关+4 项校对参数、热键、HUD 不透明度、开机自启、空闲卸载）。
其中"必须配置才能用"的项：**0**。

菜单项：删除「重新连接服务」，能力以「设置 → 识别 → 重新加载模型」保留。

---

## 7. 构建与分发

`macos/build_xcode.sh` 基于现有脚本改造：

1. `xcodebuild -resolvePackageDependencies`（拉 Yams + onnxruntime）
2. Release 构建，`ARCHS=arm64`、`ONLY_ACTIVE_ARCH=NO` —— **只出 arm64 一个变体**，
   现有脚本的三变体 / `lipo` 主程序逻辑整段删除
3. 防御性瘦身：脚本仍保留一步 `lipo -thin arm64`（若架构不是纯 arm64 才执行）。
   **实测结果修正了 §4.1 的预判**：ORT 的 xcframework 虽然打包了
   `macos-arm64_x86_64` 通用切片，但 Xcode 用 `ARCHS=arm64` 构建时，XCFramework
   的"按平台/架构选切片再嵌入"机制会在**拷贝时就只选中 arm64**，产物里的
   `onnxruntime.framework/Versions/A/onnxruntime` 已经是单 arm64（实测确认，
   `lipo -archs` 输出 `arm64`），lipo 这一步实际是空操作——保留只是为了在未来
   ORT 版本/构建行为变化时兜底，不是必需步骤（推翻了此前"必须补"的预判）。
4. 重新 adhoc 签名 `codesign --force --deep -s -`（若 lipo 真的执行过会破坏签名，
   Apple Silicon 强制要求有效签名；未执行时重签名也无害）
5. 打包 `VoiceTyper-<ver>-macOS-arm64.{zip,dmg}`。ZIP 用
   `ditto -c -k --keepParent`（与公证提交包一致），不用 `zip -r`：后者会把
   `onnxruntime.framework` 里的符号链接展开成普通文件，解压后 .app 因 framework
   结构损坏无法启动、已装订签名也被破坏。打包后有一步 `verify_zip_symlinks`
   结构自检（解开 ZIP 确认符号链接存在且不悬空），不依赖签名/公证环境。

**实测体积**（`build_xcode.sh` 完整跑通一次的真实结果，不含模型）：
App bundle 解包后 **35MB**，压缩后 zip **9.8MB**、DMG **11MB**——比最初预估更小，
因为可执行文件/框架二进制压缩率很高，且不再有模型那 240MB 不可压缩的死重。

`scripts/fetch_model.sh` 保留，但角色从"构建前置步骤"变成**开发与测试辅助**：
用同一组 URL 和 sha256 把模型拉到 `~/Library/Application Support/VoiceTyper/models/`，
供跑金标准测试或跳过首启引导用。

`Info.plist` 关键项：`LSUIElement=1`、`NSMicrophoneUsageDescription`、`LSMinimumSystemVersion=14.0`。
`ENABLE_HARDENED_RUNTIME=YES`，`CODE_SIGN_ENTITLEMENTS=Resources/VoiceTyper.entitlements`
（空 entitlements，无强化运行时例外——麦克风访问走 TCC 权限而非 sandbox entitlement，本应用未
启用 App Sandbox）。`build_xcode.sh` 默认仍是 ad-hoc 签名，不受此项影响；若设置
`VOICETYPER_SIGN_IDENTITY`/`VOICETYPER_NOTARY_PROFILE` 可走 Developer ID 签名 + 公证，
详见 `macos/README.md`「签名与公证」一节（R3-16）。

---

## 8. 测试策略

新增 `VoiceTyperTests` 测试 target（现有客户端工程**没有**测试，这次补上）。

| 测试 | 内容 | 通过标准 |
| --- | --- | --- |
| **FbankParityTests** | `dump_reference_fixtures.py` 用 `client-server/server/` 导出一段 0.63s 合成正弦+噪声信号（61 帧，含 `applyLFR` 尾帧补齐分支）的 `[T,80]` fbank 与 `[T',560]` LFR+CMVN 特征；另有一条不依赖 Python/模型的纯 Swift 用例覆盖全零静音输入的对数下限分支 | 帧数**完全相同**；`max‖Δ‖∞ < 1e-3`（静音用例：固定常量误差 < 1e-4） |
| **EndToEndRecognitionTests** | 用 `speech_zh_en_mixed.wav`（15.5s 中英混排+数字+标点）跑完整 Swift 链路 | 编辑距离 ≤ 2（G2 的正式验收，见下方实测结论） |
| **TextPostprocessorTests** | `<\|...\|>` 剥离、中英间距、纯标点丢弃、`▁` 还原 | 覆盖 `_postprocess` 每条规则 |
| **RecognitionBufferTests** | 合成音频驱动滑窗：窗口滚动、切点能量最小、`finalize` 走完整音频 | 预览单调增长不回退为空；finalize 输入长度 == 总样本数 |
| **ConfigStoreTests** | YAML 读写往返、缺字段回落默认、`~/.config` 迁移 | 往返幂等；迁移只带走 hotkey/opacity |
| **LLMCorrectorTests** | `URLProtocol` 打桩：正常 / `finish_reason=length` / 5xx / 超时 | 后三者均返回**原文**，不抛给用户 |
| **ModelDownloaderTests** | 纯函数校验（sha256、文件清单自洽性、下载顺序、模型缺失时的单次自动触发策略）+ `URLProtocol` 打桩：HTTP 非 2xx、sha256 校验失败重试一次后放弃、取消后不再发起下一次尝试 | 缺失模型时每进程自动触发一次；非 2xx 响应体不落盘；校验失败重试上限为 1 次；取消立即生效不残留旧尝试 |

前两组依赖本机已有模型（`ModelLocator` 任一优先级命中即可），缺失时 `XCTSkip`，
保证 CI / 无模型环境下仍能跑其余测试。

**实测结论（P1 阶段实际验证结果）**：用真实语音样本（`say` 合成，15.5s 中英混排+数字+标点）
跑通全链路后，`EndToEndRecognitionTests` 在 27 个测试里唯一出现的差异是一个英文单词的
大小写（"i" vs "I"）。排查方式：把 Swift 计算出的 fbank/LFR/CMVN 特征直接喂给 Python 的
ORT session（绕开 Python 自己的特征提取），解码结果与纯 Python 管线完全一致——这证明
Swift 侧的特征提取本身是正确的（逐点误差 2.9e-4，长音频下仍远低于 1e-3 阈值）。差异根源
是 ONNX Runtime 本身：Python wheel 与 iOS/macOS xcframework 是两套独立编译的二进制，
50 层 transformer 编码器内部浮点求和顺序不保证跨构建一致，在个别真正模棱两可的 token 上
可能翻转 argmax 决策。这是良性的跨平台浮点不确定性，不是逻辑 bug，因此 G2 的验收标准
改为编辑距离容忍度而非字节级相等（见 §1 表格）。

---

## 9. 老客户端改名（`client-server/client_macos_swift/`）

| 文件 | 改动 |
| --- | --- |
| `scripts/generate_xcodeproj.rb` | `TARGET_NAME` → `VoiceTyperClient`；`PRODUCT_BUNDLE_IDENTIFIER` → `com.voicetyper.client`；`PROJECT_PATH` → `VoiceTyperClient.xcodeproj`；**顺手修掉 `MARKETING_VERSION` 的漂移**（脚本里是 2.6.1，pbxproj 里是 2.7.0，以 **2.7.0** 为准） |
| `build_xcode.sh` | `APP_NAME="VoiceTyperClient"` |
| `Resources/Info.plist` | `CFBundleName` / `CFBundleDisplayName` → `VoiceTyperClient`；麦克风用途文案 |
| `Support/Constants.swift` | `appName` → `"VoiceTyperClient"`；`bundleIdentifier` → `"com.voicetyper.client"`；**`configDirectoryName` 保持 `voice_typer` 不动**（老用户配置零迁移） |
| `VoiceTyper.xcodeproj` | 删除后用脚本重新生成为 `VoiceTyperClient.xcodeproj` |
| `README.md` / `docs/*` / `packaging/INSTALL.txt` | 文案替换 |

版本号 **2.7.0 保持不变**（用户明确要求）。

**注意**：`AppLog.subsystem` 取自 `bundleIdentifier`，改 ID 后 `log stream --predicate` 的过滤条件同步变化，需在 `docs/` 里更新排障命令。

---

## 10. 实施阶段

| 阶段 | 内容 | 产出与验收 |
| --- | --- | --- |
| **P0 骨架** | 建 `macos/` 工程与生成脚本（arm64、macOS 14）；搬运客户端代码；接入 onnxruntime SPM 依赖；ASR 层先用假实现（返回固定文本） | 能编译、能起菜单栏、按热键能把固定文本上屏 |
| **P1 引擎** | `FbankFrontend` + `LFRCMVN` + `SenseVoiceEngine` + `CTCDecoder` + `TextPostprocessor`；金标准 fixtures 与测试 | **FbankParityTests / EndToEndRecognitionTests 全绿**（G2 达成，最关键的一步） |
| **P2 会话** | `RecognitionBuffer` 滑窗 + `LocalASRSession` + `ASRService`；接进 `VoiceTyperController` | 真实录音→实时预览→松手上屏，端到端跑通 |
| **P3 配置与 UI** | 新 `AppConfig` schema、`ConfigStore`、`ConfigMigrator`、Keychain；设置窗 3 页重排；菜单精简；`.modelLoading` 状态 | 全新用户路径：打开→授权→可用；老用户热键自动继承 |
| **P3.5 模型获取** | `ModelDownloader`（串行下载、断点续传、sha256 校验、原子落盘）+ 模型卡片 UI + `.modelMissing` / `.downloadingModel` 状态 | 删掉本机所有模型副本后首启无需点击，自动完成下载→校验→加载→可听写；中途断网可续 |
| **P4 校对** | `LLMCorrector` + 设置页 + 「测试校对」 | 开关校对前后文本差异符合预期；断网/错 key 不丢文本 |
| **P5 打包** | `build_xcode.sh`（arm64 单变体）、ORT 框架瘦身与重签名、图标、许可证声明、`fetch_model.sh` 辅助脚本 | 在**干净的另一台 Mac** 上装 DMG 走通 G1（含首启下载） |
| **P6 收尾** | 老客户端改名；根 `README.md` / `CLAUDE.md` 与 `client-server/PROTOCOL.md` 同步；`macos/README.md` | 文档与实现一致，`client-server/PROTOCOL.md` 标注其适用范围不含 `macos/` |

P1 是唯一有真实技术不确定性的阶段，建议**先做 P1 的金标准测试再写实现**（先落 fixtures，
再让 Swift 去追平），这样每一步都有明确的数值反馈。

---

## 11. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| **fbank 数值对不齐** | 识别质量下降且难察觉 | 金标准逐帧比对（1e-3）；退路是 vendoring knf 五个 C++ 文件（半天工作量） |
| **常驻内存 ~510MB** | 菜单栏 App 偏重 | 已实测 `disable_prepacking` 省 290MB 且零性能代价；空闲卸载默认改为"从不"以换取零等待，内存紧张的机器可在设置里改回定时卸载 |
| **首启下载失败**（断网、ModelScope 抽风、磁盘满） | 新用户第一印象直接卡死 | 断点续传 + sha256 校验 + 小文件先行；识别页显示失败原因并提供手动重试，下次启动自动续传；`fetch_model.sh` 作为手动逃生口；`ModelLocator` 会复用 `~/.cache/modelscope/` 已有模型 |
| **ModelScope 接口变更** | 首启下载全面失效 | URL 与 sha256 都是常量，改起来是一次小版本发布；`asr.model_dir` 手动指定是永久逃生口 |
| **模型权重许可** | 分发合规 | 改为用户自行下载后 App 不再分发权重，压力大幅下降；仍在「关于」面板署名 |
| **ORT SPM 包版本漂移** | 构建不可复现 | `Package.resolved` 入库（真正起锁定作用的是这个文件，`xcodebuild -resolvePackageDependencies` 默认遵循它，不会自行升级）；`generate_xcodeproj.rb` 里 ORT 包声明为 `exactVersion`，Yams 声明为 `upToNextMajorVersion`（此前这里误写成统一的 `upToNextMinor`，与脚本实际不符，R4-12） |
| **不支持 Intel Mac** | Intel 用户完全无法使用 | 已定：只出 arm64。README 明确写明最低要求为 Apple Silicon；Intel 用户继续用 `VoiceTyperClient` + `client-server/server/` 方案 |
| **ORT 崩溃拖垮整个 App** | 菜单栏应用退出 | 首版接受（进程内）；`SenseVoiceEngine` 已按可 `load/unload` 设计，将来若需要可平移到 XPC 子进程 |
| 两个 App 同时安装 | 热键互抢、双份权限 | 配置目录已隔离；README 明确写"装了新版请删掉 VoiceTyperClient" |

---

## 11.1 已知限制（明确不修，记录避免重复发现）

- **finalize 超时只停止等待，不停止计算**：`LocalASRSession.finalize(timeout:)` 的看门狗超时后只是提前
  报错、放弃等待，asrQueue 上已经在跑的那次 ORT 推理不会被中断。SPM 检出的 ONNX Runtime ObjC 绑定
  （`ORTRunOptions`，见 `objectivec/include/ort_session.h`）没有暴露 `SetTerminate`，要真正取消运行
  只能绕到 C API 自己包 `OrtRunOptionsSetTerminate`，为一个极低频场景引入平台耦合不划算。最坏情况由单段
  120 秒上限（`LocalASRSession.maxSessionSamples`）约束推理耗时的上界。
- **不做同 App 内的 AX 焦点身份校验**：`TextInsertionService` 只用录音开始时记录的前台应用 pid 判断
  焦点是否变化（跨 App），不识别"同一个 App 内切换了输入字段"。收益场景（几秒内切字段）少见，而
  AX element 身份跨查询是否稳定因 App 实现而异，一旦误判会让正常听写被拒绝插入、退化为只复制到剪贴板，
  代价比它防的问题更常见。
- **录音开始后 ~150~170ms 的音频会被丢失**：按下热键到麦克风真正开始产出数据存在这段固有延迟
  （CoreAudio HAL 设备启动，非 Swift 侧代码可控，见 §4.3 的实测与两个候选方案的否决理由）。
  用户如果按键后立刻开口，最开头的一两个字可能不会被录进去。Pre-roll（麦克风常开）能完全盖掉，
  但与本项目的隐私定位冲突；预热 converter/prepare() 只能省掉小头，不采纳。
  代码层面仍不修，但**已在产品层面缓解**：HUD 的入场动画（180ms）恰好覆盖这段延迟，因此
  首启引导与 README 都明确写了"看到浮窗再开口"，把一个隐藏的固有延迟变成一条可教的操作。
- ~~**松手后（`.recognizing` 阶段）没有主动取消入口**~~ —— **已实现**（见下方 §11.2 R5-04）。
  此前这条按"等真的观察到用户被卡住再立项"暂缓，理由是把它当成了"用户要等多久"的问题。
  重新评估后认为定性错了：真正的问题不是等待时长，而是**结果会被自动写进光标位置，而用户在
  松手之后没有任何阻止手段**（HUD 还设了 `ignoresMouseEvents`）。"说错了想撤回"是高频需求，
  且实现成本远低于原估计——不需要中断 ORT 推理，只要丢弃结果不插入即可，`LocalASRSession.close()`
  与 `cancelFlag` 早就具备抑制迟到回调的能力。

---

## 11.2 R5：从"能用"到"好用"

本轮不改识别管线，只针对**上手与日常顺手程度**。所有改动都在 macOS 侧；Windows 的识别管线
契约（fbank / LFR-CMVN / CTC / 文本后处理 / 模型 I/O）未被触碰，无需同步评估。

| # | 改动 | 为什么 |
| --- | --- | --- |
| R5-01 | **首启四步引导**（`UI/Onboarding/`）：欢迎 → 权限 → 模型 → **试一试** | 三项权限全绿 ≠ 能用。真实链路是「热键 → 麦克风 → 识别 → 插入」，任何一环坏掉，用户看到的现象都是同一个"按了没反应"。最后一步在引导窗口自己的输入框里跑一次真实听写，并在失败时指出断在哪一环——这是唯一能在用户撞上问题**之前**暴露问题的地方 |
| R5-02 | **Fn 键冲突检测**（`Support/SystemKeyboardSettings.swift`） | 默认热键是 Fn🌐，而事件 tap 是 `.listenOnly`（不吞事件）。「系统设置 → 键盘 → 按下🌐键」非"不执行任何操作"时，松手瞬间系统自己的 Fn 行为也会触发（弹表情面板 / 切输入法）。这与 VoiceTyper 的代码无关，用户几乎不可能自己定位到 |
| R5-03 | **`hotkey.mode` = `hold` / `toggle`** | 长段口述（写邮件、写文档）一直按住手指很累。**刻意不做**"短按 toggle、长按 hold"的自动判别：那会把今天被 `minimumRecordingDuration` 丢弃的一次轻碰变成用户毫无察觉就开始的持续录音，误触代价从"什么都没发生"升级为"一直在录" |
| R5-04 | **`.recognizing` 阶段可按 Esc 取消**（`HotkeyService.acceptsCancelWhenInactive`） | 见 §11.1 对原判断的修正。不中断已在飞的 ORT 推理，只丢弃结果不插入 |
| R5-05 | **签名状态自检 + 手动检查更新**（`Support/CodeSigningInfo.swift`、`Services/UpdateChecker.swift`） | ad-hoc 签名下每次更新都可能丢失三项 TCC 授权，这是当前最影响长期使用体验的一件事。真正的解法是 Developer ID 签名（需要账号，不在代码范围内），代码能做的是**让用户事先知道**，并在拿到证书后自动闭嘴。检查更新只在用户主动点菜单时联网一次，不做后台轮询、不引入 Sparkle（appcast 托管 + 更新签名密钥管理不在本轮范围） |

同批修掉的具体问题：

| # | 问题 | 位置 |
| --- | --- | --- |
| B1 | 下载进度动效每 200ms 被打断重建——`appliedState != state` 用 `AppState` 全等，而 `.downloadingModel(Double)` 带进度载荷 | `StatusBarController` 改为比较"外观三元组"（符号 / 着色 / 动效） |
| B2 | 权限没给全时菜单栏完全看不到模型下载进度：下载中 `asrState` 仍是 `.modelMissing`，而 `computeTargetState` 又会先行返回 `.setupRequired` | `engineStatusText()` 自行感知 `isDownloadingModel` |
| B3 | 未就绪时按热键毫无反应 | 只要输入监控已授权就照常监听热键，`VoiceTyperController.blockedReason` 门禁住录音，按下时经 `onBlockedAttempt` 说明缺什么 |
| B4 | 识别结果为空时 HUD 静默消失，与"插进去了但没看见"无法区分 | 新增 `onEmptyRecognition` → `RecordingHUDController.showNoSpeech()` |
| B5 | 窗口已存在（可能在别的 Space / 被遮挡）时点图标毫无反应 | `applicationShouldHandleReopen` 不再看 `hasVisibleWindows` |
| B6 | 状态栏图标没有 tooltip | `statusItem.button?.toolTip` |
| B7 | 取消下载后设置窗口反而抢焦点跳到最前 | `setupControllerIfNeeded` 的"是否抢焦点"改为**只**由 `forceShow` 决定（原先还有 `|| !permissions.allRequiredGranted || !isModelReady`），并加 `forcedPresentations` 做每个理由一次 |
| B8 | 每次保存非 UI 配置都重建整个 `RecordingHUDController` | 改为 `RecordingHUDController.updateConfig(_:)` 就地生效 |
| B9 | 菜单「打开配置目录」把用户导向手改 YAML 这条只制造困惑的路径 | 移除该菜单项与 `ConfigStore.openConfigDirectory()`；README 改为明确标注 `config.yaml` 是内部存储。**刻意不做**文件热重载：那是在往相反方向加固一条本就不该鼓励的入口 |

**待真机验证**（本轮在 Linux 上完成编码，未经 macOS 编译与运行）：

- `SystemKeyboardSettings.fnKeyUsage()` 读的 `AppleFnUsageType` 是未公开偏好项，四个取值的映射
  需要在真机上对照「系统设置 → 键盘 → 按下🌐键」逐项核对；读不到时按"可能冲突"提示，是保守方向。
- `CodeSigningInfo` 对 ad-hoc 与 Developer ID 两种构建的判定。
- 引导「试一试」步骤里，文本能否真的通过 AX / 粘贴写进引导窗口自己的输入框。
- `toggle` 模式与 `.recognizing` 阶段 Esc 取消的真实按键时序。

---

## 11.3 R6：浮窗与性能

延续 §11.2 的方向，这一批只动 HUD 与性能，识别管线本身仍未触碰（Windows 侧无需同步评估）。

### 浮窗

| # | 改动 | 为什么 |
| --- | --- | --- |
| R6-01 | **录音时持续静音则主动提示**（`VoiceTyperController` 的静音探针，1.5s / 4.5s 各探一次） | 麦克风被静音、选错输入设备、蓝牙耳机走错输入端——这几种情况下用户会对着一个什么都没录到的会话一直说，直到松手才发现结果是空的。而信号（电平恒为 0）在第一秒就已经有了 |
| R6-02 | **预览最多两行，超长时从开头截断** | 一行 + 前导省略号意味着长句只剩尾巴，而"边说边校对识别结果"正是浮窗存在的理由。截断自己算（`tailFitting` 二分测量）而不是交给 `NSTextField.byTruncatingHead`：AppKit 只保证对单个行片段做首部截断，多行时没有明确契约，万一退化成截尾部，用户看到的会是长听写的**开头** |
| R6-03 | **LLM 校对期间显示「校对中…」** | 本地推理是几百毫秒且完全可控，校对要走网络、最长等到 `llm.timeout`。都显示"识别中"的话，用户既不知道该不该继续等，也无从判断慢在哪 |
| R6-04 | **录音状态行显示输入设备名** | 与 R6-01 配套：提示"没采到声音"之后，用户需要的下一条线索就是"那它到底在听哪个麦克风" |
| R6-05 | **浮窗位置可配**（`ui.hud_position`：底部居中 / 右下角 / 跟随光标 / 不显示） | "不显示"刻意只隐藏**过程**（录音、预览、成功确认），错误与"没有识别到内容"仍会浮出来——那些信息在别处拿不到（菜单栏只有一个红点），一并静音会把一个可配置项变成静默失败的陷阱 |

### 性能

| # | 改动 | 实测/量级依据 |
| --- | --- | --- |
| R6-06 | **`asr.preload_on_launch` 可切换启动预加载**（后续默认改回 true，见下） | 提供 `ASRService.prepareWithoutLoading()`：只定位模型文件并置为 `.suspendedForIdle`——那个状态的语义本来就是"就绪但引擎不在内存里"，关掉预加载时首次按热键才加载，加载与录音并行（`makeSession`），用户通常正在说第一句话。**R6 曾把默认设为 false，后续评审改回 `true`**：多数用户高频使用，启动即加载让首次听写零等待；关掉仅服务"当天可能一次都不用、且在意常驻 ~510MB"的场景 |
| R6-07 | **`RecognitionBuffer` 一次性预留单段上限容量** | `append` 跑在**主线程**（`sendAudio` 经 `Task { @MainActor }` 派发，不是音频线程——早前的评审记录说反了）。120s 上限下缓冲区最终 7.7MB，倍增扩容时最大的一次要在主线程 memcpy 整个缓冲区，恰好发生在用户说话、浮窗刷预览的时候。`reserveCapacity` 分配的是虚拟地址空间，物理页写入才提交，按上限预留几乎不花内存 |
| R6-08 | **静音段跳过预览**（`LocalASRSession` 的能量门限 VAD） | 预览每 600ms 对 15s 窗口重跑一次、满载 166ms，稳态约 28% 单核；用户思考停顿时这些算力全是浪费。跳过对结果零影响：`finalize()` 永远对完整音频整段重跑。配套把 `RecognitionBuffer.preview()` 的滚动从 `if` 改成 `while`——跳过预览后两次之间可能积压超过一个窗口 |
| R6-09 | **模型权重分四段并行下载** | 241MB 单连接是首启体验的大头；OSS 的 `accept-ranges` / 206 已在 §4.4 实测确认。续传用**自己的**分段文件长度而不是 `URLSession` 的不透明 resume data——后者的畸形数据会让 CFNetwork 直接 abort 进程（§11.1）。只对 ≥32MB 的文件启用；探测不到 206、下载出错或校验失败，一律永久回落到那条经过多轮修复的单连接路径 |
| R6-10 | **下载失败按 5s / 20s / 60s 退避自动重试** | 失败绝大多数是网络抖动，而首启拿不到模型意味着应用完全不可用——这是新用户能遇到的最糟第一印象。续传数据已落盘，重试成本只是续上。给定次数用完就停，避免变成用户看不见的死循环 |

### 待真机验证

- **分段下载**是本批风险最高的一项：分段切分、`Content-Range` 解析、回落路径都有单测覆盖，
  但真实的 ModelScope 302 → OSS 跳转、并发 206、中断续传只能手工验收。验收时建议同时测
  "下到一半退出应用再重开"与"服务端不支持 Range"（可用本地代理强制返回 200）两种情况。
- `AudioInputDevice.currentName()` 在切换蓝牙 / USB 麦克风后的返回值是否及时更新。
- 浮窗四种位置在多屏、竖屏、Dock 位置不同的机器上的实际观感；跟随光标模式在屏幕下沿附近
  的夹逼行为。
- 两行预览的换行与首部截断在中英混排下的实际效果。
- 静音探针的 1.5s / 4.5s 是否会在正常"按下热键后想一下再说"的节奏里误报。

---

## 12. 决策记录

| # | 决策 | 结论 | 说明 |
| --- | --- | --- | --- |
| D1 | 模型分发方式 | ✅ **首启从 ModelScope 下载** | App 52MB；已验证端点、断点续传与 sha256（§4.4） |
| D2 | 架构覆盖 | ✅ **只出 arm64** | 放弃 Intel Mac；构建脚本三变体逻辑整段删除 |
| D5 | 配置目录 | ✅ **`~/Library/Application Support/VoiceTyper/`** | 与 `VoiceTyperClient` 的 `~/.config/voice_typer/` 天然隔离；首启一次性继承热键与 HUD 透明度 |
| D3 | 新 App 版本号 | ✅ **3.3.0**（当前发布） | 起始为 3.0.0，随后随发布迭代到 3.1.0 → 3.1.3 → 3.1.6 → 3.1.9 → 3.2.0 → 3.2.1 → 3.3.0 |
| D4 | 空闲卸载默认值 | ✅ **从不卸载**（`ASRConfig.idleUnloadMinutes` 默认 0） | 初版默认 10 分钟，3.3.0 后改为"从不"：与"启动时预加载"默认开启配套，让按热键始终零等待；内存代价（~510MB 常驻）由用户按需改回定时卸载 |
| D6 | 热键触发方式 | ✅ **显式二选一 `hold`（默认）/ `toggle`** | 否决"短按 toggle、长按 hold"的自动判别：会把被丢弃的误触升级为用户无察觉的持续录音（§11.2 R5-03） |
| D7 | 版本更新提醒 | ✅ **菜单里手动「检查更新…」**，不引入 Sparkle、不做后台轮询 | 后台自动检查与"音频只在本机处理"的定位相悖；Sparkle 需要 appcast 托管与更新签名密钥管理，不在本轮范围（§11.2 R5-05） |
| D8 | 配置文件的定位 | ✅ **内部存储，不是配置入口** | 移除菜单「打开配置目录」，也不做文件热重载：手改会绕过 UI 校验且需重启才生效，给入口只会制造困惑（§11.2 B9） |
| D9 | 启动时是否预加载模型 | ✅ **默认是**（`asr.preload_on_launch = true`），可在设置里关闭 | 多数用户高频使用，启动即加载让首次听写零等待；关掉后按需加载与录音并行、代价接近于零，服务"当天可能一次都不用、且在意常驻 ~510MB"的场景（§11.3 R6-06） |
| D10 | 浮窗的"不显示"语义 | ✅ **只隐藏过程，保留错误提示** | 全部静音会让插入失败、没采到声音这些在别处看不到的信息彻底消失，把一个偏好项变成静默失败的陷阱（§11.3 R6-05） |
| D11 | 分段下载的失败处理 | ✅ **任何异常都永久回落单连接** | 分段是优化，单连接是经过多轮修复的可靠路径。首次安装是唯一必经之路，优化绝不能把它带崩（§11.3 R6-09） |
