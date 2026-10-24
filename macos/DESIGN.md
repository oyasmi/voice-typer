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
| `macos/`（新，主分发版本） | **`VoiceTyper`** | **`com.voicetyper.app`** | **3.6.0**（当前版本，见 D3） |

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
prompts/correction.md                        → 提示词（最初搬运为 bundle 资源；2026-10 由一体化版本重写后反向跟随）
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
│   ├── correction.md                  # 校对提示词，与 windows/Resources/correction.md 保持同一份
│   └── THIRD_PARTY_LICENSES.txt
├── scripts/
│   ├── generate_xcodeproj.rb          # 沿用现有 xcodeproj 生成方式
│   ├── fetch_model.sh                 # 开发期/测试用：命令行下载同一份模型
│   └── dump_reference_fixtures.py     # 用 client-server/server/ 生成金标准测试数据
├── Sources/VoiceTyper/
│   ├── App/            VoiceTyperAppMain, AppDelegate, AppCoordinator
│   ├── Core/           AppConfig, AppState, ConfigStore, ConfigMigrator,
│   │                   PermissionCenter, VoiceTyperController, KeychainStore,
│   │                   DictationMetrics
│   ├── ASR/            ASRService, SenseVoiceEngine, FbankFrontend, LFRCMVN,
│   │                   CTCDecoder, TextPostprocessor, ModelLocator,
│   │                   ModelDownloader, RecognitionBuffer, LocalASRSession
│   ├── LLM/            LLMCorrector, LLMCapabilityStore
│   ├── Services/       HotkeyService, ModifierTapRecognizer, AudioCaptureService,
│   │                   AudioInputDevice, TextInsertionService
│   ├── UI/             StatusBarController, StatusMenuHeaderView,
│   │                   RecordingHUDController, HUDMotion, SetupWindowController,
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

#### 热词偏置解码分支（VOC，§11.7）
词汇表为空或强度为「关」时，`SenseVoiceEngine` 仍走上面的贪心 `CTCDecoder`，一行不改；有词汇时整句改走
`ASR/Hotword/` 的带偏置前缀束搜索（`HotwordCompiler` 用词表前缀树把写法切成 token、`ContextGraph` 做 Aho–Corasick
打分、`CTCPrefixBeamSearch` 解码），产出的 token 串同样交给 `TextPostprocessor`。词汇经
`ASRService.updateVocabulary` 在 asrQueue 上热更新到引擎（与语言一样，不重载模型）。规格见
[`docs/VOCABULARY_SPEC.md`](docs/VOCABULARY_SPEC.md)，实施记录见 §11.7。

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

- system prompt 从 `Bundle.main` 的 `correction.md` 读取。2026-10 重写：按 SenseVoice 的真实错误类型
  （英文术语音译、同音真词）组织规则，新增口误自我修正、中英文之间加空格、口语小数目计数转回汉字、
  口述列举转编号列表，长度约为原来的 56%。与 Windows 共用一份；`client-server/` 3.5.1 起重新跟随本文件
- 8 组 few-shot 消息（内容固定，可命中 LLM 前缀缓存；必须与 `correction.md` 自洽，并与 Windows 逐字一致）
- 输入包 `<asr_text>…</asr_text>` 标签
- `max_tokens = max(configured, text.count * 2 + 128)`（防长听写被截断）
- `finish_reason == "length"` → **放弃修正、返回原文**
- 防御性剥离模型回显的标签
- 任何失败（网络/超时/鉴权/解析）→ 记日志 + `onWarning` + **使用 ASR 原文**，绝不丢文本

**深度思考参数与能力缓存（R7-A1）**：请求默认带 `"thinking": {"type": "disabled"}`。该写法已对照
火山方舟（豆包系列）与智谱（GLM-4.5 系列）的当前 chat completions 文档核对（2026-09，文档检索，未逐一实测请求）。
服务以 HTTP 400/422 且响应体（只读前 4KB，仅用于判定、绝不写日志）含 `thinking` 拒绝时，去掉字段重发**一次**，
重发超时取剩余预算 `max(1, timeout − 已耗时)`。结果按 `SHA-256(请求地址 + "\n" + 模型)` 前 16 位缓存在
`UserDefaults`（`llm.thinkingParameterUnsupported`，最多 32 条，**不含 API Key**）；控制器每次保存配置都会重建
`LLMCorrector`，缓存因此不放在实例里。「测试校对」忽略缓存重新探测并纠正缓存。其他厂商的等价开关
（`enable_thinking`、`reasoning_effort`）本批次不做。

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
  interface_language: "zh"   # zh / en；界面语言（默认中文），重启后全面生效
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
5. 打包 `VoiceTyper-<ver>-macOS-arm64.{dmg,zip}`。DMG 是正式分发物，先生成；ZIP 是附带
   产物，打包失败只告警、不阻断构建（`VOICETYPER_SKIP_ZIP=1` 可直接跳过）。ZIP 首选
   `ditto -c -k --keepParent`（与公证提交包一致）；曾在一台装有终端安全软件的 Mac 上实测
   ditto 被 SIGKILL（`Killed: 9`），因此 ditto 失败时退回 `zip -ry`。不能用不带 `-y` 的
   `zip -r`：它会把 `onnxruntime.framework` 里的符号链接展开成普通文件，解压后 .app 因
   framework 结构损坏无法启动、已装订签名也被破坏；`-y` 按链接本身存储，ditto 额外保留的
   扩展属性在已签名 bundle 中本就不存在，不影响签名/公证。打包后有一步 `verify_zip_symlinks`
   结构自检（用 `unzip` 解开 ZIP 确认符号链接存在且不悬空），不依赖签名/公证环境；自检
   失败仍是构建失败。

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
详见 `macos/DEVELOPMENT.md`「签名与公证」一节（R3-16）。

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
| R5-02 | **Fn 键冲突检测**（`Support/SystemKeyboardSettings.swift`） | 默认热键是 Fn🌐，而事件 tap 当时是 `.listenOnly`（不吞事件；3.5.2 起有辅助功能权限时改为主动 tap，但 Fn 的 `flagsChanged` 仍永不吞，见 §11.5 R8-B1）。「系统设置 → 键盘 → 按下🌐键」非"不执行任何操作"时，松手瞬间系统自己的 Fn 行为也会触发（弹表情面板 / 切输入法）。这与 VoiceTyper 的代码无关，用户几乎不可能自己定位到 |
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
| R6-02 | **预览最多两行，超长时从开头截断** | 一行 + 前导省略号意味着长句只剩尾巴，而"边说边校对识别结果"正是浮窗存在的理由。截断自己算（`tailFitting` 二分测量）而不是交给 `NSTextField.byTruncatingHead`：AppKit 只保证对单个行片段做首部截断，多行时没有明确契约，万一退化成截尾部，用户看到的会是长听写的**开头**。**注**：标签约束未随之修改，多行实际从未生效；3.5.2 修复并改为最多三行，见 §11.5「浮窗预览排版修复」 |
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
- ~~`AudioInputDevice.currentName()` 在切换蓝牙 / USB 麦克风后的返回值是否及时更新~~：R7 起 HUD 显示的是录音时
  实际使用的设备（`AudioCaptureService.activeInputDevice`），该函数已移除。
- 浮窗四种位置在多屏、竖屏、Dock 位置不同的机器上的实际观感；跟随光标（3.5.2 起为「跟随输入位置」，见 §11.5 R8-E3）模式在屏幕下沿附近
  的夹逼行为。
- 两行预览的换行与首部截断在中英混排下的实际效果（3.5.2 起为三行，并入 §11.5「浮窗预览排版修复」的真机验证）。
- 静音探针的 1.5s / 4.5s 是否会在正常"按下热键后想一下再说"的节奏里误报。

---

## 11.4 R7：耗时度量、LLM 提速、输入设备、热键、粘贴与浮窗动效

六项都不触及两平台共享的识别契约（fbank / LFR-CMVN / CTC / 文本后处理 / 模型 I/O / `correction.md`），
Windows 无需同步修改；A7 的度量思路对 Windows 同样适用，记为后续事项。

| # | 改动 | 要点 |
| --- | --- | --- |
| R7-A7 | **会话级耗时日志**（`Core/DictationMetrics.swift`） | 每次听写（成功、为空、取消、丢弃、失败、启动失败）收尾时**恰好一行** `.notice` 日志，`stop()` 不上报。度量载体是值类型，时钟与出口（`onMetrics`）均可注入以便单测；结构体**禁止任何 String 存储属性**（有反射测试钉死），摘要因此可安全标 `.public`。会话内打点在 `LocalASRSession.timings`，控制器在 `close()` 之前读取 |
| R7-A1 | **关闭深度思考 + 能力缓存** | 见 §5.4 |
| R7-A4 | **粘贴按键盘布局取键码**（`PasteKeyResolver`） | 遍历键码找产生 `v` 的那个；`QWERTYCMD` 布局与名称以 ⌘ 结尾的布局直接用 ANSI_V；每次粘贴现查不缓存。已知不修：热键录制按物理键位记录，非 QWERTY 下键名与键帽可能不一致，但录制与匹配同键码，功能正确 |
| R7-A2 | **蓝牙场景改用内置麦克风**（`AudioInputDevice.resolveInput`） | 配置 `audio.input_device`：`auto`（默认）/ `system` / 设备 UID（为后续手选预留）。auto 仅在「默认输出与默认输入都是蓝牙 + 有可用内置输入 + 未合盖」时偏离系统默认——输出不是蓝牙时蓝牙麦克风是用户有意的选择，一律尊重。设备在录音开始前解析一次、录音中不切换。**目标就是系统默认输入时不设置设备**（`AudioInputDevice.deviceToPin`）：跟随默认时引擎挂在自建的 `CADefaultDeviceAggregate` 上，显式设成同一个麦克风会让 AUHAL 真的切换一次，引擎启动后约 100ms 发出 `AVAudioEngineConfigurationChange` 并停机——3.5.0 曾因"每次 start 都显式设置"导致启动后 / 睡眠唤醒后第一次按热键 HUD 一闪而过、录音为空（系统日志 `caller requesting device change from <聚合> to <麦克风>` 可佐证，已用独立脚本在真机复现：钉默认设备必然触发、不钉从不触发）。只有偏离默认时才钉；上次钉过、这次回到默认时整体重建引擎。尚未收到任何样本时的配置变更视为启动阶段切换，透明重启采集一次（真机脚本验证重启后不再触发、样本正常）。HUD 与日志：钉住时读回 AUHAL 实际设备，跟随默认时用系统默认输入。设置失败只记 warning、不中断录音 |
| R7-A3 | **单独修饰键热键**（`ModifierTapRecognizer`） | 右 ⌘ / 右 ⌥ / 左 ⌥ / 右 ⌃，左右靠 keyCode 与设备位区分。纯值类型状态机：干净按下 → `.began`；按住期间任意 keyDown / 鼠标按下 / 其他修饰键 → `.cancelled`（至多一次）；抬起且仍干净 → `.ended`。hold 模式**按下即开始采集、组合快捷键静默丢弃**，代价是作修饰键使用时麦克风短暂打开、菜单栏指示点闪一下，因此浮窗延迟 150ms 出现；toggle 模式只有干净的单击才切换、不打开麦克风。Esc 同样喂给识别器使手势作废，避免 toggle 下松开时被误当作一次单击。刻意不支持左 ⌘、Shift、左 ⌃。门禁态下只在干净单击时提示原因，避免组合快捷键时被骚扰 |
| R7-B4 | **浮窗动效**（`UI/HUDMotion.swift`） | 录音→识别：波形 0.12s 收拢并由白变橙后再脉动，前景做一次 1→0.985→1 的「呼气」；圆点颜色 0.2s 过渡；结果态图标弹簧弹入、状态文字上移 2pt 淡入；「校对中…」灰阶流光（`layer.mask`）。**只对 `topRow` / `previewClipView` 做 presentation 层动画，绝不对 `NSVisualEffectView` 做 transform**（会破坏磨砂渲染），也不写 model 值（AppKit 管理 layer-backed 视图几何）。「减少动态效果」开启时无缩放 / 位移 / 弹簧 / 流光，仅保留 ≤0.15s 颜色与透明度过渡 |

**验证口径**：全部单元测试通过（含新增的度量、LLM 能力缓存、粘贴键码、设备解析、修饰键状态机、控制器手势与
HUD 动效参数测试）。以下**尚未在真机验证**，不得当作已验证：A7 五种 outcome 的实测日志；A1 对真实混合推理
模型 / 会拒绝未知字段的服务的行为；A4 在 Dvorak / Colemak / Dvorak-QWERTY ⌘ / 拼音输入法下的粘贴；
A2 的六个 AirPods 场景（含合盖、连续两次切换策略、识别准确率对比）；A3 的六个热键场景（含外接键盘、
中文输入法下单击右 ⌘ 不切输入法）；B4 深浅色、四种位置、减少动态效果下的观感（`CASpringAnimation`
作用于 `transform` 与 `CAGradientLayer.locations` 越界取值的实际渲染尤其需要肉眼确认）。

---

## 11.5 R8：用户体验打磨

实施规格见 [`docs/UX_R8_SPEC.md`](docs/UX_R8_SPEC.md)（编号 `R8-xx`，分四批）。主题：说出的话永远找得回来、
热键与 Esc 不穿透、等待期反馈、跟嘴跟手、输出格式与自定义词汇。已定决策中影响输出的一条：**中英文之间统一加空格**
（在 `TextPostprocessor` 之后的独立格式化层实现，不改识别契约与金标准夹具，两平台共用用例夹具）。

### 第一批（3.5.2）：不丢字 · 不打扰

**验证口径**：全量 `xcodebuild … test` 通过（见各项）。以下所有涉及热键、剪贴板、麦克风、浮窗、辅助功能的行为
**均未在真机验证**，下表「真机验证」列一律写「未验证」，不得当作已验证。实现顺序按规格 §11：
R8-F2 → R8-A2 → R8-A1 → R8-C1 → R8-C2 → R8-C3 → R8-B2 → R8-B1（B1 单独提交）。

#### R8-F2 中英文之间统一加空格

```text
编号：R8-F2
提交：第一批提交（feat(macos): R8 第一批——…）
改动后的行为：上屏文本、浮窗预览、「最近结果」里的文本，中文与英文单词之间都有一个半角空格；与是否启用 LLM 无关。
  LLM 收到的仍是 ASR 原文（不改发给 LLM 的文本）。TextPostprocessor 与金标准夹具未动。
接口 / 配置 / 度量字段变更：新增 Text/OutputFormatter.swift（applyCJKLatinSpacing / formatForOutput）；
  共享夹具 Fixtures/text_format_cases.json（25 条，含规格要求的 19 条）。无配置、无度量字段。
自动化测试及结果：OutputFormatterTests（逐条读夹具 + 幂等断言）；VoiceTyperControllerTests 增补「插入服务与回调收到
  格式化后的文本」「预览回调收到格式化后的文本」。全部通过。
真机验证：不涉及系统 API，无需真机；真实输入法 / 应用里的观感未验证。
与本规格的差异及理由：夹具在规格 19 条基础上多加 6 条边界用例（数字开头词元、单字母、括号、@、纯数字与汉字等），
  未删改规格里任何期望值。
Windows 影响：必须。C# 实现同一规则并在 VoiceTyper.Tests.csproj 里 Link 同一份夹具，两平台规则只能一起改（未做）。
```

#### R8-A2 最近结果（内存）与单例插入服务

```text
编号：R8-A2
提交：第一批提交
改动后的行为：菜单栏在 header 之后新增「复制上一次结果」与「最近结果」子菜单（最多 10 条，新的在前，前 30 字符，
  后缀（未输入）/（已取消）/（部分），末尾「清空最近结果」）；点击条目写入剪贴板（concealed / transient）并弹 0.9s
  「已复制到剪贴板」（不受浮窗「不显示」影响，听写进行中不打断）。AppCoordinator 持有唯一的 TextInsertionService，
  重建控制器不再丢 pendingRestore。
接口 / 配置 / 度量字段变更：新增 Core/DictationHistory.swift；VoiceTyperController.onResultAvailable、
  init(textInsertionService:)（原已有）改由协调器注入；StatusBarController 新增 historyEntriesProvider /
  onCopyHistoryEntry / onClearHistory；RecordingHUDController.showCopiedToClipboard()。
自动化测试及结果：DictationHistoryTests（容量淘汰、去重、空白忽略、清空、onChange、菜单标题）；控制器各结局记录
  （已输入 / 焦点变化 / 插入失败 / 取消有预览 / 取消无预览 / 空与丢弃不记录）；AppCoordinatorInsertionServiceTests
  （重建控制器后插入服务同一实例）。通过。
真机验证：未验证。手工步骤：① 正常听写 → 菜单出现条目；② 听写中切到别的应用 → 条目带「（未输入）」；
  ③ 识别中按 Esc → 条目带「（已取消）」；④ 点击条目后在 TextEdit 粘贴，内容正确；⑤ 清空后菜单项禁用；
  ⑥ 退出重开，列表为空。另：浮窗设为「不显示」时点击条目仍应弹出确认。
与本规格的差异及理由：「取消」只在 isRunning 时记录（与「已取消」提示同条件）。
Windows 影响：可选（Windows 只有「上一次」）。
```

#### R8-A1 终稿看门狗按时长缩放 + 失败兜底

```text
编号：R8-A1
提交：第一批提交
改动后的行为：看门狗 = min(max(录音秒数×5, 30), 300)，从推理真正派发到 asrQueue 起算（不含等引擎加载）；等引擎上限
  60s，加载失败 / 模型缺失（engineUnavailable）时立即失败；超时 → 把已识别预览（格式化后）复制到剪贴板并进入「最近结果」，
  浮窗「识别超时，已把识别出的部分复制到剪贴板」；其他失败 → 只进「最近结果」，不碰剪贴板，报错追加「可在菜单栏「最近结果」
  里找回已识别的部分」；无预览则两者都不发生。等引擎时浮窗状态行改为「录音中 · 模型加载中…」（已攒 1s 音频）/
  「识别中 · 正在加载模型…」（松键后约 300ms 仍未就绪）。
接口 / 配置 / 度量字段变更：VoiceTyperController.finalizeWatchdogTimeout(audioSeconds:)；LocalASRSession 新增
  engineUnavailable 构造参数、onEngineWait / EngineWaitStage；ASRSessionTimings.finalizeTimedOut；度量末尾追加
  finalize_timeout=0|1；RecordingHUDController.setEngineWait(_:)。
自动化测试及结果：公式 5 例；超时置位 finalizeTimedOut；看门狗不含引擎加载时间；engineUnavailable 立即失败；
  onEngineWait 每阶段至多一次；超时有预览 → 复制 + 历史 1 条；普通失败有预览 → 不复制 + 历史 1 条；无预览 → 都不发生。通过。
真机验证：未验证。手工步骤：设置「空闲卸载 5 分钟」并关闭启动预加载 → 重启后立刻按住热键说 3 秒 → 浮窗应出现
  「录音中 · 模型加载中…」，结果正常上屏。超时路径可在 Debug 构建临时把公式下限改成 0.1s 验证（不得提交）。
与本规格的差异及理由：(1) 失败文案拼接用 LF("%@。%@")（先去掉原文末尾标点），中英文各有对应格式串；
  (2) 看门狗时长通过控制器 init 参数注入（默认即公式），仅为让测试不必等 30s。
Windows 影响：已有（W-01），对齐即可。
```

#### R8-C1 校对回落不再被「已输入」盖掉

```text
编号：R8-C1
提交：第一批提交
改动后的行为：校对回落时成功提示变为「已输入 · 未校对」+ 原因一行，显示 2.5s；原因六档见规格表。浮窗为「不显示」时仅
  鉴权失败仍提示（配置类问题），其余不提示。LocalASRSession 不再发「智能校对未成功」的 onWarning。
接口 / 配置 / 度量字段变更：LLMCorrector.FallbackReason、CorrectionReport.fallbackReason；ASRSessionTimings.llmFallbackReason；
  VoiceTyperController.onInsertedWithNote(InsertionNote?)（InsertionNote 带 showsWhenHUDHidden）；
  RecordingHUDController.showSuccess(note:)；度量末尾追加 llm_fail=<reason|->。
自动化测试及结果：LLMCorrectorTests 覆盖 401/403/429/5xx/其他 4xx/无法解析、URLError.timedOut / 其他 URLError、
  截断 / 空结果六档映射；控制器 insertionNote 映射与「不显示」例外；成功时 note 为 nil。通过。
真机验证：未验证（需要真实服务：改错 Key / 断网各试一次，观察浮窗两行提示的排版与停留时长）。
与本规格的差异及理由：(1) 英文文案沿用界面既有术语「AI proofreading」而非规格里的「Smart correction」，
  避免同一功能两种叫法；(2) 非 HTTP、无法解析的响应归入「服务出错」。
Windows 影响：已有（WT-16）。
```

#### R8-C2 校对中再按热键跳过校对

```text
编号：R8-C2
提交：第一批提交
改动后的行为：校对中浮窗状态行为「校对中… · 再按 %@ 跳过」；此时按热键（hold / toggle）立即上屏识别原文，提示
  「已输入 · 未校对 / 已跳过智能校对」（不算失败，不受「不显示」例外）。单独修饰键只认干净单击（松开时），按下时
  不跳过也不弹「上一段听写尚未完成」。
接口 / 配置 / 度量字段变更：LocalASRSession.isCorrecting / skipCorrection()；ASRSessionTimings.LLMResult.skipped
  （llm_result=skipped）；RecordingHUDController.setCorrecting(hotkeyDisplay:)。
自动化测试及结果：跳过后 onFinal 恰好一次且为原文、迟到结果被忽略、不能重复跳过、未校对时返回 false；控制器：hold 下按键
  跳过且不开始新录音，修饰键按下不跳过、干净单击才跳过，未校对时按键行为不变。通过。
真机验证：未验证（需开启智能校对，在校对中按热键；右 ⌘ 单击与 ⌘C 各试一次）。
与本规格的差异及理由：控制器 init 新增 llmCorrectorOverride（仅测试注入桩 URLSession）。
Windows 影响：已有（C3）。
```

#### R8-C3 120 秒上限倒计时

```text
编号：R8-C3
提交：第一批提交
改动后的行为：录音剩余 ≤ 15s 时计时位置变为橙色「还剩 N 秒」，每 0.5s 刷新；进入识别阶段恢复常规色与已录时长。
接口 / 配置 / 度量字段变更：LocalASRSession.maxSessionSeconds（maxSessionSamples 由它推出）；
  RecordingHUDController.countdownSeconds(elapsed:)。
自动化测试及结果：countdownSeconds：104.9 → nil、105 → 15、110.2 → 10、119.6 → 1、120 → nil。通过。
真机验证：未验证（录满 105s 以上观察橙色倒计时与文案宽度；中英文各一次）。
与本规格的差异及理由：无。
Windows 影响：已有（C3）。
```

#### R8-B2 安全输入与密码框

```text
编号：R8-B2
提交：第一批提交
改动后的行为：(1) 焦点是密码框（AXSecureTextField）时按热键不开始录音，浮窗锁形图标 +「当前是密码输入框…」2s，
  不受「不显示」影响，度量 outcome=secure_field；(2) 插入前再查一次，若焦点已到密码框则不插入、改为复制并提示
  「目标是密码输入框，结果已复制到剪贴板」，进入「最近结果」；(3) 系统安全输入开启且热键为组合键时，菜单 header
  第三行、设置 → 通用 → 热键下方与引导「试一试」排障列表显示「「%@」开启了安全输入，组合键热键暂时收不到」（设置页附
  「Fn 与单独修饰键热键不受影响」）。
接口 / 配置 / 度量字段变更：新增 Support/SecureInput.swift（isEnabled / ownerPID / warning / currentWarning）、
  Services/FocusInspector.swift（FocusInspecting + AXFocusInspector）；控制器 init(focusInspector:)、
  onSecureFieldRefused；DictationMetrics.Outcome.secureField；StatusMenuHeaderView.setWarning。
自动化测试及结果：控制器：密码框时不调用 audioCapture.start 并上报 secure_field、离开后可立即听写、插入前变成密码框 →
  复制不插入且进历史、单独修饰键（hold）按下不提示而干净单击才提示；SecureInputTests（有 / 无应用名、Fn 与单独修饰键、
  未开启时不显示）。通过。
真机验证：未验证。手工步骤：Safari 登录页密码框按住 Fn → 拒绝提示；终端开启「安全键盘输入」后：组合键热键 → 菜单
  header 有说明；Fn 热键 → 记录是否仍能触发（V）；关闭后提示消失。ownerPID（IOConsoleUsers 里的
  kCGSSessionSecureInputPID）字段是否存在待真机确认，取不到时应用名回落为「某个应用」。
与本规格的差异及理由：(1) 单独修饰键（hold）每次组合快捷键都会「按下」，密码框里的拒绝提示留到干净单击（松开）时才弹，
  否则 ⌘C 之类会被反复骚扰（「不打扰」优先）；(2) AX 查询设 250ms 消息超时，主线程不会因目标应用无响应而卡数秒；
  (3) 插入前的密码框检查在主线程做一次 AX 查询，会计入 release_to_done，真机需确认其耗时可忽略。
Windows 影响：建议（UIA IsPassword）。
```

#### R8-B1 热键与 Esc 不穿透

```text
编号：R8-B1
提交：单独一个提交（feat(macos): R8-B1——…），位于第一批提交之后
改动后的行为：有辅助功能权限时事件 tap 改为 .defaultTap，只吞被 VoiceTyper 消费的按键——组合键主键的 keyDown
  （含自动重复）与 keyUp、本次触发了 .cancel 的 Esc 的 keyDown / 自动重复 / keyUp；flagsChanged（Fn、单独修饰键、
  任何修饰键）、鼠标事件、其他按键永不吞。门禁态下按下热键同样吞（这仍是我们的热键，放行会打出字符）。创建主动 tap 失败
  或没有权限则以 .listenOnly 重试，行为与 3.5 相同；日志记一行当前模式（不含按键内容）。
接口 / 配置 / 度量字段变更：新增 Services/HotkeyEventFilter.swift（纯值类型：Input / Action / Decision，
  handle(_:acceptsCancelWhenInactive:)）；HotkeyService 只做 CGEvent→Input 转换、主线程派发与按 consume 返回 nil /
  原事件。无配置、无度量字段变更。引导「试一试」排障文案改为「热键与系统快捷键冲突…」。
自动化测试及结果：先把原逻辑原样迁入 HotkeyEventFilter，以 9 条行为钉子（Fn / 组合键 / Esc / 单独修饰键的动作序列）
  与既有 ModifierTapRecognizerTests 全绿，再加吞键规则：按下 / 重复 / 松开均吞且配对；先松修饰键再松主键仍配对；
  修饰键不匹配时放行且 keyUp 也放行；Esc 只在录音中 / acceptsCancelWhenInactive 时吞，不在听写时放行；flagsChanged 永不吞；
  单独修饰键与 Fn 不吞普通键；keyUp 丢失后的陈旧记录由下一次非重复 keyDown 清除。全量 330 个测试通过。
真机验证：未验证（没有在真机上创建过主动 tap；本机自动化环境也没有辅助功能授权）。手工步骤：
  1. 热键设为 ⌥Space（按住模式），在 TextEdit 里按住 3 秒说话 → 不应出现任何空格，只有识别文本。
  2. 热键设为 ⌃⌥D，在终端里按住说话 → 终端不应收到字符。
  3. Safari 全屏播放视频时按住热键再按 Esc 取消 → 视频不应退出全屏；不在听写时按 Esc → 正常退出全屏。
  4. 热键设为 ⌃Space，记录系统输入法是否仍会切换（V：系统快捷键可能在 session tap 之前处理，结论写进本节）。
  5. 撤销辅助功能权限 → 日志显示退回 listenOnly，热键仍可用（按门禁提示走）。
  6. 连续快速打字 1 分钟（含按住热键期间打字）→ 无可感知的输入延迟。
  另需确认：主动 tap 的回调超时被系统禁用后 reenableTap() 能恢复；Fn 热键下 Esc 被吞后 Fn+Esc 是否有系统含义。
与本规格的差异及理由：(1) 对「已吞 keyDown 的键码」的非重复 keyDown 视为 keyUp 丢失的陈旧记录并清除（规格只写了配对不变量，
  没写 tap 被系统短暂禁用后的恢复，这样做避免该键被永久当成重复）；(2) 已激活的组合键，主键的任何再次 keyDown 都吞
  （规格写「已处于激活态的同键码重复」），不检查修饰键是否仍按着——用户先松修饰键、主键继续自动重复时仍要吞，否则会漏出字符。
Windows 影响：已有（WR-007）。
```

### 第二批（3.5.2）：跟嘴 · 跟手 · 稳

**验证口径**：全量 `xcodebuild … test` 通过（363 个，连跑两次无波动）。同第一批：热键、剪贴板、麦克风、浮窗、辅助功能、
系统睡眠 / 锁屏相关行为**均未在真机验证**，下面各项「真机验证」一律写「未验证」。实施顺序：
R8-D1 → D2a → A3a → A5 → B3 → C4 → C5 → G1 → H1。**版本号保持 3.5.2**（第一、二批共用，不再递增）。

#### R8-D1 预览改为事件驱动（跟嘴）

```text
编号：R8-D1
提交：第二批提交（feat(macos): R8 第二批——…）
改动后的行为：采集块 600ms → 200ms（AudioCaptureService 默认 chunkSamples=3200）。预览只在下列条件全部满足时才跑：无预览在跑、
  未进入终稿、自上次预览以来有语音、自上次预览开始累计 ≥ 4800 个新样本（300ms）、当前时刻 ≥ previewNotBefore
  （= 上次预览结束 + 上次耗时/2，故稳态推理占空比 ≤ 2/3）。不引入定时器，下一个采集块最迟 200ms 后到达再检查。
  RecognitionBuffer 顶部的稳态 CPU 注释同步更新。
接口 / 配置 / 度量字段变更：ASRSessionTimings 新增 previewDeliveredNanos / previewDeliveredCount /
  finalizeDispatchedAt / finalizeInferenceStartedAt；度量末尾追加 preview_avg（预览从发起到文字送达主线程的平均耗时）与
  final_wait（finalize 派发到推理真正开始）。
自动化测试及结果：样本不足 300ms 不跑且不计入「静音跳过」；占空比限制（假时钟：耗时 400ms → 200ms 内不跑，推进后再跑）；
  preview_avg 取发起到送达；final_wait 两个时间点。通过。
真机验证：未验证。验收方法（规格）：同一段 20s 口述改动前后对比 previews（应明显增多）、preview_avg（应下降）、活动监视器 CPU
  （不长期贴满一个核）；M1 等较慢机型至少测一次。「说话到出字 P50 ≤ 500ms」尚无实测数据。
与本规格的差异及理由：无。
Windows 影响：已有（A1）。
```

#### R8-D2a 采集「松键时仍在说话」度量

```text
编号：R8-D2a
改动后的行为：控制器保留最近 150ms 的带时间戳电平样本；松键时若最后 100ms 内最大电平 ≥ silenceRMSThreshold，度量
  tail_speech=1，否则 0。只采数据，不改变任何行为（补录见第三批 R8-D2b，门槛：tail_speech=1 占比 ≥ 20%）。
接口 / 配置 / 度量字段变更：DictationMetrics.tailSpeech（Bool?）；度量末尾追加 tail_speech。
自动化测试及结果：窗口边界（50ms 内算、120ms 前不算、低于阈值不算、空缓冲）；端到端写入度量；无松键事件（取消）为未测量。通过。
真机验证：未验证（需在真机上积累一个版本的日志后统计占比）。
与本规格的差异及理由：没有「松键」的路径（取消、设备变化、达到上限）输出 tail_speech=-（未测量）而不是 0，避免把没测的当成「没在说话」。
Windows 影响：已有（tail_speech）。
```

#### R8-A3a AX 插入回读度量 + 网页控件不做整值替换

```text
编号：R8-A3a
改动后的行为：(1) kAXSelectedText 写入失败后，若焦点元素自身或其祖先（沿 kAXParent 最多 20 层）是 AXWebArea，不做
  kAXValue 整值替换，直接走粘贴；(2) AX 写入前读一次值（≤100k 字符），写入成功后等 50ms 再读，判定 ok / unchanged / unknown。
  只记录、不回退（回退见第三批 R8-A3b，需要数据）。日志与度量都不含值内容。
接口 / 配置 / 度量字段变更：InsertionVerification（含纯函数 classify）；TextInsertionService.lastInsertVerification；
  TextInserting 协议新增 lastInsertVerification（带默认实现）；度量末尾追加 insert_verify=ok|unchanged|unknown|-。
自动化测试及结果：classify 五种情形；控制器把回读判定写入度量。通过。AX 行为本身无法单测。
真机验证：未验证。手工验证矩阵（规格）：TextEdit、备忘录、Pages、Safari 文本框 / 富文本编辑器、Chrome 文本框 /
  contenteditable、VS Code、Slack、微信、飞书、终端、iTerm2、Xcode——每个应用记录 insert_path 与 insert_verify。
与本规格的差异及理由：回读的 50ms 等待在主线程同步进行（规格如此），会计入 AX 路径的 release_to_done；写入前读值
  超过 100k 字符时跳过（得 unknown）。真机需确认这 50ms 可接受，不可接受则改为异步回读后补记度量。
Windows 影响：不需要。
```

#### R8-A5 剪贴板备份提前到识别阶段

```text
编号：R8-A5
改动后的行为：进入 .recognizing 时（finalize 派发之后，与 asrQueue 上的推理并行）调用 prepareForInsert() 备份用户剪贴板；
  粘贴兜底时若 changeCount 与快照一致直接使用，否则重新快照；仍在等待中的 pendingRestore 继承逻辑优先于提前快照；所有不插入的
  收尾（取消、失败、空结果…）与每次收尾都会 discardPreparedBackup()。copyToClipboard 也会作废提前快照。
接口 / 配置 / 度量字段变更：TextInserting 新增 prepareForInsert() / discardPreparedBackup()（带默认空实现）；度量末尾追加
  clip_backup（毫秒，无提前备份为 -）。
自动化测试及结果：识别阶段备份一次、录音期间不备份、收尾丢弃、识别中取消丢弃、短录音不备份、耗时写入度量。通过。
  PasteboardSnapshot 与 changeCount 的比较逻辑依赖真实 NSPasteboard，未单测。
真机验证：未验证。手工步骤：剪贴板里放一张大图 / 文件后在会走粘贴兜底的应用（如终端）里听写，确认听写后原剪贴板被恢复，
  对比 clip_backup 与 insert 的耗时；若提前快照本身也很慢，再评估移到后台线程（NSPasteboard 跨线程读取须先确认安全）。
与本规格的差异及理由：仍有 pendingRestore 时直接不备份（此刻剪贴板里是我们写的临时文本，备份它没有意义）。
Windows 影响：已有（§22.2）。
```

#### R8-B3 卡键看门狗 + 锁屏 / 睡眠处理

```text
编号：R8-B3
改动后的行为：(1) 按住模式录音期间每 250ms 读一次热键的物理状态（Fn：maskSecondaryFn；单独修饰键：设备相关的左右位；
  组合键：所需修饰键，不检查主键），连续两次判定已松开 → 按松键处理并调用 HotkeyService.resetActiveState()（在 worker
  线程复位激活态与识别器）；toggle 模式不轮询。(2) AppCoordinator 订阅 willSleep / sessionDidResignActive /
  com.apple.screenIsLocked：录音阶段静默丢弃（outcome=interrupted，不弹「已取消」），识别阶段不动；订阅 didWake /
  sessionDidBecomeActive / com.apple.screenIsUnlocked：控制器在跑且无进行中的听写时重新安装 tap（suspend + resume），失败走
  既有就绪重评；1 秒内的连串通知只重装一次。
接口 / 配置 / 度量字段变更：Services/HotkeyPhysicalState.swift；HotkeyListening.resetActiveState()；
  HotkeyEventFilter / ModifierTapRecognizer 的 reset()；VoiceTyperController.interruptRecording()；
  DictationMetrics.Outcome.interrupted。
自动化测试及结果：物理状态三种热键类型；两次判定已松开 → 结束录音、复位、文本上屏；从没观察到「按着」不动作；toggle 不轮询；
  interrupted 结局静默且不进历史；识别阶段不受打断；filter.reset()。通过。
真机验证：未验证。手工步骤：按住热键说话时合盖 / 锁屏 → 不应残留录音与麦克风指示点；唤醒后热键立即可用；
  人为制造丢失松开事件（如按住时快速锁屏再解锁）后下一次按下正常。CGEventSource.flagsState 是否包含设备相关位、
  被吞的组合键主键是否影响物理状态（规格标 V）都需真机确认。
与本规格的差异及理由：看门狗只在本次录音「至少观察到一次物理上按着」之后才会判定卡键。这条信号在某台机器 / 某类热键上若不可靠
  （始终读到未按下），不加这个保护会在 500ms 后误杀每一次正常录音——「不丢字」优先；代价是用户在第一个 250ms 轮询前就
  卡键的极端情形救不了（短录音本身也由 300ms 过滤处理）。
Windows 影响：已有（B3 / B4）。
```

#### R8-C4 操作提示

```text
编号：R8-C4
改动后的行为：toggle 模式录音状态行追加「 · 再按 %@ 结束」；hold 模式前 5 次录音追加「 · Esc 取消」，计数存
  UserDefaults（hint.escCancelShownCount）；浮窗设为「不显示」时不计数。带提示时设备名显示上限由 14 收紧到 8 个字符。
  「减少动态效果」下文案不变。
接口 / 配置 / 度量字段变更：Support/RecordingHint.swift；showHUD(inputDeviceName:hint:)；recordingStatus(inputDeviceName:hint:)。
自动化测试及结果：两种模式的提示文本与次数上限；带 / 不带提示的状态行与设备名截断。通过。
真机验证：未验证（规格要求中英文案都测宽度：长设备名 + 「Press … to finish」是否把计时挤出可视区，需肉眼确认，必要时再收紧上限）。
与本规格的差异及理由：无。
Windows 影响：部分。
```

#### R8-C5 下载进度详情 + 就绪提示

```text
编号：R8-C5
改动后的行为：设置「识别」模型卡片、引导「语音模型」一步、菜单头副标题显示「已下载 X / Y · N% · Z/s，约剩 T」；速度取最近 8s
  窗口，不足 1s 或无进展只显示前半段。下载完成且模型就绪后，引导和设置窗口都不可见且权限齐全时，浮窗 3s「语音模型已就绪 ·
  按住 %@ 说话」（toggle：「按一下 %@ 开始说话」）；权限未齐不提示。自动重试用尽、窗口都不可见时浮窗提示一次失败并指向
  「设置 → 识别」。两条提示不受「不显示」影响。
接口 / 配置 / 度量字段变更：Support/DownloadStats.swift（总量取 ModelDownloader.totalBytes）；RecordingHUDController.showNotice(_:)；
  Setup / Onboarding 的 updateStatus 新增 downloadDetail 参数。
自动化测试及结果：速度窗口（跨度、无进展、8s 滑出）；文本两种形态与剩余时间；分钟格式；进度钳制；总量等于文件清单之和。通过。
真机验证：未验证（需实际下载一次：观察三处文案刷新、窗口都关闭后完成时的浮窗、断网到重试用尽时的浮窗）。
与本规格的差异及理由：无。
Windows 影响：已有（E3、F-01）。
```

#### R8-G1 菜单栏快捷切换

```text
编号：R8-G1
改动后的行为：「暂停听写」下方新增「识别语言」（自动 / 中文 / 英文 / 粤语 / 日语 / 韩语）、「麦克风」（自动 / 跟随系统；配置里是
  指定设备时只显示一个选中的「指定设备」）、「智能校对」开关项（未填 Base URL 时禁用，标题为「智能校对（在设置中配置）」）。选择后经
  AppCoordinator.applyConfig 保存（与设置页同一事务）；听写进行中这些项禁用。
接口 / 配置 / 度量字段变更：StatusBarController 新增 QuickSettingsState / quickSettingsProvider / onSelectLanguage /
  onSelectInputDevice / onToggleLLM；无配置字段变更。
自动化测试及结果：菜单与 NSMenu 构建无自动化覆盖（与现有菜单一致），applyConfig 路径沿用既有测试。构建通过。
真机验证：未验证（逐项切换后确认设置窗口同步、识别语言在下一次听写生效、听写中项目呈禁用）。
与本规格的差异及理由：规格里「麦克风」的设备列表依赖 R8-G2（第三批），本批只有「自动 / 跟随系统」两项。
Windows 影响：可选。
```

#### R8-H1 更新后重新授权引导

```text
编号：R8-H1
改动后的行为：UserDefaults 记录 lastLaunchedVersion 与 permissionsWereAllGranted。启动时版本号变化、上次权限齐全而这次不齐、
  且签名确为 ad-hoc / 未签名（Developer ID 不显示）→ 设置「权限」页顶部显示横幅，权限补齐后消失；每项权限已有「系统设置」按钮。
接口 / 配置 / 度量字段变更：ReauthorizationNotice（shouldShow / bannerText）；SettingsViewModel.reauthorizationNotice。
自动化测试及结果：显示条件六种组合；横幅文案含版本号。通过。
真机验证：未验证（需要真实的 ad-hoc 更新：旧版授权后安装新版，权限被系统判失效）。
与本规格的差异及理由：横幅显示后把 permissionsWereAllGranted 置 false，避免下一次版本更新前重复提示（权限补齐后重新置 true）；
  横幅只放在设置的权限页，引导（首次使用）里没有。
Windows 影响：不需要。
```

### 第三批（部分）与第四批（部分）：R8-E2 · R8-E3 · R8-D2b · R8-D3

**范围说明**：第三批中只做 E2、E3、D2b，第四批中只做 D3；其余（F1、G2、E1、F3、A3b、A4，G3–G5）未做。**版本号保持 3.5.2**。
验证口径同前：全量 `xcodebuild … test` 380 个通过（连跑三次无波动）；浮窗位置、辅助功能坐标、补录时序、淡显观感
**均未在真机验证**。

#### R8-E2 浮窗落在焦点窗口所在屏

```text
编号：R8-E2
提交：本批提交（feat(macos): R8 第三批（E2/E3/D2b）与第四批（D3））
改动后的行为：浮窗出现时读取前台应用 kAXFocusedWindow 的位置与尺寸，换算成 Cocoa 坐标（y = 主屏高度 - axY - 高度），
  落在窗口中心所在的屏（中心不在任何屏内时取相交面积最大者）；取不到回落鼠标所在屏。只在浮窗出现时计算一次并缓存到本次
  听写（或一次性提示）结束，听写中焦点变化不会让浮窗跳屏。
接口 / 配置 / 度量字段变更：新增 Services/FocusGeometry.swift（纯计算 cocoaRect / screenIndex + AX 读取 focusedWindowFrame）；
  RecordingHUDController 缓存 anchorScreen。无配置、无度量字段。
自动化测试及结果：FocusGeometryTests：坐标换算（含副屏在主屏上方、往返不变）、选屏（中心点 / 最大相交面积 / 无相交）。通过。
真机验证：未验证。手工步骤：双屏下把文本编辑窗口放在副屏、鼠标留在主屏，听写 → 浮窗应出现在副屏；副屏在主屏上方 / 左侧
  各试一次；全屏应用与 Stage Manager 下各试一次。
与本规格的差异及理由：AX 查询加 250ms 消息超时（与 R8-B2 相同的理由：在主线程上做，目标应用无响应时不能卡数秒）。
Windows 影响：不需要。
```

#### R8-E3 「跟随光标」改为跟随插入点

```text
编号：R8-E3
改动后的行为：配置值仍是 near_cursor（兼容旧配置），显示名改为「跟随输入位置」，帮助文字说明「取不到输入位置时跟随鼠标」。
  取焦点元素 kAXSelectedTextRange → kAXBoundsForRange（长度 0 取不到时依次试选区后一个字符、前一个字符的后沿），换算为
  Cocoa 坐标；浮窗放在插入点下方 8pt，下方放不下（低于可见区下沿加边距）翻到上方，再做左右夹逼；落屏取插入点所在屏。
  插入点不可信（高度为 0、超过 400pt、不在任何屏内、含非有限值——自绘 / Electron 控件常见）按取不到处理，回落到鼠标
  位置与鼠标所在屏（原行为）。同样只在浮窗出现时计算一次。
接口 / 配置 / 度量字段变更：FocusGeometry.caretRect / isPlausibleCaret / originNearCaret；HUDPosition.nearCursor 显示名与
  帮助文案（含英文）。无配置字段变更。
自动化测试及结果：插入点可信度（全零、离屏、整块控件矩形、NaN、零宽正常插入点）；定位在下方有空间 / 无空间时的位置。通过。
真机验证：未验证。手工步骤：TextEdit、备忘录、Safari 文本框、Chrome contenteditable、VS Code、终端、Xcode 各听写一次，
  记录插入点是否取得到、浮窗是否贴着输入行；屏幕下沿附近输入时应翻到上方；取不到的应用应回落到鼠标。
与本规格的差异及理由：规格写「长度 0 取不到时试长度 1」，实现多加了一档（前一个字符的后沿），因为光标在文末时「后一个
  字符」没有范围、很多控件此时也给不出矩形。
Windows 影响：已有（§22.3）。
```

#### R8-D2b 松键后补录约 150ms

```text
编号：R8-D2b
改动后的行为：hold / toggle 正常松键（结束）时，浮窗立即切「识别中」，audioCaptureService.stop() 延后 150ms；期间 Esc
  仍可取消（走「录音中取消」，丢弃采集，延迟的 stop 作废）；设备变化、达到上限、卡键看门狗触发的收尾不补录；短于 300ms 的
  误触仍立即丢弃；补录期间 toggle 再按一次无效。release_to_finalize 相应增加约 150ms，属预期。
接口 / 配置 / 度量字段变更：VoiceTyperController.init(releaseTailCapture:)（默认 0.15，设为 0 关闭）；
  无配置字段、无新度量字段（tail_speech 仍记松键那一刻的状态）。
自动化测试及结果：松键即宣布「识别中」且只宣布一次、采集延后停止、Esc 取消后延迟 stop 作废、toggle 补录期间再按被忽略、
  设备变化立即 stop 且不重复、卡键看门狗不补录、短录音仍立即丢弃。通过（测试里默认关闭补录以保持既有用例的时序，补录用例显式开启）。
真机验证：未验证。手工步骤：快速说「…好的」并在最后一个字刚出口时松键，对比开关补录前后末字是否被截；确认 release_to_done
  增加量符合预期（约 +150ms）；补录期间按 Esc 不上屏。
与本规格的差异及理由：**规格把 D2b 列为数据驱动项**，门槛是 R8-D2a 的 tail_speech=1 占比 ≥ 20%；目前没有任何真机数据，门槛
  尚未满足。本批是应你的明确要求实施的，因此默认开启；若收集 tail_speech 后占比低于门槛，把 defaultReleaseTailCapture 改为 0
  即可整体关闭（一处常量）。
Windows 影响：已有判据（A6 未做）。
```

#### R8-D3 预览尾部变化淡显（第四批）

```text
编号：R8-D3
改动后的行为：每次预览刷新，与上一次预览的公共前缀之后的部分先以次级色显示约 300ms，再变回常规色；每次刷新重新计时。
  「减少动态效果」下不做。
接口 / 配置 / 度量字段变更：RecordingHUDController.commonPrefixLength / highlightStart（分叉点按首部截断平移）；
  previewHighlightDuration=0.3。无配置、无度量字段。
自动化测试及结果：公共前缀长度；分叉点换算（无截断 / 钳制 / 首部截断平移 / 分叉点落在被截掉部分）。通过。着色本身（NSAttributedString
  与标签属性的切换）无自动化覆盖。
真机验证：未验证（深浅色、两行预览、首部截断、减少动态效果下的观感）。
与本规格的差异及理由：第一次出字（没有可比较的上一次）与文本没有变化时不淡显，避免整句首次出现时闪一下次级色。
Windows 影响：已有（C1）。
```

### 规格外：浮窗预览排版修复

> 宽度档位（480 / 680）与「开头加省略号截断」已被下一节「浮窗宽度策略与整行滚动」取代；本节的约束与高度修复仍然有效。

```text
起因：用户反馈录音预览时浮窗下方总空着一到两行，长句只在一行里向左推，像没打磨完。
原因：
  1. R6-02 的「最多两行」从未生效。预览标签的横向约束仍是早期单行滚动的写法（左边 `<=` 预览区、横向强制贴合内容），
     而换行 NSTextField 在未设 preferredMaxLayoutWidth 时，固有宽度是「整段排成一行」的宽度（实测长句 2662×17pt）。
     标签于是被撑成一条向左伸出的单行，左侧（连同前导省略号）被预览区裁掉；tailFitting 却按两行计数，窗口照样长到两行高。
  2. 窗口高度手写（展开 100 / 122、预览区 22 / 44），没按行高（14pt 字体 17pt）推导：一行时预览下方空约 37pt，
     「两行」时空约 59pt。
  3. 修好 1 之后会暴露的隐患：NSTextField 左右各有 2pt 行片段内边距，实际换行宽度比标签窄 4pt；tailFitting 与加宽计算
     都按标签宽度量，最后一个词会折到被裁掉的下一行（600 组中英混排样本中 16 组不一致，扣 4pt 后 0 组）。
改动后的行为：
  - 标签与预览区四边对齐，预览真正按多行排版；最多 3 行，从开头截断，前导省略号可见。
  - 窗口高度 = 顶部 10 + 状态行 28 + 间距 6 + 行数 × 行高 + 底部 14：一 / 二 / 三行分别为 75 / 92 / 109pt。
  - 预览改为左对齐（多行时右对齐首行左缘参差）。
  - 宽度从 480pt 起步，随文字加宽，封顶 680pt（窄屏仍受屏宽一半约束）；原为 850pt 起步、封顶屏宽一半。
    收窄后用第三行补回容量，浮窗面积与原先 850pt 单行相当。
接口 / 配置 / 度量字段变更：RecordingHUDController 新增 fitPreview / preferredHudWidth / expandedHeight / previewHeight /
  lineHeight(of:) / configurePreviewLabel；无配置、无度量字段。
自动化测试及结果：HUDLayoutTests 新增三项——按浮窗同一配置的标签实测「算出的行数 = 实际绘制行数」（480 / 680 两种宽度、
  逐字增长的中英混排）；加宽后的宽度确实排得下一行；预览下方留白不足一行、每多一行正好长一行高。全量 260 项通过。
  另用临时测试把真实浮窗渲染成图片目视确认过一 / 二 / 三行与截断的排版（未提交）。
真机验证：未验证（中英混排长听写、深浅色背景、四种浮窗位置、跟随输入位置贴近屏幕下沿时、减少动态效果）。
与本规格的差异及理由：R8-E1（紧凑态小胶囊，约 240–300pt）仍未实施；起步宽度从 850pt 降到 480pt 只是本次修复的副产品，
  480pt 由状态行最长情形（设备名 + 操作提示 + 计时）决定。
Windows 影响：Windows 浮窗自己折行、逐行绘制、高度按行数推导，没有同类问题；宽度封顶 680、三行与按词换行已同步，
  见 windows/DESIGN.md §28。
```

### 规格外：浮窗宽度策略与整行滚动

```text
起因：修复后起步 480、随文字逐字加宽到 680，用户觉得偏宽；长句超出三行时逐字丢开头再加省略号，所有行的断行位置
  每来一个字都会变，文字像蛇一样往前爬。
改动后的行为：
  - 宽度两档：起步 400（状态行实测更宽时放宽，至多 460）；预览一行放不下时一次跳到 600，之后换行。中间档比起步只宽
    几个字，多跳一次换不来什么，故不设。窄屏仍受屏幕宽度一半约束。
  - 一次显示期间宽度只增不减（widthFloor）：预览被修正变短、短暂清空、紧接着的「已输入」提示都不回缩；浮窗隐藏或
    开始新一次听写时归零。浮窗从中心向两边变宽而文字左对齐，逐字跟随时每次刷新文字都会整体左移，现在整个过程至多一次。
  - 状态行变长（「校对中… · 再按 X 跳过」）时按同一规则加宽。
  - 高度：变高立即生效；录音 / 识别中行数减少要持续 0.6s 才收矮，期间回升则作废；预览清空时宽高不动，只等原有的
    防抖收起，收起后才回到一行。
  - 超过三行按整行滚动：全文从头按词换行（NSLayoutManager，换行规则与预览标签一致），只显示最后三行。第四行出现的
    同时第一行整行消失，已显示的行断行不变。不再加前导省略号。尾部淡显的分叉点按滚出去的字符数平移。
接口变更：tailFitting → lastLines / lineStartOffsets；preferredHudWidth → singleLineHudWidth；新增 compactWidth /
  targetWidth；highlightStart 改为按 fittedCount 计算；defaultHudWidth 删除。无配置、无度量字段。
自动化测试及结果：HUDLayoutTests 9 项——只显示最后几行且是原文后缀；逐字增长时显示起点只落在原先的行首、起点不变期间
  已显示的首行不变；排版行数 = 标签实际绘制行数（400 / 600 两种宽度）；单行宽度确实排得下一行；高度推导；宽度档位；
  真实浮窗上宽度只增不减、行数减少后延迟收矮。全量 263 项通过。另用临时测试渲染真实浮窗目视确认紧凑态、一行、
  三行（未提交）。
真机验证：未验证（长听写的整行滚动观感、识别修正时宽高是否还会跳、四种位置下一次加宽的观感、深浅色、减少动态效果）。
与本规格的差异及理由：R8-E1 规格要的是按内容约 240–300pt 的小胶囊；按用户决定起步定为 400，状态行实测宽度只用于放宽。
Windows 影响：同一套参数已同步，见 windows/DESIGN.md §29。
```

### 与规格 §2 已定决策的关系

第一、二批没有改动 §2 的任何已定决策（R8-D-1…D-6）。实施中的偏离均为「不丢字、不打扰」方向的最小调整，已逐项写在上面各条的「差异」里。

---

## 11.6 R9：架构与实现审查修复（2026-10-19，随 3.6.0 发布）

一轮针对设计合理性、健壮性与关键路径性能的代码审查，编号 `R9-xx`。识别契约（fbank / LFR-CMVN / CTC / 文本后处理 /
模型 I/O）与共享夹具均未改动。

**验证口径**：本轮在 Linux 上完成编码，**未经 macOS 编译与运行**；单元测试已按新接口更新并补充（见各项），需在 macOS 上跑
全量 `xcodebuild … test` 后再定稿。涉及 AX、剪贴板、热键、下载的行为**均未在真机验证**。

### 改了什么

| # | 问题 | 改动 | 落点 |
| --- | --- | --- | --- |
| R9-01 | 只改 API Key 时配置看起来没变，协调器不重建控制器，校对器一直拿旧 Key；「测试校对」用的是输入框里的新 Key，会通过，界面也提示「已保存并生效」 | 协调器记住构建控制器时读到的 Key（`appliedLLMAPIKey`），保存时 Key 变了也重建（与 Windows `SaveRecognition` 的 `keyChanged` 一致） | `AppCoordinator.requiresControllerRebuild` |
| R9-02 | 是否重建控制器只比较 asr / llm / hotkey，漏了 `audio`：菜单栏「麦克风」快捷切换与设置里的输入设备改动要到重启才生效 | 重建判定加入 `audio` | 同上 |
| R9-03 | `AXFocusInspector` / `FocusGeometry` 在 system-wide 元素上设 0.25s 超时，按 AX 头文件语义这改的是**整个进程**的默认超时，插入文字也被压到 0.25s。忙碌的应用没及时回复 `kAXSelectedText` 写入时，代码当作失败去粘贴，而那条写入稍后仍被处理——同一段文字可能出现两次（依据 API 语义推断，未真机复现） | 新增 `AXMessaging`：进程默认超时只在启动时设一次（1s），探测类查询在具体元素上设 0.25s，插入设 1.5s；AX 写入返回 `kAXErrorCannotComplete` 视为「未确认」（`TextInsertionResult.unconfirmed`），不再粘贴，只复制到剪贴板并说明 | `TextInsertionService.swift`、`FocusInspector.swift`、`FocusGeometry.swift` |
| R9-04 | 空识别结果时控制器先发 `onEmptyRecognition`，紧接着又发 `onStateChange(.idle)`，协调器在后者里 `hideHUD()`，「没有识别到内容」提示一出现就被收起（B4 实际失效）。根源是十几个回调之间的隐式顺序契约 | 控制器对外改为唯一出口 `onEvent(Event)`；结局 `finished(Completion)` 每段恰好一次，带上屏文本与校对说明，协调器不再靠 `pendingInsertionNote` 之类拼接先后到达的回调 | `VoiceTyperController.Event / Completion`、`AppCoordinator.handleControllerEvent` |
| R9-05 | 协调器的 `currentState` 同时承载"就绪度"与"听写阶段"，约 10 处写入；`computeTargetState` 需要传 `previous` 才不覆盖进行中的听写；模型下载失败会无条件覆盖它 | 拆成 `readiness`（纯函数 `computeReadiness`）、`dictationPhase`（只由控制器事件写入）、`dictationError`（2.5s 回落）；显示状态由 `displayState` 合成；是否拆掉进行中的听写由 `shouldInterruptDictation` 判定（只有"加载与录音并行"可共存）。`reevaluateReadiness()` 改为同步（原先声明为 async 却没有任何 await，所有调用方都要套一层 Task） | `AppCoordinator` |
| R9-06 | 尾音回调用的是"当前" `active` 而不是发起它的那段听写：Esc 取消后立刻再按热键，迟到的尾音会把新录音推进到「识别中」，松键被忽略，麦克风一直开到 120 秒上限（低概率竞态） | 单段听写的全部状态（预览兜底文本、电平、静音探测、卡键看门狗）移进 `Utterance`，每段带 `id`；所有会话 / 采集回调先确认 `id` 仍是当前听写 | `VoiceTyperController` |
| R9-07 | 回调线程切换混用 `Task { @MainActor }` 与 `DispatchQueue.main.async`（热键路径先 main.async 再套一层 Task） | 热键回调本就在主线程，直接 `MainActor.assumeIsolated` 同步处理；采集回调经 `DispatchQueue.main.async` 投递（严格先进先出）；卡键看门狗加入 common 模式（菜单展开时也轮询）；短录音判定改用注入的单调时钟 | 同上 |
| R9-08 | 松键后（150ms 补录期间、送入尾音时）仍会发起预览，结果注定被丢弃，却占住串行的 asrQueue，终稿要排在它后面（`final_wait`） | `LocalASRSession.stopPreviews()`：松键与采集结束时调用，已入队未开跑的预览也跳过 | `LocalASRSession`、`VoiceTyperController` |
| R9-09 | 预览期间持有整段音频快照，主线程每 200ms 追加一次就触发一次写时复制，在主线程上拷贝整段音频（120s 时 7.7MB），抵消了 R6-07 预留容量的初衷 | 预览只在锁内拷贝尚未固化的那一段；终稿时采集已停，仍整段共享 | `RecognitionBuffer` |
| R9-10 | AX 插入回读在主线程同步 `sleep(50ms)`，写入前后还各读一次整段值（长文档是一次跨进程整篇拷贝）——只为一条度量，却在「松键 → 出字」关键路径上 | 回读改为比较写入前后的 `kAXNumberOfCharacters`（预期变化 = 插入长度 − 被替换选区长度），50ms 后异步完成；度量行随之晚约 50ms 输出 | `TextInsertionService.verifyLastInsertion`、`InsertionVerification.classify` |
| R9-11 | 每次听写都在识别阶段完整快照剪贴板，而最常见的 AX 路径根本用不上；逐类型读取还会迫使来源应用当场生成延迟提供的数据（大图、表格选区、文件承诺） | 识别阶段先做一次便宜的 AX 探测（与插入路径同序：`kAXSelectedText` 可写 → 不备份；网页控件或不可写 → 备份），只在预判要粘贴时备份；预判错了由粘贴路径当场补快照 | `TextInsertionService.prepareForInsert` |
| R9-12 | `config.yaml` 读不出来时应用停在「配置加载失败」，热键、引导、设置都不启动，而菜单里早已没有打开配置目录的入口（B9），用户无从自救 | 另存为 `config.yaml.broken`（覆盖上一份）、回落默认配置，启动后弹一次说明；原文件保持不动，直到用户在设置里保存（与 Windows W5-04 一致） | `ConfigStore.loadOrCreate`、`AppCoordinator.presentBrokenConfigNotice` |
| R9-13 | 分段下载每段是一次完整请求，断网时这次已收到的字节随临时文件丢弃，分段文件只在请求完整结束后才增长，"按已有字节续传"实际是整段（约 60MB）重来；且一次失败就整体回落单连接、从 0 开始 | 每段按 8MB 分块请求、每块完成即追加落盘；单块网络失败本地重试 2 次（1s / 2s 退避），状态码或区间不对才放弃分段 | `ModelDownloader.runSegments` |
| R9-14 | 校对器自建的 URLSession 从不失效（每次重建控制器、每次「测试校对」各留一个）；`timeoutIntervalForRequest` 只是空闲超时，不是总时长 | 自建会话在释放时 `finishTasksAndInvalidate()`；补设 `timeoutIntervalForResource` | `LLMCorrector` |
| R9-15 | 若干注释与现状不符（「启动时不预加载」是默认、菜单有「打开配置目录」、600ms 采集块、"逻辑原样搬运"） | 改为描述现状 | 各文件 |

接口变更：`VoiceTyperController` 的十余个 `onXxx` 回调合并为 `onEvent`；`TextInserting.lastInsertVerification` →
`verifyLastInsertion(_:)`；`TextInsertionResult` 新增 `.unconfirmed`；`InsertionVerification.classify` 改为按字符数；
`AppCoordinator.computeTargetState` → `computeReadiness` + `shouldInterruptDictation` + `displayState` +
`requiresControllerRebuild`；`LocalASRSession.stopPreviews()`；`ConfigStore.lastLoadFellBackToDefaults`；
`AppState.swift` 新增 `DictationPhase`。无配置字段变更。

度量字段语义变更：`insert_verify` 改为按字符数判定；`clip_backup` 在预判走 AX 时为 `-`（没有备份）；走 AX 插入时
整行度量晚约 50ms 输出。字段顺序不变。

自动化测试（新增 / 改写）：控制器测试改用事件记录器，新增空结果只收尾一次、迟到尾音不影响下一段、松键后不再预览、
度量等待回读、未确认插入不重试共 5 项；`AppCoordinatorReadinessTests` 改写为就绪度 / 打断判定 / 显示合成 / 重建判定
四组；`LocalASRSessionTests` 新增停止预览 2 项；`ConfigStoreTests` 新增坏配置回落 1 项；`TextInsertionServiceTests`
新增回读判定 1 项；`SegmentedDownloadTests` 新增单块失败重试 1 项。编写时均未在 macOS 上运行；2026-10-23 随 3.6.0 在
macOS（Apple M4）上 `xcodebuild … test` 全量通过（332 项，1 项按设计跳过，见 §11.7）。

### 刻意没做的（性价比不高或另有取舍）

- **测试去掉 sleep**：控制器测试仍有约 50 处按墙钟等待。热键与采集回调已改为确定的主线程投递，进一步把
  `asyncAfter` / `Timer` 抽成可注入的调度器才能彻底去掉等待，改动面大、收益只在测试时长，暂不做。
- **拆分大文件**（`RecordingHUDController` 约 1500 行、`AppCoordinator` 约 1250 行）：新增文件需要在 macOS 上重新生成
  工程，本轮在 Linux 上无法验证，留待下一轮（可参照 Windows 第五轮的 `ModelDownloadOrchestrator` 拆法）。
- **fbank 增量缓存**、**校对请求预热 TLS 连接**：前者在推理占大头时收益有限，需 profiling 证明；后者会在用户开口时
  就联网，与"不说话不联网"的直觉相悖，暂不做。
- **听写中暂停推迟到收尾后**（Windows W5-01）：macOS 菜单在听写进行中本就禁用「暂停」，不存在丢内容的路径。
- **注释里的历史叙述**（"此前…"）只清理了与现状矛盾的几处，其余保留，评审编号可在本文反查。

### 待真机验证

1. **R9-01 / R9-02**：开启智能校对，填错 API Key 听写一次（应提示鉴权失败）；改成正确 Key 保存后立即再听写，应完成校对，
   无需重启。菜单栏「麦克风」在「自动 / 跟随系统」间切换后立即听写，浮窗状态行的设备名应随之变化（戴蓝牙耳机时最明显）。
2. **R9-03**：在 TextEdit、备忘录、Safari、Chrome、VS Code、Slack、微信、终端里各听写一次，确认只出现一次文字；
   让目标应用繁忙（例如 VS Code 打开大工程正在索引）时听写，确认不会出现两次，未确认时浮窗提示并已复制到剪贴板。
   `log stream … category == "app"` 中「AX 写入超时未得到回复」的出现频率需要记录。
3. **R9-04**：对着静音的麦克风按住热键 1 秒再松开，「没有识别到内容」提示应停留约 2.5 秒。
4. **R9-06 / R9-08**：按住热键说话、松开后立即按 Esc、再立即按住说下一句，第二句应正常出字；对比改动前后
   `final_wait`（同一段 20s 口述，松键时仍在说话）。
5. **R9-10 / R9-11**：剪贴板里放一张大图后在 TextEdit（AX 路径）听写，`clip_backup` 应为 `-`、原剪贴板不变；在终端
   （粘贴路径）听写，`clip_backup` 有值、原剪贴板被恢复。`insert_verify` 在 TextEdit 应为 `ok`。
6. **R9-12**：退出应用，把 `config.yaml` 改成非法 YAML（如 `asr: [`）后启动：弹出说明，热键可用，目录里出现
   `config.yaml.broken` 且内容与改坏的文件一致；在设置里保存后 `config.yaml` 恢复为合法内容。
7. **R9-13**：删除模型后开始下载，中途断网 10 秒再恢复，应从断点继续而不是整段重来；下到一半退出应用再打开，应从
   各分段已有长度续传。

### Windows 影响

识别契约与共享夹具未变。R9-01 Windows 已有（`SaveRecognition` 的 `keyChanged`），R9-12 已有（W5-04）。
其余需要 Windows 侧另行评估：R9-08（`LocalAsrSession.SchedulePreview` 同样只看 `_isFinalizing`，送入尾音时仍会排一次预览）；
R9-09 不适用（Windows 的 `RecognitionBuffer` 用定长数组加计数，无写时复制）；R9-03 / R9-10 / R9-11 是 macOS AX 专属。

## 11.7 自定义词汇（VOC，2026-10-22，3.6.0）

按 [`docs/VOCABULARY_SPEC.md`](docs/VOCABULARY_SPEC.md) 四个阶段全部实施：配置（VOC-1）、识别层偏置（VOC-2–6）、
替换层（VOC-7）、LLM 词表（VOC-8）、设置页与「从最近结果添加」（VOC-9）、度量（VOC-10）、测试与离线评估工具（VOC-11），
以及 Windows 移植（阶段 4；Windows 设置页 2026-10-23 补齐，见下文「Windows 影响」）。

**验证口径**：实施在 Linux 上完成；2026-10-23 在 macOS 上补做编译、全量测试与真机听写（见「macOS 编译与真机验证」）。

- **已运行**：用 Swift 6.1.2 Linux 工具链（Swift 6 语言模式）编译并运行不依赖 AppKit / Accelerate / ORT 的部分——
  `HotwordCompiler`、`ContextGraph`、`CTCPrefixBeamSearch`、`VocabularyReplacer`、`OutputFormatter`、`VocabularyConfig`
  及其 XCTest（34 项），另把 `LocalASRSession`、`LLMCorrector`、`DictationMetrics`、`SettingsViewModel`、`AppConfig`、
  `ConfigStore`（真实 Yams）在桩之上编译并运行了与本功能相关的用例（全部通过）。`SenseVoiceEngine`、`ASRService` 在 ORT /
  前端桩之上通过了类型检查。
- **独立参考实现**：同一算法另写了 Python 版，对 23 个写法的编译结果、对 `speech_zh_en_mixed.wav` 真实 `ctc_logits`
  （本机 ONNX Runtime 1.31 跑出）的 12 组解码结果，Swift 与 Python 逐 token 相同；共享夹具的期望值同样由它核对。
- **实施时未做**（2026-10-23 已补做前两项）：没有在 macOS 上编译与跑 `xcodebuild … test`，没有真机听写，没有用真实录音
  校准参数。

### 实施结果

| 编号 | 落点 |
| --- | --- |
| VOC-1 | `AppConfig.vocabulary`（`VocabularyConfig` / `VocabularyEntry` / `VocabularyBias`），`ConfigStore` 写回 `vocabulary` 段 |
| VOC-2–6 | `ASR/Hotword/HotwordCompiler.swift`、`ContextGraph.swift`、`CTCPrefixBeamSearch.swift`；`SenseVoiceRecognizing.setVocabulary` / `lastDecodeStats`；`ASRService.updateVocabulary` / `vocabularyReport` |
| VOC-7 | `Text/VocabularyReplacer.swift`；`LocalASRSession.completeWithASRText` 与 `OutputFormatter.formatForOutput(_:vocabulary:)` 各调用一次 |
| VOC-8 | `LLMCorrector.correctWithReport(_:glossary:)`、`userMessage(_:glossary:)`；三份 `correction.md` 同步新增一条规则 |
| VOC-9 | `UI/Settings/VocabularySettingsView.swift`（表格、强度、试一试、从最近结果添加 sheet）、`SettingsViewModel` 词汇段、`SetupTab.vocabulary` |
| VOC-10 | `ASRSessionTimings.decodeMode / hotwordHits / decodeNanos`，度量行末尾追加 `decode_mode vocab_hits decode_ms` |
| VOC-11 | `HotwordCompilerTests`、`ContextGraphTests`、`CTCPrefixBeamSearchTests`、`VocabularyReplacerTests`、`SettingsViewModelVocabularyTests`、`HotwordEvaluationTests` 与既有测试的补充；共享夹具 `vocabulary_cases.json`（27 例）、`ctc_hotword_cases.json`（11 例） |

词汇变化走「重建控制器」路径（`requiresControllerRebuild` 加入 `vocabulary`）：替换层在控制器构造时编译，听写中保存会被
拒绝，设置页在听写中整体禁用。

### 与规格的差异（均已回写规格）

| # | 规格原文 | 实施 | 依据 |
| --- | --- | --- | --- |
| 1 | §5.2：只有「汉字之后」的 ASCII 段两种 `▁` 都生成 | **写法开头**的 ASCII 段也两种都生成；变体顺序改为每种大小写 / 拆词形式先带 `▁` 再不带 | 真实 logits：中文句子里英文词跟在汉字或全角标点后时模型不输出 `▁`（`，audio▁driven▁text`）。只按规格生成时，`Audio` 等词在中文语境里根本匹配不上 |
| 2 | VOC-D6：只有 1 个 token 的序列丢弃 | 写法的**原样合写**变体本身就是 1 个 token 时，整个写法记为 `tooShort`、不参与偏置 | 真实 logits 上 `issues`（`▁issues` 是单 token）的首字母大写变体 `▁I s s ue s` 在强档把 `isue` 改成了 `▁issuesue`。模型能直接输出的词，偏置别的大小写只会制造误改 |
| 3 | §5.4 候选集 = TopK ∪ 热词首 token ∪ 当前热词下一 token | 另加入**前缀末 token**（停留），同样受门限约束；停在图根上的假设不再重复检查热词首 token | 前者保证门限内的「停留」不会因为没进前 K 名而被剪掉（贪心的 argmax 总在 TopK 里，对夹具无影响）；后者是纯优化，结果不变（停在根上时「下一个 token」就是全部热词首 token，本帧公共候选里已有） |
| 4 | §5.4 每帧线性扫描 | 按 256 个 token 分块，先求块最大值，整块在门限外就跳过 | 门限内的 token 每帧通常只有个位数；分块后扫描成本从逐个比较 25k 次降到约 100 次向量化求最大值 |
| 5 | §6.2 一次性拼出结果、不链式替换 | 一轮内仍不链式；**整轮重复到不动点**（最多 4 轮）；同起点同长度时 **term 模式优先于 alias 模式**；汉字 alias 换成英文 term 时若与相邻英文粘连，补一个空格 | 单轮无法保证幂等：`克劳德code` → `Claude code`，第二轮才被 `Claude Code` 统一（处理链里替换跑两次，结果会不一致）。term 优先保证已是某个 term 的文本不会被别的词条的 alias 再改（「不链式替换」的本意）。粘连：`克劳德code` 单轮会变成 `Claudecode` |
| 6 | §4.2 校验 | 另把写法内部的换行、制表等空白收敛为一个空格 | 写回 YAML 时不必转义控制字符；词条本来就不该含换行 |
| 7 | §5.1 `setVocabulary` 在 `bias == .off` 时置空 | 置空图，但**照常编译并返回报告** | 设置页在「关」时仍能显示哪些词条只做替换 |

### 真实 logits 上的观察（证据 S，参数仍是 E）

对 `speech_zh_en_mixed.wav`（TTS 合成，`你好，欢迎使用语音输入法……isue编号是123，版本号是251。`）：

- 50 条无关词汇在轻 / 标准 / 强三档下，束搜索结果与贪心**逐 token 相同**（Swift、Python、C# 三方一致）。没有触发
  §5.8 的「贪心锚定」退路。
- `一二三`（原文 `123`）：三个字的声学分差合计约 16.4，大于强档的 `3b = 7.5`，**不采用**——符合「没听到的不硬塞」。
- `Audio` / `i自动`：命中在贪心路径上已有的写法（`au d io`、`I自动`），输出不变，由替换层统一大小写。
- 这段 TTS 音频里所有字的第二名都比第一名低 4–12，几乎没有可翻盘的同音字；**同音替换的效果只能用真实录音评估**。
- 帧统计：约 67%（G=5）的帧走跳帧捷径，与 §5.6 的估计（大部分帧）一致。

### 性能

**Apple Silicon 真机（2026-10-23，Apple M4 / 24GB，用户日常使用的构建，取自度量日志 `decode_ms`）**：真实听写、3 条词汇、标准档。

| 音频 | 解码方式 | `decode_ms` | 同句 `asr`（整段推理） |
| --- | --- | --- | --- |
| 3.1–8.6s | 贪心（无词汇） | 0.1–0.3 | 70–82ms |
| 3.2–7.2s | 束搜索 | 0.2–0.8 | 76–108ms |
| 32.1s | 束搜索 | 3.2 | 369ms |

束搜索相对推理耗时不到 1%，在可感知范围之外；120s 长句与 200 条词汇的组合尚未在真机上记录（按 32s 的线性外推约 12ms，**E**）。
同机 `xcodebuild test`（Debug）下 50 条词汇的编译日志为 7ms。

**开发机（Intel i5-8260U / Linux，实施时）**：合成 2000 帧（≈120s）× 25055、约 75% blank 帧、200 条词汇（最坏情况：每帧都扫描）：

| 实现 | 贪心 | 束搜索（空图） | 束搜索（200 条） | 编译 200 条 |
| --- | --- | --- | --- | --- |
| C#（.NET 10 Release，`Vector<float>` 求最大值） | 43ms | 41ms | 49ms | 17–24ms |
| Swift（-O，求最大值用手写循环替代 vDSP） | — | 32–39ms | 35–42ms | 11ms |

Swift 一栏里约 20ms 是整行求最大值，macOS 上由 `vDSP_maxv` 完成；真实 15.8s 音频（10 条词汇）C# Release 解码 8–11ms。
XCTest 的性能用例按 Debug 构建运行，只断言 2s 上限（Linux Debug 实测约 0.4s）。

### 参数校准（真实录音评估未做）

3.6.0 发布时三档参数为规格 §5.6 的初值；2026-10-24 的保守调整见下节。`HotwordEvaluationTests` 已就绪（用法见 `DEVELOPMENT.md`），需要按 §10.5 录一批真实
语音（10–20 个会认错的词各 3 句 + 30 句日常话）跑一轮，以「标准档误触发为 0、召回尽量高」为目标调整，并把结果写回这里。
差异 #2 修正前观察到的 `issuesue` 说明：强档在「词表里的词与原文读音相近、模型又能直接输出」时有误改风险，校准时
优先检查强档。3.6.0 按初值发布：用户在真机上的日常听写里标准档效果良好（见「macOS 编译与真机验证」），但这不等于系统化的召回 / 误触发评估。

### 热词积极程度收敛（2026-10-24，未发布）

用户反馈多数时候效果良好，但部分无关句子也被积极改成热词；尚无对应录音与词汇表，不能断言每个 bad case 的来源。
代码可确认的放大因素是：完整热词的奖励按 token 数累计（最多 4 个），已确定的前缀也贡献奖励，
标准档最多 6 分，且仍允许单帧落后 5 分的候选。因此一个弱尾字或多个弱候选仍可能合成热词。

先调整现有预设，保持上下文图、剪枝、默认档位、配置字段和预览 / 终稿的一致语义：

| 档位 | 每 token 奖励 b（旧 → 新） | 候选门限 G（旧 → 新） |
| --- | --- | --- |
| 轻 | 1.0 → 0.75 | 4.0 → 2.0 |
| 标准 | 1.5 → 1.25 | 5.0 → 3.0 |
| 强 | 2.5 → 1.75 | 7.0 → 4.0 |

- 降低 `b` 限制弱候选的累计翻盘；收紧 `G` 阻挡弱尾字，避免已确定的前缀替它支付全部损失。标准档奖励上限从 6 降到 5。
- 标准档保留 1.25，而非直接减到 1.0：共享夹具中 `张三` / `张三丰` 同时存在时，扩展尾字只获得新增的一个 token 奖励，
  仍须能纠正分差 1.2 的近分候选。原有正例与命中次数保持不变。强档仍保留更高召回空间。
- 这是有合成反例约束的保守起点，仍是估算，不能据此给出真实误触发率、召回率或“最佳参数”。收紧门限也可能漏掉较难的真热词；
  束搜索有剪枝，不能保证所有输入的误触发都随档位单调变化。
- `aliases` 的确定性替换和可选 LLM 词表是另外两条路径，调 `b/G` 不会降低它们的积极程度。
  特别是常用词 alias，即使识别偏向为“关”也照常替换；须用具体 bad case 区分来源。

**自动化证据（Linux 主机）**：

- 共享 `ctc_hotword_cases.json` 从 11 增到 23 例，既有 11 例的期望 token 与命中次数不变。新增 12 例包含 8 个拒绝反例、
  3 档近分召回正例，以及标准 / 强档的尾字召回差异。旧 Swift 参数在 8 个反例上全部误选热词；新参数下两平台 23 例全部通过。
- Swift 6.1.2 / Swift 6 模式的临时测试工程：编译器、上下文图、束搜索共 **26 项通过**，含真实词表的编译测试。
  使用当前源文件，仅移除临时副本的 Accelerate 导入、以循环实现 vDSP 求最大值，并适配日志、模型定位与资源入口；
  不代表 macOS 原生构建或 vDSP 真机验证。
- .NET 10：Windows 主工程与测试工程交叉编译 **0 警告 / 0 错误**；热词、词汇、词汇设置纯逻辑、文本后处理与输出格式
  相关测试 **34 通过、1 跳过**（默认模型定位未命中）。
- 另用临时 `net10.0/linux-x64` 工程链接当前 C# 识别管线，显式指定本机模型，真实 ONNX Runtime 1.24.2 推理：
  `speech_zh_en_mixed.wav` 配 50 条无关词汇，关 / 轻 / 标准 / 强均与同引擎无词汇结果逐字相同；
  `Audio` 在三档下均命中 1 次且输出不变，**1 项通过**。该音频是既有 TTS 夹具，只证明回归，不证明日常语音效果。

**手工验证（待执行；两平台相同）**：

1. 保留实际词汇表，关闭智能校对，以同一批录音对照旧版与新版；包含已遇到的 bad case、近音但无关的日常句、共享前缀不同尾字、
   停顿 / 背景噪声，以及 10–20 个真实热词各 3 句正例。分别跑关 / 轻 / 标准 / 强；记录误触发、召回、字错误率与 `decode_ms`。
2. 先看标准档是否减少误改、原有热词是否仍能识别；困难词再比较强档的召回收益与误触发代价。预览与终稿都要检查。
3. 若偏向设为“关”仍发生同样的词汇替换，检查是否由 alias 命中；再分别对照关闭 / 开启智能校对的结果。
4. 按 `HotwordEvaluationTests` 的离线流程汇总上述成对录音，以评估集上标准档误触发为 0、召回尽量高为目标；
   无关句至少 30 句。达到评估集目标也不等于所有场景保证零误触发。

本轮 **macOS 原生构建 / 真机听写、Windows x64 / arm64 真机均未验证**。Windows 同步实现与证据见 `windows/DESIGN.md` §33.5。

### macOS 编译与真机验证（2026-10-23，3.6.0）

- **编译与全量测试**：Apple M4 / Xcode，`xcodebuild … test` 332 项全部通过（1 项为默认跳过的离线评估工具），含需要真实模型的
  `EndToEndRecognitionTests`（无关词汇在强档下与贪心逐字相同）与真实 `tokens.json` 上的编译用例。编译器警告均为本功能之前的
  既有代码。
- **真机听写**：用户在 macOS 上实测，功能正常、效果超出预期（用户反馈）。度量日志里束搜索听写的 `vocab_hits` 为 0–3，无词汇时
  `decode_mode=greedy`；「词汇已编译」日志只有条数。规格 §10.6 的 8 个手工步骤没有逐条记录结果。
- **发布前收尾**（本节同批改动）：
  1. `VocabularyConfig.validated(logging:)`：设置页草稿每次渲染都要规整一遍，表里有空白新行时，此前每次渲染（包括在「试一试」里
     敲每一个字）都会写一条「词汇表已规整：空词条 1 条」warning。草稿改为不写日志，只有加载 / 保存时写。
  2. 状态列区分「模型加载后检查」（还没有编译报告）与「保存后检查」（有报告但查不到：刚输入、尚未提交的行），此前一律显示前者。
  3. 「词汇」页只在回车或离开输入框时提交：输入框还有焦点时切走分页或关闭窗口，刚敲的字会丢。补了 `onDisappear` 与
     `windowWillClose` 两处提交（没有变化时是空操作）。

### 待真机验证

1. 规格 §10.6 的手工步骤 1–8 逐条记录（特别是 3「50 条无关词汇 × 20 句日常话无误改」与 6「120 秒长句 + 200 条词汇」）。
2. 设置页：在输入框里敲字后直接切到别的分页、或直接关窗，词条已保存（本节收尾 3）；新行未提交时状态列悬停为「保存后检查」。
3. 「从最近结果添加」：在 sheet 的文本里拖选片段，「听起来像」随之更新，「正确写法」预填片段；添加后词条出现在表里。

### Windows 影响

阶段 4 已移植（见 `windows/DESIGN.md` §33 与 `windows/docs/REVIEW_HISTORY.md` 的 VOC 条目）：配置 schema、编译器、上下文图、
束搜索、替换层、LLM 词表、度量字段，读同一份共享夹具。设置页「词汇」2026-10-23 补齐（`windows/DESIGN.md` §33.4）：功能与
本页相同，保存沿用 Windows 设置窗口的草稿 + 「保存并应用」。上面收尾 1、2 两平台同步。C# 测试在 Linux / macOS 主机上通过
（STA 界面用例除外）；**Windows 真机未验证**。

---

## 12. 决策记录

| # | 决策 | 结论 | 说明 |
| --- | --- | --- | --- |
| D1 | 模型分发方式 | ✅ **首启从 ModelScope 下载** | App 52MB；已验证端点、断点续传与 sha256（§4.4） |
| D2 | 架构覆盖 | ✅ **只出 arm64** | 放弃 Intel Mac；构建脚本三变体逻辑整段删除 |
| D5 | 配置目录 | ✅ **`~/Library/Application Support/VoiceTyper/`** | 与 `VoiceTyperClient` 的 `~/.config/voice_typer/` 天然隔离；首启一次性继承热键与 HUD 透明度 |
| D3 | 新 App 版本号 | ✅ **3.6.0**（当前发布） | 起始为 3.0.0，随后随发布迭代到 3.1.0 → 3.1.3 → 3.1.6 → 3.1.9 → 3.2.0 → 3.2.1 → 3.3.0 → 3.3.1 → 3.5.0 → 3.5.1 → 3.5.2（R8 第一、二批，版本号不再递增）→ 3.6.0（自定义词汇是较大的功能更新，升次版本号；同时收录 R9） |
| D4 | 空闲卸载默认值 | ✅ **从不卸载**（`ASRConfig.idleUnloadMinutes` 默认 0） | 初版默认 10 分钟，3.3.0 后改为"从不"：与"启动时预加载"默认开启配套，让按热键始终零等待；内存代价（~510MB 常驻）由用户按需改回定时卸载 |
| D6 | 热键触发方式 | ✅ **显式二选一 `hold`（默认）/ `toggle`** | 否决"短按 toggle、长按 hold"的自动判别：会把被丢弃的误触升级为用户无察觉的持续录音（§11.2 R5-03） |
| D7 | 版本更新提醒 | ✅ **菜单里手动「检查更新…」**，不引入 Sparkle、不做后台轮询 | 后台自动检查与"音频只在本机处理"的定位相悖；Sparkle 需要 appcast 托管与更新签名密钥管理，不在本轮范围（§11.2 R5-05） |
| D8 | 配置文件的定位 | ✅ **内部存储，不是配置入口** | 移除菜单「打开配置目录」，也不做文件热重载：手改会绕过 UI 校验且需重启才生效，给入口只会制造困惑（§11.2 B9） |
| D9 | 启动时是否预加载模型 | ✅ **默认是**（`asr.preload_on_launch = true`），可在设置里关闭 | 多数用户高频使用，启动即加载让首次听写零等待；关掉后按需加载与录音并行、代价接近于零，服务"当天可能一次都不用、且在意常驻 ~510MB"的场景（§11.3 R6-06） |
| D10 | 浮窗的"不显示"语义 | ✅ **只隐藏过程，保留错误提示** | 全部静音会让插入失败、没采到声音这些在别处看不到的信息彻底消失，把一个偏好项变成静默失败的陷阱（§11.3 R6-05） |
| D11 | 分段下载的失败处理 | ✅ **任何异常都永久回落单连接** | 分段是优化，单连接是经过多轮修复的可靠路径。首次安装是唯一必经之路，优化绝不能把它带崩（§11.3 R6-09） |
| D12 | 输入设备默认策略 | ✅ **`auto`：仅蓝牙输出 + 蓝牙输入时改用内置麦克风** | 蓝牙耳机麦克风走通话模式，音质差且拖垮耳机里的音乐；输出不是蓝牙时蓝牙麦克风是有意的选择，合盖或无可用内置输入时也不切。可改为 `system` 严格跟随（§11.4 R7-A2） |
| D13 | 单独修饰键热键的采集时机 | ✅ **hold 模式按下即开始采集，作为组合快捷键使用时静默丢弃** | 不丢开头的字；代价是麦克风指示点闪烁，用 150ms 延迟浮窗与 toggle 模式（只有干净单击才开始）缓解（§11.4 R7-A3） |
