# VoiceTyper macOS 架构与实现第二轮审查报告

> 审查日期：2026-08-18  
> 审查对象：`d572f8676adbcb7b1aedb1082ab4c2a41c734733`（v3.1.0 修复批次）  
> 对照基线：`32f6dc1f0a15e468756733cdb2b4d2e0fcbb59c5`  
> 审查范围：`macos/` 的设计文档、应用编排、录音、ASR、LLM、文本插入、模型下载、配置与测试工程  
> 本轮只产出审查报告，不修改产品代码。

## 1. 结论先行

本轮修复是有效的，但当前版本仍不宜作为“发布候选”放行。

修复批次已经解决或明显改善了以下问题：金标准 WAV 已纳入版本控制；用户识别文本不再写入日志；LLM URL 不再强制解包；模型哈希移出主线程；下载取消、HTTP 状态与 Keychain 保存结果得到处理；配置保存减少了不必要的控制器重建；音频边界改为 `[Float]`；空闲卸载不再立即反向触发预加载；模型维度增加了部分校验。这些都是真实的质量提升。

但第二轮发现两个发布阻断项：

1. **重叠听写时，旧 `LocalASRSession` 会失去最后一个强引用，FIFO 队首永远无法完成，后续所有听写结果被永久堵住。** 这是本轮为修复顺序问题引入的确定性生命周期缺陷。
2. **标准测试命令会先启动完整生产应用，并阻塞在真实 Keychain 访问，测试用例没有开始执行。** 因此目前只能证明“应用和测试代码可编译”，不能证明自动化测试通过。

此外还有 7 个 P1 问题，集中在设备切换后的状态分裂、录制热键时提前销毁控制器、文本插入目标校验、ASR 取消与并发隔离、明文 LLM 端点、LLM 空结果回归、模型契约校验不足。

第一轮 18 项的回归结果为：**5 项关闭、10 项部分关闭、1 项未关闭、2 项出现修复回归**。这说明现在不适合继续围绕旧客户端回调接口做局部加固；应先收敛会话所有权和状态机，再处理外围细节。

## 2. 审查方法与验证证据

本轮执行了以下工作：

- 逐文件审查 v3.1.0 相对基线的 36 个 macOS 文件改动（约 `+1536/-610`）。
- 重新走查 `热键 → 录音 → 预览 → finalize → LLM → 有序提交 → 文本插入` 调用链。
- 专项检查空闲卸载、并发听写、异常收尾、Keychain、剪贴板、模型下载和 Swift 6 隔离边界。
- 回归第一轮 18 项发现，并检查新增测试是否真的覆盖修复语义。
- 执行干净 DerivedData 的应用构建、测试构建与标准测试命令。

验证结果：

| 验证项 | 结果 | 说明 |
| --- | --- | --- |
| `xcodebuild ... build` | **通过** | 干净 DerivedData，约 12.5 秒，退出码 0 |
| `xcodebuild ... build-for-testing` | **通过** | 应用和测试 bundle 均编译成功，约 12.7 秒，退出码 0 |
| `xcodebuild ... test` | **未通过/挂起** | 超过 90 秒没有进入测试用例，人工终止 |
| 测试宿主采样 | **定位到根因** | `AppDelegate → AppCoordinator.start → SettingsViewModel.load → KeychainStore.loadLLMAPIKey → SecItemCopyMatching` |
| 工作区状态 | **干净** | 审查前无未提交改动；本报告为本轮唯一新增文件 |
| 金标准 WAV | **已跟踪** | `speech_zh_en_mixed.wav` 已被 Git 跟踪，忽略规则有精确例外 |

仓库静态可见 10 个 `XCTestCase` 测试类、45 个 `test...` 方法，但由于测试宿主初始化问题，本轮不能把它们记为“运行通过”。

## 3. 发布阻断项（P0）

### R2-01：有序提交队列没有拥有旧会话，第二段录音会令第一段永久失联

**位置**

- `Sources/VoiceTyper/Core/VoiceTyperController.swift:13, 30, 175-258`
- `Sources/VoiceTyper/ASR/LocalASRSession.swift:41, 124-181`

**证据链**

`VoiceTyperController` 允许在上一段已松键、仍处于识别或 LLM 校对时开始下一段录音，因为 `beginRecording()` 只检查 `isRunning && !isRecording`。控制器新增了 `pending` FIFO，试图按录音发起顺序提交结果，但它只用一个 `asrSession` 属性强持有“当前会话”。

当 B 开始时，`asrSession = B` 覆盖 A。A 的其余引用全部是弱引用：

- 控制器注册的 `onFinal`、`onWarning`、`onError` 和音频闭包都 `weak session`；
- `LocalASRSession.runFinalize` 的队列闭包和回主线程任务都 `weak self`；
- finalize 看门狗同样 `weak self`；
- `RecognitionBuffer` 被推理闭包强持有，但不会反向持有 `LocalASRSession`。

因此 A 可在推理或 LLM 等待期间析构，永远不会回调 `complete(A, ...)`。FIFO 的队首 A 永远是 `result == nil`，即使 B、C 已完成，`drainCommits()` 也不能前进，`pending` 会持续增长。

**最小复现场景**

1. 完成录音 A，A 开始离线识别或慢速 LLM 校对。
2. A 返回前开始录音 B。
3. B 覆盖唯一强引用 `asrSession`，A 被释放。
4. B 返回结果，但因 A 仍占 FIFO 队首而无法上屏；后续结果全部同样被阻塞。

**为什么这是架构问题**

当前实现同时采用了“单一当前会话”和“允许多个未完成 utterance”两种互斥的所有权模型。FIFO 只保存结果槽位，不保存产生结果的工作单元。这个问题不是补一个 `weak/strong` 即可完整解决：旧会话成功、失败、超时、取消时还必须各自执行一次且仅一次的 `close()`、ASR lease 释放和队列槽位结算。

**建议**

优先选择更简单、也与项目约定 `Idle → Recording → Recognizing → Inserting` 一致的方案：**在一段听写完成插入前不接受下一段录音，删除 `pending` FIFO 和多会话兼容分支。** 这是最符合“裁弯取直、去肥增瘦”的处理。

如果“连续快速听写、允许上一段仍校对时开始下一段”是明确产品需求，则必须把它做成一等模型：

- 建立 `UtteranceContext`，包含 ID、强持有的 session、目标焦点、阶段和结果；
- 由 `[UtteranceID: UtteranceContext]` 拥有每个未终止会话；
- 所有成功、错误、超时、取消统一进入幂等的 `finishUtterance(id:outcome:)`；
- 终止路径负责 close、释放 ASR lease、移除强引用、填充 FIFO，再尝试提交；
- 明确“B 正在录音时 A 可以提交吗”，避免全局状态从 `.recording` 被旧结果改写为 `.inserting`。

**必须补的测试**

- A 慢、B 快；A 快、B 慢；A 失败；A 超时；A 被取消；LLM 开/关两种模式。
- 验证每个 session 只关闭一次、每个槽位只结算一次、结果顺序稳定、最终无残留 context。
- 如果选择串行方案，验证 `.recognizing/.inserting` 期间热键不会创建新录音。

### R2-02：测试 target 启动生产 App，真实 Keychain 阻塞在任何测试之前

**位置**

- `scripts/generate_xcodeproj.rb:120-136`
- `VoiceTyper.xcodeproj/project.pbxproj` 的 `BUNDLE_LOADER/TEST_HOST`
- `Sources/VoiceTyper/App/AppDelegate.swift:6-9`
- `Sources/VoiceTyper/UI/Settings/SettingsViewModel.swift:90-95`
- `Sources/VoiceTyper/Core/KeychainStore.swift:10-19`

**现象与证据**

测试 target 被配置为 hosted unit test，`TEST_HOST` 是完整 `VoiceTyper.app`。运行项目文档给出的标准测试命令后，宿主执行生产 `AppDelegate.applicationDidFinishLaunching`，继而启动 `AppCoordinator`、读取真实配置、检查权限、创建设置 UI，并同步访问用户 Keychain。

进程采样显示主线程稳定阻塞在：

```text
AppDelegate.applicationDidFinishLaunching
  AppCoordinator.start
    AppCoordinator.setupControllerIfNeeded
      SettingsViewModel.load
        KeychainStore.loadLLMAPIKey
          SecItemCopyMatching
```

这不仅会挂测试，还可能读取或迁移用户真实配置、触发 TCC/UI、副作用访问模型目录。新增测试再多，只要 test runner 进不去，它们就不能形成质量门禁。

**建议**

首选结构性修复：

1. 把纯逻辑和可注入服务移入 `VoiceTyperCore` framework/library target；
2. 单元测试直接链接 Core，使用无宿主 test bundle；
3. App target 只保留 `AppDelegate`、窗口和组装根；
4. 为 Keychain、配置目录、前台应用/AX、时钟和调度器建立协议或闭包注入；
5. 少量真正需要 AppKit 生命周期的测试单独放 integration/UI test target。

短期可在 XCTest 环境下禁止 `AppDelegate` 启动生产 coordinator，但这只能作为止血措施，不能替代 Core target 分层。同步 Keychain 查询也不应位于主线程启动路径；应异步读取，并把结果通过依赖注入交给 ViewModel/Controller。

**验收标准**

- 标准 `xcodebuild ... test` 有确定退出码并打印真实执行/跳过数量；
- 无模型时只跳过模型相关测试，其余测试实际执行；
- 测试不访问真实 Keychain、Application Support、TCC、麦克风或前台应用；
- CI 增加超时，避免“无输出挂起”被误认为仍在运行。

## 4. 高优先级问题（P1）

### R2-03：输入设备变化只停止了 AudioCapture，控制器仍认为正在录音

**位置**：`AudioCaptureService.swift:97-107`，`VoiceTyperController.swift:98-100, 123-142, 235-244, 282-297`

设备变化时，`AudioCaptureService` 内部调用 `stop()`，刷出尾音并触发 finalize，随后只通过 `onFatalError` 发出字符串。控制器把该回调仅转成 `onPreviewWarning`，没有把 `isRecording` 置为 false，也没有清除 `recordingStartedAt`。

如果识别在用户松开原热键前完成，`handleFinalText()` 因 `isRecording == true` 不会回到 `.idle`，状态停在 `.inserting`。之后松键虽会把 `isRecording` 改为 false，但 `AudioCaptureService.stop()` 已经无事可做，也不会再产生任何状态回调，应用可永久显示“输入中”。

建议把字符串警告改为类型化终止事件，例如 `captureEnded(.deviceChanged, tail)`，由控制器唯一地完成“采集结束 → isRecording=false → finalize 一次 → 状态迁移”。不要让服务自行收尾一半、控制器再猜另一半。

### R2-04：录制新热键先销毁控制器，绕过了“听写中禁止破坏性保存”保护

**位置**：`SettingsViewModel.swift:213-246`，`AppCoordinator.swift:105-112, 394-416`

`beginHotkeyRecording()` 立即调用 `onSuspendHotkey(true)`，而 coordinator 直接执行 `voiceTyperController.stop()`。此动作发生在 `applyConfig()` 的 `currentState.isActiveDictation` 检查之前，所以用户在识别/校对阶段点进热键录制，当前 pending 会先被清空、会话先被关闭；随后保存才被拒绝。

更糟的是，`stop()` 不发出状态迁移，`activateReadyState()` 又刻意保留 `.recording/.recognizing/.inserting`，于是重新启用监听后 UI 可能仍停在旧的 active 状态，但实际已经没有正在处理的 utterance。

建议在设置 UI 层和 coordinator 层都禁止 active dictation 期间进入热键录制；“暂停监听”应只暂停事件 tap，不应等价于销毁整个听写控制器。控制器的 `stop/cancel/shutdown` 也应区分语义，并总是产生可验证的终态。

同类入口还包括“重新加载模型”：它也应受 active session/lease 约束，而不是直接调用 `asrService.reload()`。

### R2-05：文本插入仍不能确认同一 App 内的目标，AX 范围也缺少边界校验

**位置**：`TextInsertionService.swift:35-49, 99-101`

当前只比较录音开始和插入时的前台 PID。用户可以在同一个 App 内切换窗口或字段，例如从正文切到收件人、搜索框甚至敏感字段，PID 不变，结果仍会写入错误位置。

全量 value 回退路径直接把 AX 返回的 `CFRange` 转为 `NSRange` 并调用 `replacingCharacters(in:)`，没有验证：

- location/length 是否非负；
- location 是否为 `kCFNotFound`；
- `location + length` 是否溢出或超过 UTF-16 长度。

恶意或实现不完整的 AX 控件可令此处触发越界异常。建议录音开始时捕获并保留 focused AX element/window 身份，插入前用当前焦点做 `CFEqual` 或等价身份比较；无法确认时只复制剪贴板。范围必须经过完整、无溢出的 UTF-16 边界验证，非法范围直接放弃 AX 回退。

剪贴板恢复已有明显改善，但仍需用真实慢应用验证 1 秒窗口；任何无法确认目标的情况都应优先“复制并提示”，而不是冒险注入。

### R2-06：ASR 生命周期仍依赖数据竞态和看门狗“报错但不停止”

**位置**：`ASRService.swift:51-55, 183-194`，`LocalASRSession.swift:36-42, 76-113, 168-181`，`SenseVoiceEngine.swift:142-155`

当前存在三类问题：

1. `ASRService.engine` 和 `LocalASRSession.closedForQueue` 使用 `nonisolated(unsafe)`；代码注释称单调布尔值的“粗粒度竞态可接受”，但 Swift 内存模型下未同步的跨线程读写仍是数据竞态，不能作为正式并发契约。
2. finalize 超时只向上层报错；已经开始的 `ORTSession.run(..., runOptions: nil)` 仍会占据唯一串行推理队列。后续录音、卸载、reload 都会排在它后面，UI 已报超时不代表系统恢复可用。
3. `sessionEnded()` 没有活动会话计数/lease。任一会话结束就重新安排空闲卸载，无法正确表达多个未完成会话；reload 也没有 active lease 防护。

建议建立真正队列隔离的 `ASREngineWorker`：engine 只存在于 worker 内部，不提供跨隔离域 getter；所有 load/recognize/unload 都通过 worker 方法排队。关闭标记使用 actor/锁保护或通过队列 generation token 判废。会话创建获得 lease，所有终止路径幂等释放，只有 lease 计数归零才开始空闲计时。

若当前 ORT Swift binding 支持终止 run，应把取消句柄接入；若不支持，就必须把“超时仅停止等待、不停止计算”写入设计，并通过更短的输入上限、资源测量和进程级恢复策略控制最坏情况。当前 120 秒上限是改善，但尚无 60/90/120 秒峰值 RSS 与 finalize 延迟证据。

### R2-07：LLM Endpoint 允许向任意远程主机使用明文 HTTP 发送文本和 API Key

**位置**：`LLMEndpoint.swift:5-23`

实现同时接受 `http` 和 `https`，但没有把 HTTP 限定为 `localhost`、回环 IP 或 Unix 本地代理。若用户配置 `http://example.com`，识别文本和 `Authorization: Bearer ...` 都会明文传输。这与“只发送识别文本、不发送音频”并不冲突，但仍是明确的隐私和密钥泄露风险。

建议默认只接受 HTTPS；HTTP 仅允许 `localhost`、`127.0.0.0/8`、`::1`，或通过显式的高风险确认开启。URL 测试应覆盖 IPv4/IPv6 回环、userinfo、端口、query/fragment 和国际化域名。

### R2-08：只有 `<asr_text>` 标签的 LLM 响应会把原识别文本替换为空串

**位置**：`LLMCorrector.swift:112-124`

代码先检查 `content.isEmpty`，随后才剥离可能回显的 `<asr_text>...</asr_text>`。响应为 `<asr_text>\n</asr_text>` 时，剥离后变为空串，但没有再次回落原文。控制器把空串视为失败/丢弃，用户的 ASR 文本会消失。

建议所有规范化和标签剥离完成后再执行一次非空校验；更稳妥的是把 LLM 返回建模为 `.corrected(nonEmptyText)` / `.fallback(reason)`，不让空字符串承担控制信号。补充 tags-only、嵌套/大小写异常标签和纯零宽字符测试。

### R2-09：模型校验修复了 CMVN 维度，但尚未建立完整的可执行契约

**位置**：`ModelLocator.swift:41-113`，`SenseVoiceEngine.swift:59-105, 142-166`，`FbankFrontend.swift:43-61`

正向进展是 `lfr_m/lfr_n/n_mels/frame_length/frame_shift` 和 CMVN 维度已有校验。但以下风险仍在：

- `frontend_conf.fs` 未校验为 16000，甚至未校验大于 0；负数或极小值会在 `FbankFrontend` 初始化数组/FFT 前产生非法派生尺寸，可能直接 trap。
- 已存在但损坏的 `config.yaml` 会回退默认值，容易把模型包损坏伪装成可用模型。
- 只检查文件存在，不验证固定下载目录或缓存模型的 manifest/hash 组合完整性。
- ONNX 输入输出名称、元素类型、rank/shape 没在加载期验证。
- tokens 数量没有与 `ctc_logits` 的 vocab 维度相等校验；当前解码会静默跳过越界 token，形成“成功但缺字”。
- 输出缺失时返回空字符串，模型契约错误与合法静音不可区分。

建议定义 `ModelManifest/ModelContract`，加载时一次性验证采样率、派生 frame 尺寸、LFR/CMVN、词表、ONNX I/O schema 和 vocab；固定下载模型校验整套 pin，显式侧载模型则给出结构化错误。运行时的合法空识别与模型错误必须使用不同结果类型。

## 5. 中优先级问题（P2）

### R2-10：Hotkey worker 的“遗留生命周期等待”分支实际上不可达

`HotkeyService.start()` 先调用 `stop()`；而 `stop()` 无论 worker 是否真实退出，都会把 `workerLifecycle` 清空。因此紧随其后的 `if let abandoned = workerLifecycle` 不可能拿到上一次启动超时保留的 lifecycle，与注释和设计意图矛盾。

超时 worker 因 `cancelled` 检查大体不会再覆盖新 worker 状态，这是改进；但当前代码无法等待或观测遗留线程，`stop()` 等待超时后也仍声称“安全清理”。建议让 lifecycle token 成为 worker 自己拥有的不可变上下文，以 ID/generation 回传退出；超时句柄保留在独立集合直到收到退出信号。至少应为 startup timeout、stop timeout、快速 start-stop-start 增加可控工厂测试。

### R2-11：ModelDownloader 的注入边界泄漏，成功安装路径仍无回归测试

`ModelDownloader` 接受测试注入的 `downloadDestination`，但 delegate 保存 resume data 时硬编码写入 `ModelLocator.downloadDestination`。这会令测试或未来的多目录下载把断点文件写到错误位置，也破坏测试隔离。

新增测试覆盖了 HTTP 失败、checksum 失败和取消，这是有价值的；但没有覆盖一次完整成功下载、首次目标不存在时的原子安装、进度到 1、已有正确文件跳过、resume data 落点。测试注释认为 download task 不适合打桩，却已经用相同机制覆盖失败路径；建议把文件系统与网络传输进一步拆开，分别确定性测试。

### R2-12：浮点配置未拒绝 NaN/Infinity

`AppConfig.validated()` 用 `<`/`>` 判断 `temperature/timeout/opacity`。NaN 与上下界比较都为 false，会原样穿透 clamp；Infinity 可被夹逼，但非有限值的处理没有显式契约。建议对所有浮点配置先检查 `isFinite`，非有限值回落默认值并记录字段名。补 YAML `.nan/.inf` 测试。

### R2-13：迁移期文档和注释仍把旧 client-server 形状当作目标架构

以下表述已经不再只是“历史说明”，而会继续指导错误设计：

- `macos/README.md` 称“不发明新的状态机”、`LocalASRSession` 与旧 WebSocket 接口完全一致、三个服务“原样搬运”；
- `macos/DESIGN.md` 仍称 Windows 是旧架构客户端，且版本表停在 3.0.0；这与仓库当前 Windows 一体化主力实现不符；
- `VoiceTyperController` 仍写“短录音避免上传”；
- `AudioCaptureService` 注释仍称 `[Float]` 回调为“PCM 字节”；
- HUD/状态菜单注释仍写“服务端发来”“服务端连接情况”。

更关键的是，“保持旧回调签名”正是 R2-01 所有权缺陷的背景。建议把现有 `DESIGN.md` 改为“当前架构与不变量”，把迁移映射移到历史 ADR。当前设计原则应是：本地进程内强类型调用、显式资源所有权、单一状态事实源；旧协议只能作为语义参考，不能约束内部接口形状。

## 6. 第一轮 18 项回归矩阵

| 第一轮项 | 第二轮状态 | 结论 |
| --- | --- | --- |
| F-01 干净检出缺 WAV / 工程生成漂移 | **关闭** | WAV 已跟踪，fixture 扩展名与忽略规则已修正，干净构建通过 |
| F-02 日志包含完整识别/校对文本 | **关闭** | 改为字符数或结构化错误，未再发现完整用户文本日志 |
| F-03 非法 LLM URL 崩溃、空响应丢文 | **部分关闭** | URL 强解包和普通空响应已修；tags-only 仍会变空（R2-08） |
| F-04 空闲卸载后立即自动加载 | **部分关闭** | `suspendedForIdle` 修复单会话循环；多会话 lease 仍缺失（R2-06） |
| F-05 旧会话结果乱序/丢失 | **修复回归** | FIFO 思路正确，但旧 session 被释放，反而永久堵塞（R2-01） |
| F-06 进程内仍用 `Data` 模拟网络音频帧 | **部分关闭** | 已改 `[Float]`；单槽 callback/弱引用仍复制旧网络客户端所有权模型 |
| F-07 长录音峰值、超时与取消 | **部分关闭** | 零拷贝 logits、120 秒上限、关闭判废有改善；无实测、无真正取消且有数据竞态 |
| F-08 模型包参数/契约校验不足 | **部分关闭** | CMVN/LFR 基础校验完成；采样率、派生尺寸、ONNX schema/vocab 仍缺（R2-09） |
| F-09 Hotkey start/stop 竞态 | **部分关闭** | 每代 lifecycle 与取消检查有改善；遗留句柄分支不可达且无测试（R2-10） |
| F-10 文本插入、焦点与剪贴板恢复 | **部分关闭** | 选区优先、失败恢复、PID 检查有改善；同 App 焦点和 AX range 仍不安全（R2-05） |
| F-11 下载忙态与取消语义 | **关闭** | UI 取消与 `.cancelled` 回退语义已补齐 |
| F-12 HTTP/哈希主线程/下载错误处理 | **关闭** | HTTP 状态检查和 detached SHA256 已实现；新增测试隔离问题另见 R2-11 |
| F-13 配置保存事务与校验 | **部分关闭** | 保存错误、Keychain 结果、数值夹逼有改善；热键录制绕过保护，NaN 未处理 |
| F-14 状态由多处布尔值和回调共同驱动 | **未关闭** | 仍有 coordinator/controller/capture/session 四处状态源，已造成 R2-03/R2-04 |
| F-15 音频设备变化缺少确定收尾 | **修复回归** | 增加通知与尾音 finalize，但 controller 状态未同步，可卡在 `.inserting`（R2-03） |
| F-16 核心编排缺少测试接缝 | **部分关闭** | 新增 ASR/下载/配置测试；标准测试被生产 App/Keychain 阻塞，核心 controller 等仍零覆盖 |
| F-17 产品命名与用户可见表述 | **关闭** | 主要 UI 命名和版本已统一；迁移期技术注释归入 F-18/R2-13 |
| F-18 设计文档仍以旧架构为中心 | **部分关闭** | 用户介绍有所更新，但核心设计原则、Windows 定位、版本与源码注释仍陈旧（R2-13） |

## 7. 根因归纳：需要“裁弯取直”的四处

### 7.1 不要再让本地 session 假装成 WebSocket client

旧 client 的 callback API 需要应对远端连接、消息到达和断线；本地 session 的工作单元、内存和取消都由本进程拥有。继续复刻 `onPartial/onFinal/onError` 可变属性，会让所有权隐含在弱闭包和“当前对象”里。

建议内部接口改为强类型、结构化并发风格，例如：录音产生 `AudioCaptureOutcome`；识别 session 返回 partial stream 和唯一 final outcome；取消由 session handle 明确拥有。即使暂不采用 `AsyncStream`，也应使用不可变 delegate/context 和终止一次性保证，而非随时可覆盖的 callback slots。

### 7.2 产品若不需要重叠听写，就删除多会话复杂度

项目核心流程明确是线性的 `Idle → Recording → Recognizing → Inserting`。当前允许在 recognizing 时开始新 recording，迫使系统引入 FIFO、跨会话状态覆盖、焦点快照队列和 ASR lease。如果没有明确且经过 UX 验收的“流水线听写”需求，这些都是不必要的肥肉。

默认建议串行化整个 utterance。若确实需要流水线，则状态模型必须升级为“全局 readiness + 多个 per-utterance state”，不能继续用一个 `AppState` 表示多个并发事实。

### 7.3 建立单一状态事实源

目前至少有：

- `AppCoordinator.currentState/isPaused/isDownloadingModel`；
- `VoiceTyperController.isRunning/isRecording/pending/currentUtteranceID`；
- `AudioCaptureService.isRunning`；
- `LocalASRSession.isFinalizing/closed`；
- `ASRService.state`。

这些状态通过无返回值 callback 相互猜测，异常路径很容易只更新其中一半。建议定义类型化事件和 reducer：`hotkeyPressed`、`captureStarted`、`captureEnded(reason)`、`recognitionFinished(outcome)`、`insertionFinished(outcome)`、`shutdown(reason)`；只有一个 `@MainActor` owner 能改变用户态。服务只报告事实，不直接决定跨层状态。

### 7.4 ASR 串行队列应是隔离边界，不应只是约定

现在通过 `nonisolated(unsafe)` 和注释维持“大家都只在 asrQueue 使用 engine”。更瘦的做法是让 engine 根本无法被队列外取得。worker 接收值类型请求，内部持有 engine；session 只持 worker handle 和 generation/lease。这样可以删除 `engineAccessor`、`closedForQueue` 的竞态和多处手工 queue hop。

## 8. 建议整改顺序

### 阶段 A：恢复可信发布门禁

1. 决定听写是否允许重叠；默认建议串行并删除 FIFO。
2. 修复 session 强所有权/终止一次性语义，补 R2-01 的确定性测试。
3. 拆 Core test target 或至少禁止测试宿主启动生产 coordinator，令标准测试真实执行。
4. 把 Keychain、配置目录和系统服务注入测试环境。

### 阶段 B：收敛状态和异常收尾

1. 用类型化 `captureEnded` 修复设备变化。
2. 区分 pause hotkey listener、cancel utterance、shutdown controller。
3. 禁止 active dictation 期间录制热键/reload 模型，或通过显式 lease 延后操作。
4. 让所有终止路径走同一个幂等收尾函数。

### 阶段 C：安全与模型边界

1. 捕获并校验具体 AX 焦点目标，校验所有 CFRange。
2. HTTP 仅限回环，远端强制 HTTPS。
3. LLM 规范化后再次判空。
4. 建立模型 manifest/contract，加载期报告结构化错误。

### 阶段 D：去除迁移遗存

1. 引入队列隔离的 ASR worker，移除 `nonisolated(unsafe)` 共享状态。
2. 将旧协议 callback 接口替换为进程内强类型结果/事件。
3. 重写 README/DESIGN 的当前架构章节，历史迁移说明进入 ADR。
4. 清理“上传、服务端、PCM 字节”等陈旧注释。

## 9. 下一轮验收清单

自动化测试至少应覆盖：

- 标准测试命令在无模型机器上确定结束；非模型测试真实运行。
- 快速连续热键、慢 LLM、识别错误、超时、取消的会话所有权与提交顺序。
- 设备在录音中拔出/切换，分别在用户松键前后完成识别，最终均进入稳定状态。
- 录音、识别、校对、插入各阶段尝试录制热键、保存配置、reload 模型。
- 同 App 切窗口/切字段、无效 AX range、AX 不可写、粘贴失败和用户中途复制新内容。
- 两个活动 session（若产品保留并发）与极短 idle timeout 的 lease 行为。
- LLM tags-only、远程 HTTP、回环 HTTP、HTTP 错误与超时回退。
- `fs <= 0`、`fs != 16000`、非法派生 frame、CMVN/vocab/ONNX schema 不匹配。
- 下载首次成功安装、跳过已校验文件、取消/恢复、错误目录隔离和进度单调性。
- YAML NaN/Infinity 与所有配置边界。

真机手工验证至少包括：

- 麦克风、输入监控、辅助功能权限分别拒绝/撤销/重新授权；
- 真实 USB/蓝牙/内置麦克风切换；
- Safari、备忘录、Pages/Word、终端、浏览器富文本编辑器的 AX 与粘贴插入；
- 用户剪贴板含文本、图片、多类型 item 时的恢复；
- 60/90/120 秒录音的峰值 RSS、松键到最终文本耗时、超时后的下一次听写可用性；
- 模型首次下载、取消、重启续传、磁盘空间不足、checksum 失败；
- LLM 本地 HTTP 与远程 HTTPS，确认日志不含 API Key 和用户全文。

涉及性能、权限、热键、设备切换与真实文本插入的结论，在完成以上真机步骤前只能标记为“代码审查/自动化验证通过”，不能标记为“真机验证通过”。

## 10. 最终判断

v3.1.0 修复批次把许多外围风险从“明显缺失”推进到了“有防护、有测试雏形”，方向是对的；但会话所有权和测试宿主两个基础问题说明，当前架构仍受旧 client-server 接口形状牵引。

建议下一步不要继续扩大补丁面。先做两项决定：**听写是否必须支持重叠**，以及**是否把可测试核心从 App target 拆出**。默认答案分别应是“先不支持，保持线性状态机”和“拆出 Core”。这两步能直接删除 FIFO、弱引用会话、hosted unit test 副作用等一批复杂度，也为后续状态收敛、ASR worker 隔离和可靠真机验收打下基础。
