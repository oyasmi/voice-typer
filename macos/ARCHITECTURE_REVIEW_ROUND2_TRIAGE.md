# 第二轮审查报告的核验与整改方案

> 核验日期：2026-08-18
> 核验对象：`ARCHITECTURE_REVIEW_ROUND2.md` 中的 13 项发现（R2-01 … R2-13）
> 代码基线：`d572f86`（v3.1.0），工作区无未提交产品代码改动
> 本文只做核验、分级与方案设计，不修改产品代码。

## 0. 结论先行

- 13 项发现中，**11 项完全成立、1 项部分成立、1 项结论有误**。
- 唯一的真发布阻断项是 **R2-01（会话所有权）**；报告列为第二个 P0 的 **R2-02（测试挂起）在本机无法复现**，标准测试命令 3 秒内跑完 45 个用例并 `TEST SUCCEEDED`。
- 建议修复 **11 项**，明确**放弃 4 个子项**（同 App 内焦点身份校验、ORT 运行时取消、模型 manifest/hash 体系、拆 Core framework target）。
- 核验中另外发现 1 个报告未指出、但比 R2-06 描述更严重的真实数据竞态（`N-01`）。
- 整改主线只有一条：**把听写串行化**。R2-01 的正确修法会顺带消解 R2-03 的一半、R2-06 的 lease 需求，并净删代码。

---

## 1. 逐项核验结果

| 编号 | 报告结论 | 核验 | 处置 |
| --- | --- | --- | --- |
| R2-01 | 旧会话失去强引用，FIFO 永久堵塞 | **成立**（引用图证据见 §2.1） | **修**（P0） |
| R2-02 | 测试宿主阻塞在 Keychain，测试未执行 | **部分成立**：本机 45/45 通过；宿主副作用真实存在 | 降级 P2，只做止血守卫 |
| R2-03 | 设备切换后控制器仍认为在录音，卡在 `.inserting` | **成立** | **修**（P1，随 R2-01 几乎免费） |
| R2-04 | 录制热键先销毁控制器，绕过保护 | **成立**；但"UI 卡在旧 active 状态"**不成立** | **修**（P1，改为拒绝） |
| R2-05a | AX `CFRange` 无边界校验，可越界 trap | **成立** | **修**（廉价防崩溃） |
| R2-05b | 同 App 内切窗口/字段无法识别 | 成立，但**修复性价比低** | **不修**（§4.1） |
| R2-06a | `nonisolated(unsafe)` 共享状态 | **成立，且实际已被违反**（见 N-01） | **修**（锁保护，不做 worker 重构） |
| R2-06b | 超时只报错、不终止推理 | **成立**；ORT ObjC 绑定确无终止 API | **不修**，写入设计文档 |
| R2-06c | 缺 lease，多会话计数错误 | 成立 | 随 R2-01 串行化消失 |
| R2-07 | 明文 HTTP 可发往任意远端 | **成立** | **修**（限回环/私网） |
| R2-08 | tags-only 响应把文本变成空串 | **成立** | **修**（1 行） |
| R2-09 | 模型契约校验不足（5 个子项） | 4 项成立，1 项过度 | **修 4 项**，放弃 manifest 体系 |
| R2-10 | Hotkey 遗留 lifecycle 分支不可达 | **成立**（确为死代码） | **修**（小） |
| R2-11 | resume data 硬编码写死目录 | **成立**，且会污染用户真实模型目录 | **修**（1 行） |
| R2-12 | 浮点配置未拒绝 NaN | **成立** | **修**（clamp 重载） |
| R2-13 | 文档/注释仍以 client-server 为目标架构 | **成立** | **修**（限关键项） |

---

## 2. 成立项的证据

### 2.1 R2-01：确认，且引用图可完整证明

`LocalASRSession` 的强引用**有且只有** `VoiceTyperController.asrSession` 一处：

- `VoiceTyperController.swift:191/204/211` 三个回调都是 `[weak self, weak session]`，且闭包本身存放在 session 自己身上（自持不构成外部强引用）；
- `VoiceTyperController.swift:229/235` 的 `onChunk/onTailChunk` 是 `[weak session]`，由 `AudioCaptureService` 持有；
- `LocalASRSession.swift:82`（看门狗）、`:125/:169`（asrQueue 闭包）、`:128/:172`（回主线程 Task）、`:198`（LLM 校对 Task）全部 `[weak self]`；
- `RecognitionBuffer` 被 asrQueue 闭包强持有，但它只持有 `engine`，不反向持有 session。

因此 `VoiceTyperController.swift:258` 的 `asrSession = session` 一旦被第二段录音覆盖，A 立即析构，`onFinal/onError` 永不触发，`pending` 队首 `result` 永远为 `nil`，`drainCommits()`（`:273`）无法前进——**此后所有听写结果永久不上屏**，且 `pending` 无界增长。

补充两点报告没写、但影响修复方案的事实：

1. A 析构时 `teardownASRSession()` 从未对 A 执行过，因此 A 的 `close()` 未调用、`asrService.sessionEnded()` 未配对调用（空闲卸载计时被少安排一次）。
2. 触发条件不需要 LLM：只要"松键后 → 再次按键"的间隔短于离线复识别耗时即可；开启 LLM 校对时窗口从几百毫秒扩大到几秒。

### 2.2 R2-02：报告结论有误，本机可正常执行

实际执行标准命令（干净运行，非增量）：

```
Test Suite 'All tests' passed
Executed 45 tests, with 0 failures (0 unexpected) in 1.623 (1.644) seconds
** TEST SUCCEEDED **   （xcodebuild 退出码 0，整体约 3 秒）
```

45 个用例与报告静态统计的数量一致，即**它们确实全部执行了**。报告采样到的 `SecItemCopyMatching` 阻塞是环境相关的：只有当本机 Keychain 里**已存在** `llm_api_key` 条目、且宿主 App 的 ad-hoc 签名与该条目 ACL 不匹配时，系统才会弹出授权对话框并同步阻塞主线程。本机未配置 LLM，查询直接返回 `errSecItemNotFound`。

但**报告指出的结构性问题仍然成立**：hosted unit test 会真实启动 `AppDelegate → AppCoordinator.start()`，从而读写真实 `~/Library/Application Support/VoiceTyper/`、探测 TCC 权限、创建状态栏项与设置窗口，并在权限缺失时同步读 Keychain。日志中已能看到测试期间产生的真实副作用（配置迁移、模型下载重试等）。

结论：**降级为 P2**，只做"XCTest 环境下不启动生产 coordinator"这一止血守卫；不采纳"拆 `VoiceTyperCore` framework target"的建议（见 §4.4）。

### 2.3 R2-03：确认

`AudioCaptureService.swift:104-108` 在设备变化时自行 `stop()`（走正常尾音路径 → `finalize` → 控制器置 `.recognizing`），随后只发一个字符串 `onFatalError`；`VoiceTyperController.swift:98-100` 把它转成 `onPreviewWarning`，**不动 `isRecording`**。

后果链：`handleFinalText`（`:283-291`）因 `isRecording == true` 跳过所有回 `.idle` 的分支 → 状态停在 `.inserting`；用户随后松键，`finishRecording()` 把 `isRecording` 置 false 后调 `audioCaptureService.stop()`，而它的 `guard isRunning`（`:112`）已为 false，**不再产生任何回调**。状态栏因此长期显示"输入中…"，直到下一次听写。

### 2.4 R2-04：主结论成立，次结论不成立

成立部分：`SettingsViewModel.beginHotkeyRecording()`（`:238-242`）→ `onSuspendHotkey(true)` → `AppCoordinator.swift:105-113` 直接 `voiceTyperController?.stop()`。`stop()`（`VoiceTyperController.swift:106-113`）会 `pending.removeAll()` 并 `teardownASRSession()`，**在 `applyConfig()` 的 `isActiveDictation` 检查之前就把用户正在进行的听写销毁了**——保存请求随后才被拒绝，但内容已经没了。

不成立部分：报告称"UI 可能仍停在旧 active 状态"。实际上恢复路径 `onSuspendHotkey(false)` → `reevaluateReadiness()` → `activateReadyState()` → `controller.start()`，而 `start()` 会发 `onStateChange?(.idle)`（`VoiceTyperController.swift:103`），coordinator 的 `currentState` 因此被刷成 `.idle`。UI 不会卡住。**真实损害是"用户内容被静默丢弃"，不是"状态卡死"。**

同类入口"重新加载模型"（`AppCoordinator.swift:126-128` 直接 `asrService.reload()`）确实也没有 active dictation 约束。

### 2.5 R2-05：a 成立、b 成立但不建议修

`TextInsertionService.swift:99-101` 把 AX 返回的 `CFRange` 无校验地转成 `NSRange` 并调用 `NSString.replacingCharacters(in:)`。`location` 为 `kCFNotFound(-1)`、负长度或 `location+length` 超出 UTF-16 长度时，会抛 `NSRangeException`——Swift 无法捕获，**直接闪退**。这条修起来只有几行，必须修。

b（同 App 内焦点身份）见 §4.1 的放弃理由。

### 2.6 R2-06 / N-01：比报告描述的更严重

报告说 `ASRService.engine` 的 `nonisolated(unsafe)` 是"未同步但有队列约定"。实际情况是**约定已经被违反**：

- `ASRService.swift:52` 声明 `nonisolated(unsafe) private var engine`，注释要求"只应在 asrQueue 上读写"；
- 但 `LocalASRSession.ensureBufferIfPossible()`（`LocalASRSession.swift:113`）在 **MainActor** 上调用 `engineAccessor()` → `ASRService.currentEngine()` → 裸读 `engine`。

写入方（`load()` 的 `asrQueue.async { self?.engine = built }`、`unloadNow` 的 `asrQueue.async { self?.engine = nil }`）在 asrQueue 上，读取方在主线程，**没有任何同步**。这是对一个带 ARC 的类存在体指针的无同步读写：空闲卸载或 reload 与"用户按下热键"重叠时，可能读到撕裂值或对已释放对象 retain。属于真实的、可致崩溃的数据竞态，不只是"契约不正式"。

`LocalASRSession.closedForQueue`（`:41`）同理，但它是单调 Bool，实际风险低。

R2-06b 经查属实且无解：SPM 检出的 ObjC 绑定 `objectivec/include/ort_session.h:265-310` 中，`ORTRunOptions` 只有 `setLogTag / setLogSeverityLevel / addConfigEntryWithKey`，**没有 `SetTerminate` 映射**。要真正取消运行必须绕到 C API 自己包 `OrtRunOptionsSetTerminate`，性价比不成立。

### 2.7 R2-07 / R2-08 / R2-09 / R2-10 / R2-11 / R2-12

- **R2-07**：`LLMEndpoint.swift:17` 对任意 host 放行 `http`。用户填 `http://example.com` 时，识别原文与 `Authorization: Bearer <key>` 全程明文。成立。
- **R2-08**：`LLMCorrector.swift:116` 先判空、`:120-123` 后剥标签。输入 `<asr_text>\n</asr_text>` → 剥离后为空串 → 返回 `""` → `handleFinalText` 当作空结果丢弃，**用户的 ASR 文本消失**。成立。
- **R2-09**：
  - `fs` 未校验成立：`ModelLocator.swift:107` 直接把 YAML 里的 `fs` 赋给 `opts.sampleRate`，`SenseVoiceEngine.swift:61-69` 的 guard 也没检查它。`fs<=0` 会让 `FbankFrontend.swift:46-56` 算出非正 `frameLength`，`[Float](repeating:count:)` 直接 trap。
  - tokens 与 vocab 维度未比对成立：`CTCDecoder.swift:44` 用 `where id < tokens.count` 静默跳过越界 id，词表偏小时表现为"识别成功但缺字"。
  - ONNX I/O 未在加载期校验成立（`SenseVoiceEngine.swift:145-154` 直到每次推理才按名字取）。
  - 输出缺失返回 `""` 成立（`:156-158`），与合法静音不可区分。
  - manifest/hash 体系：属过度设计，见 §4.3。
- **R2-10**：成立且是**死代码**。`start()` 第一行就调 `stop()`，而 `stop()`（`HotkeyService.swift:137`）无条件 `workerLifecycle = nil`，所以 `:76` 的 `if let abandoned = workerLifecycle` 永远取不到超时遗留的句柄。
- **R2-11**：成立。`ModelDownloader.swift:257` 硬编码 `ModelLocator.downloadDestination`，而不是注入的 `downloadDestination`。实际影响不止"测试隔离"：**跑一次 `xcodebuild test` 就会把 `.resume` 碎片写进用户真实的 `~/Library/Application Support/VoiceTyper/models/sensevoice-small/`**。
- **R2-12**：成立。`AppConfig.swift:52` 的 `guard value < lower || value > upper` 对 NaN 两边都为 false，NaN 原样穿透。`llm.temperature = .nan` 会让 `JSONSerialization` 抛错 → 每次校对都静默回落原文；`ui.opacity = .nan` 会污染窗口 alpha。

### 2.8 R2-13：成立，但只需修其中一部分

确认存在：`README.md:196-206`（"原样搬运"/"回调签名完全一致"/"不发明新的状态机"）、`DESIGN.md:6`（称 Windows 仍是旧架构客户端，与仓库现状矛盾）、`DESIGN.md:35`（版本表停在 3.0.0，README 已是 3.1.0）、`AudioCaptureService.swift:13`（`[Float]` 回调仍写"PCM 字节…38400 bytes"）、`VoiceTyperController.swift:36`（"避免误触上传"）、`RecordingHUDController.swift:120`（"服务端发来的"）、`StatusMenuHeaderView.swift:4`（"服务端连接情况"）。

但 `FbankFrontend / CTCDecoder / RecognitionBuffer / SenseVoiceEngine` 里对 `recognizer.py` 的引用**不是技术债，是有效的对齐契约**（金标准夹具正是靠它维系，AGENTS.md 亦有明文要求），不应清理。

---

## 3. 整改方案

### 3.1 【P0】R2-01 + R2-03：串行化听写，会话所有权归一

**决策：不支持重叠听写。** 理由：AGENTS.md 已把主流程定义为 `Idle → Recording → Recognizing → Inserting`；重叠听写没有产品需求背书，却要求 FIFO、跨会话状态覆盖、焦点快照队列和 ASR lease 四套机制同时正确。串行化是净删代码。

**核心改动**：把"当前会话 + 队列槽位 + 两个布尔"合并为一个被唯一强持有的值。

```swift
// VoiceTyperController
private enum Phase { case recording, recognizing }

private final class Utterance {
    let session: LocalASRSession          // 控制器是唯一所有者，杜绝中途析构
    let expectedFrontmostPID: pid_t?
    let startedAt: Date
    var phase: Phase = .recording
    init(...) { ... }
}

private var active: Utterance?
private var isRecording: Bool { active?.phase == .recording }   // 派生，不再是独立事实源
```

删除：`pending`、`nextUtteranceID`、`currentUtteranceID`、`PendingUtterance`、`complete(utteranceID:result:)`、`drainCommits()`、`recordingStartedAt`、`isRecording` 存储属性，以及三个回调里的 `weak session` + `asrSession === s` 判别。净变化约 `-60/+55` 行。

**唯一收尾函数**（幂等靠"先取走再处理"保证）：

```swift
private enum Outcome {
    case text(String)         // onFinal
    case failed(String)       // onError
    case cancelled            // 用户 Esc
    case discarded            // 短录音过滤 / 采集启动失败
    case shutdown             // stop()：不发状态
}

private func finish(_ outcome: Outcome) {
    guard let utterance = active else { return }   // 幂等：任何重复到达都直接返回
    active = nil
    utterance.session.close()
    asrService.sessionEnded()
    audioCaptureService.onChunk = nil
    audioCaptureService.onTailChunk = nil
    previewText = ""
    onPreviewUpdate?("")
    // …按 outcome 分支：插入文本 / 报错 / 发 onCancelled / 静默回 idle / 不发状态
}
```

所有终止路径（`onFinal`、`onError`、Esc、短录音、`audioCaptureService.start()` 抛错、看门狗超时、`stop()`）都只调 `finish(_:)`，不再各自拆一半。

**入口互斥**：

```swift
private func beginRecording() {
    guard isRunning else { return }
    guard active == nil else {
        onPreviewWarning?("上一段听写尚未完成")   // 有反馈，不是静默吞掉
        return
    }
    ...
}
```

**R2-03 顺带解决**：新设计里 `phase` 的推进点是 `onTailChunk`（`.recording → .recognizing`），而设备切换正是走 `stop()` → `onTailChunk` 这条路，因此**状态自动正确**，无需再让服务和控制器各猜一半。剩下的只是把字符串警告改成语义化事件：

```swift
// AudioCaptureService
enum CaptureEndReason { case normal, deviceChanged }
var onCaptureEnded: ((CaptureEndReason) -> Void)?   // 取代裸字符串 onFatalError
```

控制器收到 `.deviceChanged` 时只做两件事：提示用户 + 确认 `phase` 已推进（若因极短录音没有 tail，则 `finish(.discarded)`）。

**必须补的测试**（见 §3.9 的测试接缝）：

1. `.recognizing` 期间按热键 → 不创建新会话、`active` 不被覆盖、收到 warning；
2. 慢识别 + 快速二次按键 → 第一段结果仍能上屏（这条直接钉死 R2-01 回归）；
3. `onFinal` / `onError` / Esc / 短录音 / 采集启动失败 / `stop()` 六条路径各自 **`close()` 恰好一次、`sessionEnded()` 恰好一次、最终 `active == nil`**；
4. 录音中设备切换：分别在松键前、松键后完成识别，最终都回到 `.idle`。

### 3.2 【P1】R2-04：暂停监听 ≠ 销毁控制器

两层改动，都很小：

1. **语义分层**：`onSuspendHotkey(true)` 不再调 `voiceTyperController?.stop()`，改调新增的 `voiceTyperController?.suspendHotkeyListening()`——内部只做 `hotkeyService.stop()`，不碰会话、不清 `active`。恢复时 `resumeHotkeyListening()` 重启 tap。`stop()` 保留给真正的销毁路径（保存配置重建、暂停听写、退出）。
2. **入口拒绝**：把回调签名改成 `var onSuspendHotkey: ((Bool) -> Bool)?`，coordinator 在 `suspend == true` 且 `currentState.isActiveDictation` 时返回 `false`；`SettingsViewModel.beginHotkeyRecording()` 据此不进入录制态，并给出"正在听写中，请稍后再录制热键"的提示。同一守卫加到 `onReloadModel`（`AppCoordinator.swift:126`）。

这样"用户内容被静默销毁"和"保护形同虚设"两个问题一起消失，且没有引入新状态。

### 3.3 【P1】R2-06a / N-01：给 engine 加锁，不做 worker 重构

只改 `ASRService`，把三处裸访问收敛成两个加锁方法：

```swift
private let engineLock = NSLock()
private var _engine: (any SenseVoiceRecognizing)?     // 去掉 nonisolated(unsafe)

nonisolated func currentEngine() -> (any SenseVoiceRecognizing)? {
    engineLock.lock(); defer { engineLock.unlock() }
    return _engine
}
private nonisolated func setEngine(_ new: (any SenseVoiceRecognizing)?) -> Bool { /* 返回旧值是否存在 */ }
```

推理仍然只在 `asrQueue` 上跑（`SenseVoiceEngine` 的可变状态由队列串行化保护，这一点不变）；锁只保护"引用本身的读写"。卸载时旧引擎若正被某个 `RecognitionBuffer` 持有，它会自然延后释放，且因为 `asrQueue` 串行，不会出现两个引擎并发推理。

`LocalASRSession.closedForQueue` 同步换成一个 10 行的 `CancelFlag`（NSLock 保护的 Bool），彻底移除文件内所有 `nonisolated(unsafe)`。

**明确放弃** `ASREngineWorker` 重构：它要求重写 load/unload/reload/session 四条路径与全部测试，收益仅是"编译器可验证"，而上面的锁方案已经消除了真实竞态。

### 3.4 【P1】R2-05a：AX 范围校验

在 `TextInsertionService` 加一个纯函数并加单测（无需 AX 环境）：

```swift
/// AX 控件返回的 CFRange 不可信：kCFNotFound、负值、溢出都可能出现，
/// 直接喂给 NSString.replacingCharacters 会抛 NSRangeException（Swift 无法捕获）。
static func validInsertionRange(_ range: CFRange, in length: Int) -> NSRange? {
    guard range.location >= 0, range.length >= 0,
          range.location <= length,
          length - range.location >= range.length else { return nil }
    return NSRange(location: range.location, length: range.length)
}
```

`selectedTextRange(for:)` 返回值先过这个函数，非法时**放弃 AX 全量替换、回退粘贴**（而不是退化成"追加到末尾"，那会把文本写到用户没预期的位置）。

### 3.5 【P1】R2-07：明文 HTTP 限回环与私网

把 `LLMEndpoint` 从"能否解析"升级为"能否解析 + 是否允许"，让 UI 能给出可操作的原因：

```swift
enum LLMEndpointError: LocalizedError {
    case malformed
    case insecurePlaintextHost(String)   // "明文 HTTP 只允许本机或局域网地址，公网请使用 https://"
}
static func resolve(_ baseURL: String) -> Result<URL, LLMEndpointError>
static func chatCompletionsURL(from:) -> URL?   // 保留为 try? 便利入口，减少改动面
```

`http` 放行名单（**刻意包含私网**，否则会误伤"局域网另一台机器跑 Ollama/vLLM"这一常见本地部署）：`localhost`、`*.localhost`、`127.0.0.0/8`、`::1`、`10/8`、`172.16/12`、`192.168/16`、`169.254/16`、`*.local`。其余一律要求 `https`。

测试覆盖：IPv4/IPv6 回环、带端口、带 userinfo、`http://10.x`、`http://example.com`（拒绝）、`https://example.com`（放行）、大小写与末尾斜杠。

### 3.6 【P1】R2-08：规范化后再判空（1 行级）

`LLMCorrector.correctOrThrow` 结尾改为：**剥标签之后**再做一次非空判定：

```swift
content = stripEchoedTags(content).trimmingCharacters(in: .whitespacesAndNewlines)
guard !content.isEmpty else { return text }   // 任何规范化后变空，一律回落原文
return content
```

不引入 `.corrected/.fallback` 枚举——当前"失败即返回原文"的契约已经足够，加枚举会波及 corrector、controller、设置页三处而没有新语义。测试补：tags-only、`<asr_text>` 内只有零宽字符、大小写异常标签。

### 3.7 【P1】R2-09：把模型契约做成加载期的四条断言

不做 manifest/hash 体系（§4.3），只在**已有的** `SenseVoiceEngine.init` 校验块里补齐，全部复用现成的 `ConfigurationError.inconsistentDimensions`：

1. **采样率**：`guard fbankOptions.sampleRate == AppConstants.targetSampleRate`。采集链路固定 16kHz，要求别的采样率的模型本来就不可用——用等值判断比"大于 0"更强也更简单，同时挡掉 `fs<=0` 导致的 trap。
2. **ONNX I/O 名称**：`init` 里用 `session.inputNames()/outputNames()` 校验 4 输入 2 输出齐备，缺失时报"模型 I/O 与 SenseVoice-Small 契约不符"，而不是每次推理才失败。
3. **词表 vs vocab 维度**：`vocabSize` 只有推理后可知，故在 `recognize` 里首次拿到 shape 时校验 `vocabSize == tokens.count`，不等则 `throw`（当前的静默跳过会产出"成功但缺字"，比报错更难排查）。
4. **输出缺失**：`guard let logitsValue…` 分支由 `return ""` 改为 `throw`，让"模型契约错误"与"合法静音"可区分。

另外把 `ModelLocator.parseFrontendConf` 中"config.yaml 存在但解析失败"的 warning 保留（不升级为错误），因为 sha256 已固定的下载模型不会走到这里，而侧载模型的最终把关交给上面 4 条断言。

### 3.8 【P2】小额修复清单

| 项 | 改法 | 规模 |
| --- | --- | --- |
| R2-02 | `AppDelegate.applicationDidFinishLaunching` 开头：`guard NSClassFromString("XCTestCase") == nil else { return }`，并在注释里写明这是测试宿主隔离而非功能开关 | 3 行 |
| R2-10 | 新增 `private var abandonedLifecycle: WorkerLifecycle?`，超时时存入它（而非留在 `workerLifecycle`），`start()` 开头等它退出后清空；`stop()` 不再触碰它 | ~8 行 |
| R2-11 | `downloadDestination` 改为 `nonisolated let`，delegate 里用它替换硬编码的 `ModelLocator.downloadDestination` | 2 行 |
| R2-12 | 为浮点加 `clamp` 重载：`guard value.isFinite else { 记 warning; return 默认值 }`，再走原逻辑；补 YAML `.nan/.inf` 用例 | ~10 行 |
| R2-13 | 只改 6 处：README 的"不发明新的状态机/回调签名完全一致/原样搬运"段、DESIGN 的 Windows 定位与版本表（3.0.0 → 3.1.0）、`AudioCaptureService.swift:13`、`VoiceTyperController.swift:36`、`RecordingHUDController.swift:120`、`StatusMenuHeaderView.swift:4`。ASR 目录里对 `recognizer.py` 的对齐引用**保留** | 文档级 |

R2-06b（超时不终止推理）不改代码，但**必须在 `DESIGN.md` 写明**："finalize 超时只停止等待，不停止计算；ORT ObjC 绑定无 `SetTerminate`；最坏情况由 120 秒单段上限约束。"——把已知限制显式化，避免下一轮再被当成新发现。

### 3.9 测试接缝（支撑 §3.1 的验收）

`VoiceTyperController` 目前零覆盖，根因是三个依赖都是 `final class` 具体类型。最小接缝：为它们各抽一个只含控制器实际用到的成员的协议（`AudioCapturing` / `HotkeyListening` / `TextInserting`，合计约 20 行声明 + 3 行 `extension X: P {}`），控制器改为持有协议类型。

`ASRService` 已有 `engineFactory`/`modelLocate` 注入，配一个返回固定文本的 `FakeEngine`，即可让"热键 → 录音 → finalize → 插入"整条链在单测里以真实时序跑通，且不碰麦克风、AX 与剪贴板。这是覆盖 R2-01/R2-03/R2-04 四条验收用例的最低成本路径。

---

## 4. 明确放弃的项（及理由）

### 4.1 同 App 内的 AX 焦点身份校验（R2-05b）

**放弃。** 收益场景（用户在一次听写的几秒内于同一 App 内切换字段）非常罕见；而代价是把插入路径的正确性押在"AX element 身份在两次查询间稳定"这个各家 App 实现差异极大的假设上。一旦出现假阳性，表现是**用户正常听写却拒绝插入、只复制到剪贴板**——这比要防的问题更常见、更影响主流程。现有的跨 App PID 校验已经覆盖了绝大部分真实误插风险。建议在 DESIGN 的"已知限制"里记一笔，等真实反馈再说。

### 4.2 ORT 运行时取消（R2-06b）

**放弃。** 绑定无 `SetTerminate`（已核实头文件），自行桥接 C API 属于为极低频场景引入平台耦合。现有 120 秒单段上限已把最坏阻塞时间限定在可接受量级，改为文档化限制。

### 4.3 模型 manifest / hash 契约体系（R2-09 的一部分）

**放弃。** 下载路径的 4 个文件已有固定 sha256 逐个校验；再叠一层 manifest 只对"用户手动侧载"生效，而 §3.7 的四条加载期断言已经能把侧载失败变成明确错误。收益重叠、复杂度不低。

### 4.4 拆 `VoiceTyperCore` framework target（R2-02 的建议）

**放弃。** 前提（测试跑不起来）已被证伪；45 个用例 3 秒跑完。拆 target 需要改工程生成脚本、访问控制、资源打包与全部 `@testable` 导入，是本仓库当前规模下典型的过度工程。§3.8 的 3 行守卫已经消除了宿主副作用这一真实问题。

### 4.5 报告 §7 提出的"类型化事件 + reducer 单一事实源"

**暂缓。** 方向正确，但 §3.1 串行化之后，真正的用户态事实源已收敛为 `AppCoordinator.currentState` + 派生的 `active`，多源猜测的诱因（FIFO、跨会话覆盖、独立 `isRecording`）都被删除。在此之前引入 reducer 只是换一种写法。等真出现第三个状态消费者时再做。

---

## 5. 建议执行顺序

1. **批次一（阻断项）**：§3.1 串行化 + 收尾归一 → §3.9 测试接缝 → R2-01/R2-03 的 4 类用例。此批次单独提交，不夹带其他改动。
2. **批次二（安全与正确性）**：§3.2 热键录制/reload 守卫、§3.3 engine 加锁、§3.4 AX 范围校验、§3.5 HTTP 限制、§3.6 LLM 判空、§3.7 模型契约四断言。
3. **批次三（清扫）**：§3.8 全部小额项 + 文档订正。

每批次结束都跑 `xcodebuild … test`（当前基线：45 用例、约 3 秒、退出码 0），批次二完成后按报告 §9 的真机清单补做设备切换、AX 插入与剪贴板恢复的手工验证——这些结论在真机跑通前只能标注为"代码审查通过"。
