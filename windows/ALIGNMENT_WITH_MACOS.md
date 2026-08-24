# Windows 实现向 macOS 拉齐方案

> 基线：macOS `3.1.9`（HEAD `5ac5aab`） vs Windows `3.0.0`（自 `089c2a6` 引入后未再改动）。
>
> Windows 版本是在 `089c2a6` 一次性提交的。此后 macOS 又落了 11 个提交
> （`d572f86` → `5ac5aab`，四轮架构评审 R2/R3/R4 系列 + 三轮 UI/首启体验优化），
> 这些修复绝大多数是**平台无关的逻辑缺陷**，Windows 侧原样存在。本文逐条盘点差异、
> 给出 Windows 侧的适配改法与验证方式。
>
> **状态（2026-08-24 更新）**：上述差异已随 commit `3b1d975` 完成**代码层面**的拉齐，
> Windows 随之发布 3.1.0。阅读时请注意：
>
> - 正文保留撰写时的方案口吻（"改法 / 预估 / 建议"），实际实现以仓库代码为准；个别条目与
>   方案存在偏差，例如 W-15 的权限轮询间隔方案写 2s、实现取 4s（理由见 `DESIGN.md` §13.2）。
> - §8 测试补强清单中已落地：`AppConfigValidationTests`、`LlmEndpointTests`、
>   `AudioChunkerTests`（新增），以及 `LlmCorrectorTests`、`FbankParityTests` 的扩充；
>   `LocalAsrSessionTests`、`AsrServiceTests`、`ModelDownloaderTests`、
>   `VoiceTyperControllerTests`、`LfrCmvnTests` **尚未创建**（其中会话层与加载互斥的行为
>   已在实现对齐，仅缺对应测试，见 `windows/DESIGN.md` §8 的待补清单）。
> - §10 手工验证清单 13 条都需要真实 Windows 设备，截至本文更新**一条都未执行**——
>   项目尚未在真机上编译运行过（见根 README「Windows」一节与 `windows/DESIGN.md`
>   顶部状态框）。

---

## 0. 调研方法与证据等级

- 逐文件对读 `macos/Sources/VoiceTyper/**` 与 `windows/**` 的全部 13k 行源码，
  以及 `d572f86..5ac5aab` 的全部提交 diff。
- **本次调研没有编译、没有运行任何一侧代码**：本机无 .NET SDK、无 Windows 机器。
  下文所有 Windows 结论均来自代码阅读与跨平台逻辑比对，标注为「代码审查」；
  凡涉及性能、内存、真机行为的判断都单独标注「需真机复测」。
  这与 `windows/DESIGN.md` 开头的证据分级约定一致。
- 每条都给出 `文件:行` 证据与对应的 macOS 提交/评审编号，便于核对。

---

## 1. 结论摘要

| 分级 | 项数 | 性质 |
| --- | --- | --- |
| **P0 阻断** | 1 | Windows 当前**编译不过**，任何拉齐工作都必须先解决 |
| **P0 隐私/数值** | 3 | 违反 `AGENTS.md` 明文约束（用户文本进日志）、跨平台数值分歧 |
| **P1 用户可感知** | 13 | 丢文本、丢剪贴板、写错窗口、静默失效、无法诊断 |
| **P2 正确性/稳定性** | 11 | 竞态、内存翻倍、契约缺校验、性能退化 |
| **P3 体验/分发** | 7 | HUD 瞬态、暂停、自动下载、签名、版本、文档 |

最重要的三件事，按顺序：

1. **`AsrState.DownloadingModel` 不存在**，5 处引用 → Windows 工程压根编译不过（§2）。
2. **识别文本与 LLM 响应体被写进日志**（§3.1 / §3.2）——`AGENTS.md` 明确禁止，
   macOS 在 `d572f86` 已修，Windows 原样保留。
3. **fbank 对数下限写错 31 个数量级**（§3.3）——两个平台共用同一份金标准夹具，
   但该夹具恰好覆盖不到这个分支，静音帧特征在两个平台上不一致。

---

## 2. P0 阻断：Windows 工程编译不过

### W-00 `AsrState.DownloadingModel` 未定义

- **证据**：`windows/Asr/AsrService.cs:10` 定义
  `internal enum AsrState { Unloaded, Loading, Ready, ModelMissing, Failed }`，
  不含 `DownloadingModel`；而以下 5 处引用了它：
  - `windows/App/AppCoordinator.cs:403`
  - `windows/UI/SetupForm.cs:164`、`:170`、`:203`、`:577`
- **后果**：CS0117，`dotnet build` 直接失败。`windows/DESIGN.md` 已声明本轮调研
  「无 .NET SDK，无法编译」，这个错误就是那个前提的直接后果。
- **改法**：**不要**给 `AsrState` 加成员。macOS 侧 `ASRService.State` 同样不含下载态——
  下载是 `AppCoordinator` 的职责（`isDownloadingModel` + `modelDownloadProgress`），
  不是引擎状态。正确修法是把这 5 处改为读已经传进来的 `downloadProgress is not null`
  / `_isDownloadingModel`，与 macOS 的 `syncSetupWindow(downloadProgress:)` 结构一致。
  `SetupForm.UpdateStatus` 的签名里已经有 `double? downloadProgress` 参数，直接用即可。
- **验证**：`dotnet build` + `dotnet test` 通过（这也是**建立可验证基线**的前提，
  后续每一批改动都应以此为门槛）。

> ⚠️ 前置风险：本仓库的 Windows 代码**从未被编译过**。修完 W-00 之后极可能还有
> 其它编译错误。阶段 0 的第一件事必须是在一台装了 .NET 10 SDK 的机器上把
> `dotnet build` / `dotnet test` 跑绿，再谈功能拉齐。

---

## 3. P0：隐私与数值正确性

### W-01 识别文本被写进日志

- **证据**：`windows/App/AppCoordinator.cs:233`
  `controller.RecognizedText = text => AppLog.Info("coordinator", $"识别结果: {Truncate(text, 80)}");`
- **macOS 对应**：`d572f86`「识别文本与 LLM 响应体不再写入日志」，现为
  `AppLog.app.info("识别完成 chars=\(text.count)")`。
- **约束**：`AGENTS.md`「日志不得包含 API Key、完整敏感配置或不必要的用户文本」。
  Windows 的 `AppLog` 是明文落盘到 `%APPDATA%\VoiceTyper\logs\app.log`，
  等于把用户听写过的每一句话前 80 字永久留档。
- **改法**：改为 `$"识别完成 chars={text.Length}"`。
- **验证**：新增单测断言 `AppLog` 写入内容不含样本文本（或以代码评审为准 + grep 兜底）。

### W-02 LLM 响应体被写进异常消息并落日志

- **证据**：`windows/Llm/LlmCorrector.cs:157`
  `throw new LlmException($"LLM API 错误 ({code}): {Truncate(body, 300)}");`
  该异常在 `:108` 被 `AppLog.Warn("llm", $"LLM 纠错失败，使用原始文本: {ex}")` 记录，
  响应体（可能回显了送去的识别文本）因此落盘。
- **macOS 对应**：`d572f86` + `a00f1c0`。macOS 侧收敛为稳定的领域错误
  `LLMError.invalidResponse / .httpStatus(Int) / .malformedResponse`，
  只暴露状态码，不带响应正文，且 JSON 解析失败也不把 `NSJSONSerialization` 的
  实现细节写进日志。
- **改法**：定义与 macOS 对齐的错误枚举（C# 用 `LlmException` 的子类或带
  `Kind` 的 record），消息只含状态码；解析失败统一为「LLM 响应格式无法解析」。
- **验证**：扩充 `LlmCorrectorTests`，断言 500 + 带正文的响应产生的异常消息不含正文。

### W-03 fbank 对数下限与 macOS 差 31 个数量级

- **证据**：`windows/Asr/FbankFrontend.cs:47`
  `private const float FloatMin = 1.17549435E-38f;`（FLT_MIN），`:184` 用它做下限。
- **macOS 对应**：`bb25282`（R4-10）把 `Float.leastNormalMagnitude`（同为 FLT_MIN）
  改成 `Float.ulpOfOne`（FLT_EPSILON ≈ 1.19209290e-7），并注明这是对齐
  `kaldi_native_fbank` 的 `std::numeric_limits<float>::epsilon()`，
  已用真实全零输入实测 Python 参考值确认：全零帧的对数能量应为
  `log(FLT_EPSILON) ≈ -15.942385`，而不是 `log(FLT_MIN) ≈ -87.3`。
- **为什么两边的金标准测试都没抓到**：`dump_reference_fixtures.py` 的合成信号是
  正弦+噪声，能量恒不为零，永远走不到下限分支。`bb25282` 因此单独补了一条
  全零输入回归测试。Windows 复用同一份夹具（见 `Tests/VoiceTyper.Tests/*.csproj`
  的 `<None Include="..\..\..\macos\Tests\...">`），所以也一样测不到。
- **踩坑提醒**：C# 里 `float.Epsilon` **不是** FLT_EPSILON，而是最小次正规数
  （≈1.4e-45）。现有代码注释「不是 `float.Epsilon`（次正规数）」这句话本身是对的，
  但由此选了 FLT_MIN 是错的。正确写法是显式常量
  `private const float LogFloor = 1.1920929E-7f;`。
- **改法**：换常量 + 同步更新注释 + 补一条全零输入回归测试（对齐 macOS
  `FbankParityTests.testAllZeroInputHitsDocumentedLogFloor`）。
- **附带收益**：`bb25282` 已把夹具时长从 0.62s 改为 0.63s（61 帧），使
  `applyLFR` 的尾帧补齐分支首次被覆盖。Windows 的 `LfrCmvn.ApplyLfr` 逻辑经比对
  与 macOS 一致（含尾帧补齐），因此这次是**白拿一份新覆盖**，不需要改实现——
  但必须重跑 `FbankParityTests` 确认。

---

## 4. P1：用户可感知的缺陷

### W-04 LLM 只回标签时整段文本被吞（R2-08）

- **证据**：`windows/Llm/LlmCorrector.cs` 剥掉 `<asr_text>` 包裹后**直接返回 content**，
  没有「剥完变空则回落原文」的判断。
- **macOS 对应**：`cb9c6ca`（R2-08）——判空必须放在剥标签**之后**，
  否则 `<asr_text>\n</asr_text>` 这类响应剥离前非空、剥离后为空，会把整段听写文本丢掉。
- **改法**：剥标签后 `if (string.IsNullOrEmpty(content)) return text;`
- **验证**：单测：mock 返回 `<asr_text>\n</asr_text>` → 断言返回原文。

### W-05 Base URL 无结构化校验，且会重复拼接路径（F-03 / R2-07）

- **证据**：`windows/Llm/LlmCorrector.cs` 的 `TrimmedBaseUrl()` 无条件拼
  `/chat/completions`；`VoiceTyperController` 只检查 `!IsNullOrWhiteSpace(BaseUrl)`。
- **macOS 对应**：`d572f86` 引入 `LLMEndpoint`，`cb9c6ca`(R2-07) 加固：
  1. 结构化解析，非法输入在设置页就报错而不是请求期崩；
  2. **明文 HTTP 只放行回环/私网/`.local`**——识别文本与 `Authorization: Bearer <key>`
     全程明文，放行任意公网 host 等于把两者交给链路上任何中间人；
  3. 已含 `/chat/completions` 后缀时不重复追加。
- **Windows 现状的直接后果**：用户按 OpenAI 官方文档填
  `https://api.openai.com/v1/chat/completions` → 实际请求
  `.../chat/completions/chat/completions` → 404，且（因 W-06）界面只说「未产生预期结果」。
- **改法**：新增 `windows/Llm/LlmEndpoint.cs`，逐条直译 macOS 的 `LLMEndpoint`：
  `Uri.TryCreate` + scheme 白名单 + host 判定（`localhost` / `*.local` / `::1` /
  127.0.0.0/8 / 10/8 / 172.16-31/12 / 192.168/16 / 169.254/16）+ 后缀去重。
  `LlmCorrector.Config` 改为接收已解析好的完整 `Uri`（与 macOS 的
  `chatCompletionsURL: URL` 一致），把校验前移。
- **验证**：新增 `LlmEndpointTests`，直译 macOS `LLMEndpointTests` 的 12 条用例。

### W-06 「测试纠错」看不到真实失败原因（R3-13）

- **证据**：`windows/App/AppCoordinator.cs` 的 `TestLlmCorrectionAsync` 返回
  `bool`，判据是 `result != sample && !string.IsNullOrEmpty(result)`；
  而 `CorrectAsync` 任何失败都回落原文 → **「网络不通」和「模型认为无需修改」
  被显示成同一个结果**。
- **macOS 对应**：`b94b31e`（R3-13）新增 `LLMCorrector.test()` 抛错版本，
  `AppCoordinator.testLLMCorrection` 返回 `Result<String, SimpleMessageError>`，
  设置页展示 401 / 超时 / 网络不通的真实原因。
- **改法**：`LlmCorrector` 增加 `TestAsync`（不吞异常）；
  `OnTestLlmCorrection` 签名改为 `Func<LlmConfig, string, Task<LlmTestResult>>`
  （`record LlmTestResult(bool Ok, string Message)`），`SetupForm` 展示 `Message`。
- **验证**：单测覆盖 401 / 超时 / 正常三条路径的消息文案。

### W-07 插入前不校验焦点（F-10）

- **证据**：`windows/Services/TextInsertionService.Insert` 直接 SendInput。
- **macOS 对应**：`d572f86`（F-10）记录录音开始时的前台 pid，插入前比对，
  不一致则**不插入**，只复制到剪贴板并提示「目标窗口已变化」。
  理由是最坏情况会把听写内容写进用户没预期的窗口（例如另一个应用的密码框）。
- **Windows 适配**：`BeginLocalRecording` 时记 `GetForegroundWindow()`（HWND，
  必要时连 `GetWindowThreadProcessId` 的 pid 一起记，防止 HWND 复用），
  插入前比对；不一致 → 只写剪贴板 + `AppState.Error("目标窗口已变化，结果已复制到剪贴板")`。
  `NativeMethods` 里这两个 P/Invoke 都已存在，不需要新增互操作。
- **验证**：手工——按住热键说话，松手前 Alt+Tab 切到别的窗口，确认不插入且有提示。

### W-08 剪贴板：听写内容进 Win+V 历史与云剪贴板（R3-15）

- **证据**：`windows/Services/TextInsertionService.cs:38`
  `Clipboard.SetDataObject(text, copy: true, ...)`，无任何排除标记。
- **macOS 对应**：`b94b31e`（R3-15）给写入项打上
  `org.nspasteboard.ConcealedType` / `TransientType`，剪贴板历史工具直接跳过。
- **Windows 对应物**（等价且更彻底，因为是系统级而非社区约定）：在 `DataObject` 上
  附加三个剪贴板格式——`ExcludeClipboardContentFromMonitorProcessing`、
  `CanIncludeInClipboardHistory`（值 0）、`CanUploadToCloudClipboard`（值 0）。
  这样 Win+V 历史与跨设备云剪贴板都会跳过听写内容，与「音频仅在设备端处理」的
  隐私承诺一致。
- **改法**：把 `SetDataObject(text, ...)` 换成构造 `DataObject`，
  `SetText` + 三个排除格式，再 `Clipboard.SetDataObject(obj, copy: true, ...)`。
- **验证**：手工——听写一段，按 Win+V 确认历史里没有这条。

### W-08b 剪贴板备份是活引用而非快照（Windows 独有的同类缺陷）

- **证据**：`windows/Services/TextInsertionService.cs:28`
  `backup = Clipboard.GetDataObject();` 拿到的是对**原剪贴板所有者**
  `IDataObject` 的 COM 引用，不是数据快照；500ms 后
  `RestoreClipboardIfUnchanged` 才去读它。原所有者进程若已退出或已失效，
  恢复会静默失败，用户原剪贴板内容永久丢失。
- **macOS 对应**：`snapshotPasteboard()` 是逐 type 复制 `Data` 的**真快照**。
- **改法**：备份时立即遍历 `backup.GetFormats()` 逐格式 `GetData` 复制进一个
  本地 `DataObject`；失败的格式跳过并记数（不记内容）。
- **顺带对齐**：
  - 恢复延迟 500ms → **1000ms**（macOS `d572f86` 已把 500ms 判定为「对部分慢应用偏短」）。
  - 原剪贴板为空时（`backup is null`）当前**不做任何恢复**，识别文本会永久留在剪贴板；
    macOS 是无条件清空后恢复快照。应对齐为「空快照也要清掉我们写入的内容」。
  - `SetDataObject` 写入失败时 macOS 会先恢复用户原内容再返回失败；Windows 当前直接
    返回 false（此路径下剪贴板确未被改动，可接受，但要在注释里写明是有意为之）。
- **验证**：手工——复制一张图片 → 听写 → 确认 1 秒后剪贴板恢复成那张图片。

### W-09 单段录音上限 300s，且达上限后静默丢音（F-07b / R3-03）

- **证据**：`windows/Asr/LocalAsrSession.cs:34` `MaxSessionSamples = 300 * 16000`；
  `:86` 只发一次 `OnWarning`，之后新音频**静默丢弃**，会话继续挂着。
- **macOS 对应**：
  - `d572f86`（F-07b）：300s → **120s**。理由：桌面听写 5 分钟不是合理假设，
    更长会话意味着更大的 finalize 峰值内存与耗时；要恢复 300s 需先补
    60/90/300 秒的峰值 RSS 与 finalize 耗时实测。
  - `b94b31e`（R3-03）：新增 `onSessionCapped` 回调，控制器据此**主动走一次与松键
    完全相同的收尾路径**，把已录到的内容正常上屏。因为 HUD 的警告只闪 1.2s，
    用户完全无法察觉自己正对着一个已停止收音的会话继续说话。
- **改法**：常量改 `120 * AppConstants.TargetSampleRate`；
  `LocalAsrSession` 加 `Action? OnSessionCapped`；
  `VoiceTyperController` 收到后调 `_audioService.Stop()`（走 `OnTailChunk` → finalize）。
- **验证**：单测（假引擎 + 灌满 120s 样本，断言 `OnSessionCapped` 触发一次）；
  手工——按住热键连续说 2 分钟以上，确认自动收尾并上屏。

### W-10 录音中无法按 Esc 取消

- **证据**：`windows/Services/HotkeyService.cs` 只有 `OnPress` / `OnRelease`。
- **macOS 对应**：`HotkeyService.onCancel` + `VoiceTyperController.cancelByUser()`
  + `AppCoordinator.onCancelled` + HUD `showCanceled()`。
- **Windows 适配**：低级键盘钩子里在热键分支**之前**加 Esc 分支
  （`_isActive && isKeyDown && vk == VK_ESCAPE`），不吞事件（`CallNextHookEx` 照常调用，
  与 macOS 的 `listenOnly` tap 语义一致）。控制器侧走「不发 finalize、直接收尾」路径。
- **验证**：手工——按住热键说话，中途按 Esc，确认 HUD 显示「已取消」且不上屏。

### W-11 一次性错误后托盘永久停在错误态（R3-04）

- **证据**：`windows/App/AppCoordinator.cs` 的 `StateChanged` 里
  `AppState.Error` 之后没有任何回落逻辑。
- **macOS 对应**：`b94b31e`（R3-04）`scheduleDictationErrorRecovery()`，
  2.5s 后自愈回 `.idle`，与 HUD 的错误自动隐藏时长一致。
- **改法**：`System.Windows.Forms.Timer`（2.5s，one-shot），
  `Recording`/`Idle` 时取消；触发时若仍是 Error 则切 Idle。
- **验证**：单测不易；手工——制造一次插入失败，确认托盘 2.5s 后回「就绪」。

### W-12 保存设置会销毁进行中的听写（R2-04 / F-13）

- **证据**：`windows/App/AppCoordinator.cs` 的 `ApplyConfigAsync` → `ReloadAndReevaluateAsync`
  无条件 `_controller.Stop(); _controller.Dispose();`，不看当前是否正在听写；
  也不区分「只改了 UI 透明度」。
- **macOS 对应**：
  - `d572f86`/`cb9c6ca`：`applyConfig` 判断 `onlyUIChanged`；非 UI-only 且
    `currentState.isActiveDictation` → 抛 `ConfigSaveError.dictationInProgress`，
    界面提示「正在录音/识别/输入，请等待当前听写完成后再保存此项设置」。
  - `cb9c6ca`（R2-04）：录制热键时用 `suspendHotkeyListening()` / `resumeHotkeyListening()`
    **只停按键监听、不销毁听写**；且有进行中听写时拒绝暂停请求。
- **改法**：
  1. `AppState` 增加 `IsActiveDictation` 判定（Recording/Recognizing/Inserting）；
  2. `ApplyConfigAsync` 比较草稿与当前配置的 asr/llm/hotkey 段，
     UI-only 走「只更新内存配置 + `_hud.ApplyOpacity`」路径；
  3. 非 UI-only 且正在听写 → 抛异常，`SetupForm` 已有 `catch` 展示消息，无需改 UI；
  4. `VoiceTyperController` 增加 `SuspendHotkeyListening()` / `ResumeHotkeyListening()`。
- **验证**：手工——听写过程中在设置页点「保存并应用」，确认被拒绝且内容不丢。

### W-13 API Key 读写失败静默（R4-06）

- **证据**：`windows/Core/SecretStore.cs`
  - `SaveLlmApiKey` 返回 `void`，异常只记日志 → 用户点「保存」看到「设置已保存并生效」，
    实际密钥没落盘；
  - `LoadLlmApiKey` 把「从未保存」与「DPAPI 解密失败」都压成 `""`。
    DPAPI 跨用户/跨机器搬运配置目录后必然解密失败，表现为「智能纠错开着但一直 401」，
    界面上没有任何信号指向密钥本身。
- **macOS 对应**：`bb25282`（R4-06）新增 `KeychainStore.loadLLMAPIKeyResult()`
  区分 `errSecItemNotFound`（正常）与真正的读取失败，设置页展示真实原因；
  并且保存回滚只在读取成功时执行，避免误删真实 key。
- **改法**：`SecretStore` 增加
  `enum SecretReadStatus { Ok, NotSaved, Failed }` +
  `(SecretReadStatus, string) LoadLlmApiKeyResult()`，`SaveLlmApiKey` 返回 `bool`；
  `SetupForm.LoadEditableContent` 在 `Failed` 时展示
  「无法读取已保存的 API Key，请重新填写并保存」；`HandleSaveRecognition` 在保存失败时
  不报「已保存」。
- **验证**：单测（注入假路径/损坏文件）；手工——把 `llm_api_key.dat` 改坏，
  确认设置页给出明确提示。

### W-14 配置完全没有校验（F-13 / R2-12 / R4-05）

- **证据**：`windows/Core/AppConfig.cs` 没有任何 `Validated()`；
  `ConfigStore.LoadOrCreate` / `Save` 都不校验。
- **macOS 对应**：`AppConfig.validated()`（`d572f86` F-13 引入，
  `cb9c6ca` R2-12 加 NaN 处理，`bb25282` R4-05 加热键裸键校验），
  在 `ConfigStore` 的 load **和** save 两侧都调用。
- **Windows 现状的具体后果**（`README` 鼓励手改 YAML，「打开配置目录」就在托盘菜单里）：
  - `hotkey: {key: d, modifiers: []}` → `HotkeyService.MapKeyToVk("d")` 返回有效 vk，
    `BuildExpected([])` 返回 `None`，于是**在任何应用里正常打字敲 d 都会触发一次录音**；
  - `threads: 9999` → 直接传给 ORT `IntraOpNumThreads`；
  - `timeout: -1` → `Math.Max(1, ...)` 侥幸兜住，但 `temperature: .nan` 会让
    `JsonSerializer.Serialize` 产出 `NaN` 字面量（非法 JSON），每次纠错静默失败；
  - `opacity: 5.0` → `RecordingHud` 构造里 `Math.Clamp` 兜住了，但 `preview_window: -1`
    没人兜。
- **改法**：新增 `AppConfig.Validated()`，逐项对齐 macOS 的范围
  （`threads` 0–32、`idle_unload_minutes` 0–1440、`temperature` 0–2、
  `max_tokens` 64–8192、`timeout` 1–120、`opacity` 0.1–1.0，
  外加 Windows 独有的 `preview_window` 0–30）；
  非有限浮点重置为下限并 warning；热键校验为
  「主键必须在 `MapKeyToVk` 支持表内」且「必须至少带一个修饰键」，
  否则回落默认 `Ctrl+F2` 并 warning。`ConfigStore` 的 load/save 两侧调用。
- **验证**：新增 `AppConfigValidationTests`，直译 macOS `AppConfigTests` 的用例。

### W-15 权限页无自愈轮询

- **证据**：`SetupForm` 有「重新检测」（`OnRetryMicProbe`），但没有自动轮询。
- **macOS 对应**：`bb25282` 权限页可见且权限未齐时 2s 轮询；
  `5ac5aab`（R4-14）修了轮询抢焦点的问题——轮询必须走
  `refreshPermissionsWithoutStealingFocus()`，只刷新勾选状态，
  只有权限全齐时才转交完整的 `reevaluateReadiness()`。
- **Windows 适配**：`SetupForm` 可见 + 权限页选中 + `_micAccessDenied` 时启 2s Timer
  调 `OnRetryMicProbe`；**绝不能**在轮询路径里调 `Present()`/`Activate()`
  （现有 `ProbeMicrophone(isFirstProbe:false)` 已经只 `SyncSetupWindow()`，
  天然满足 R4-14，改动时不要破坏这一点）。
- **附带风险（Windows 独有，需真机复测）**：`MicPermissionProbe.TryProbe` 是靠
  真开一次 WASAPI 采集来探测的，2s 一次会让系统托盘的「麦克风使用中」指示灯
  反复闪烁。建议轮询间隔放宽到 3–5s，或改用
  `MMDeviceEnumerator.GetDefaultAudioEndpoint` 只探测设备存在性、
  仅在用户点「重新检测」时才做真开探测。
- **验证**：手工——在设置里关掉麦克风权限，打开权限页，去系统设置里打开，
  确认界面自动变绿且**期间系统设置窗口不被抢焦点**。

---

## 5. P2：正确性与稳定性

### W-16 音频采集竞态：尾音丢失 + 回调乱序（R3-01）

- **证据**：`windows/Services/AudioCaptureService.cs`
  - `OnCaptureDataAvailable` 在锁内查 `_running`，但随后的 `AppendSamples`（`:202`）
    在锁内写 ring buffer 时**不再复查** `_running` → `Stop()` 若插在中间，
    这批样本进了一个已被 drain、此后再也不会被读取的缓冲区（松键前最后几十毫秒静默丢失）；
  - `OnChunk`（`AppendSamples` 末尾）与 `OnTailChunk`（`Stop()` 中）都在**锁外**触发，
    两者之间没有强制先后关系 → 尾音可能先于最后一个整块到达会话。
- **macOS 对应**：`b94b31e`（R3-01）把 `isRunning` 与分帧缓冲放进同一把锁，
  并让**投递动作本身也在锁内入队**到一条串行 `deliveryQueue`，
  使入队顺序严格等于临界区顺序，一次性解决两个问题。
- **改法**：
  1. `AppendSamples` 在锁内先 `if (!_running) { _droppedNotRunning++; return; }`；
  2. 引入一条串行投递队列（一个专用线程 + `BlockingCollection`，或复用
     `UiDispatcher.Post` 但**在锁内调用 Post**）——关键是入队发生在锁内；
  3. 拆出纯逻辑的 `AudioChunker`（累积 → 吐定长块，`Drain()` 吐尾音），
     与线程/WASAPI 无关，可脱离真实麦克风单测（对齐 macOS `AudioChunkerTests`）;
  4. 丢帧计数分两类：转换/处理失败（warn）与 stop 后迟到（info），
     对齐 `b94b31e` + `bb25282` 的 R4-07；日志只记数量不记内容。
- **验证**：新增 `AudioChunkerTests`（直译 macOS 的 4 条用例）；
  竞态本身以代码评审 + 手工「快速按松热键 20 次，确认每次都有结果且不串台」为准。

### W-17 并发加载导致内存翻倍 + 空闲卸载自触发重载（R3-02 / F-04）

- **证据**：`windows/Asr/AsrService.cs` 的 `PreloadAsync` / `ReloadAsync` 之间无互斥；
  `UnloadNowAsync` 把状态置回 `Unloaded`，而 `AppCoordinator`
  的 `_asrService.OnStateChange = _ => ReevaluateReadinessAsync()` 会无差别转发，
  其 `case AsrState.Unloaded: _ = _asrService.PreloadAsync();` 分支立刻又发起一次加载——
  **空闲卸载刚把模型卸掉，马上又装回来**，空闲卸载功能等于失效。
  更糟的是两次 `LoadAsync` 会各自构建一份约 500MB 的 ORT session，
  在 `_engine = built` 替换之前短暂同时存活。
- **macOS 对应**：
  - `d572f86`（F-04）新增 `.suspendedForIdle` 状态：语义上仍视为「就绪」，
    热键监听正常，下次 `makeSession()` 按需加载，不触发自动预加载；
  - `b94b31e`（R3-02）`requestLoad(unloadFirst:)` 统一入口 + `isLoadInFlight`
    + `reloadRequestedWhileLoading`，保证同一时刻只有一次 `load()`。
- **改法**：`AsrState` 增加 `SuspendedForIdle`；`UnloadNowAsync(dueToIdle: bool)`；
  `RequestLoadAsync(bool unloadFirst)` 作为 `PreloadAsync`/`ReloadAsync` 的唯一入口，
  带 `_isLoadInFlight` / `_reloadRequestedWhileLoading`；
  `AppCoordinator` 的就绪判定把 `SuspendedForIdle` 与 `Ready` 一视同仁。
- **验证**：单测（注入假 `IdleUnloadScheduler` + 假引擎工厂，对齐 macOS `ASRServiceTests`）。
  这需要先把 `AsrService` 的模型定位与引擎构造抽成可注入的委托——macOS 已这么做了
  （`modelLocate` / `engineFactory` 构造参数），Windows 照抄即可。

### W-18 引擎引用跨线程无同步（N-01）

- **证据**：`windows/Asr/AsrService.cs` 把 `() => _engine` 交给 `LocalAsrSession`，
  会在 **UI 线程**读（`EnsureBufferIfPossible`），而写发生在 **AsrPump 线程**
  （`_pump.PostAsync(() => { _engine = built; })`）。
- **macOS 对应**：`cb9c6ca`（R2-06a/N-01）改为 `engineLock` 保护的
  `currentEngine()` / `setEngine()`。
- **改法**：用 `lock` 或 `Volatile.Read/Write` 保护 `_engine` 的读写
  （引用赋值在 CLR 上是原子的，但没有内存屏障就没有可见性保证）。
- **验证**：代码评审。

### W-19 加载完成后语言快照过期（R4-13）

- **证据**：`UpdateConfig` 语言变化时 `_pump.Post(() => _engine?.SetLanguage(lang))`，
  与 `LoadAsync` 里的 `_pump.PostAsync(() => { _engine = built; })` 的相对顺序不保证。
  若前者先执行，作用在旧引擎或 `null` 上，随后被 `_engine = built` 覆盖，
  语言设置静默丢失，要等下一次 reload 才生效。
- **macOS 对应**：`5ac5aab` 前一批（R4-13）——装载完成那一刻用最新的
  `config.language` 重放一次 `setLanguage`。
- **改法**：`_engine = built` 之后紧接着 `built.SetLanguage(_config.LanguageValue)`
  （在同一个 pump 任务里）。
- **验证**：单测（假引擎记录 `SetLanguage` 调用序列）。

### W-20 `Close()` 之后已入队的推理仍会跑完（F-07d）

- **证据**：`windows/Asr/LocalAsrSession.Close()` 只置 `_closed` 与清 buffer；
  已投递给 `AsrPump` 的 `Preview()`/`Finalize()` 闭包仍会完整跑一遍 CTC 解码，
  结果再被丢弃。120s 音频的 finalize 是秒级的，白白占着串行队列，
  会让紧接着的下一次听写排队等待。
- **macOS 对应**：`d572f86`（F-07d）+ `cb9c6ca`（N-01）：带锁的 `CancelFlag`，
  asrQueue 闭包跑之前先短路判断。
- **改法**：`LocalAsrSession` 内加一个线程安全的取消标志（`volatile bool` 或
  `CancellationTokenSource`），`_pump.Post` 的闭包首行检查。
- **验证**：单测（假引擎计数 `Recognize` 调用次数，`Close()` 后断言不再增加）。

### W-21 模型契约与维度自洽校验缺失（F-08 / R2-09）

- **证据**：`windows/Asr/SenseVoiceEngine.cs` 构造期不做任何校验，推理期
  `if (logitsResult is null || lensResult is null) return "";` —— **静默当成静音**。
- **macOS 对应**：`d572f86`（F-08）+ `cb9c6ca`（R2-09）四条加载期断言 +
  两条推理期断言。
- **Windows 侧的具体崩溃/静默路径**（侧载 `asr.model_dir` 指向不匹配的模型时）：
  - `lfr_n = 0` → `LfrCmvn.ApplyLfr` 里 `Math.Ceiling((double)originalT / 0)` = `∞`，
    `(int)∞` 在 unchecked 上下文里是 `int.MinValue` → 负长度数组分配异常；
  - `lfr_m × n_mels ≠ am.mvn 维度` → `ApplyCmvn` 里 `IndexOutOfRangeException`；
  - `fs ≠ 16000` → `FbankFrontend` 的帧长计算失真；
  - 模型 I/O 名字不符 → 每次推理都返回 `""`，用户看到「识别不出任何东西」；
  - `vocabSize ≠ tokens.Count` → `CtcDecoder` 静默跳过越界 id，表现为
    「识别成功但缺字」，比报错更难排查。
- **改法**：构造期校验 `LfrM >= 1 && LfrN >= 1 && FbankOptions.NumMelBins > 0 &&
  FrameLength/Shift > 0 && SampleRate == AppConstants.TargetSampleRate`、
  `Cmvn.Means.Length == LfrM * NumMelBins == Cmvn.Vars.Length`、`_tokens.Count > 1`；
  用 `_session.InputMetadata.Keys` / `OutputMetadata.Keys` 校验
  `{speech, speech_lengths, language, textnorm}` / `{ctc_logits, encoder_out_lens}`；
  推理期输出缺失改为 `throw`，`vocabSize != _tokens.Count` 改为 `throw`。
  全部用一个 `ModelContractException`，消息里带实际值便于排查。
- **验证**：单测不易（要真模型）；以代码评审 + 「故意指一个错误的 model_dir，
  确认给出明确错误而不是静默无输出」的手工验证为准。

### W-22 logits 全量拷贝，长录音峰值内存翻倍（F-07a）

- **证据**：`windows/Asr/SenseVoiceEngine.cs:124` `var fullLogits = logitsTensor.ToArray();`
  —— 对 120s 音频，`ctc_logits` 约 2000 帧 × 25055 词表 × 4B ≈ **200MB**，
  `ToArray()` 再复制一份，与 ORT 输出同时驻留。
- **macOS 对应**：`d572f86`（F-07a）+ `cb9c6ca`：`CTCDecoder` 增加零拷贝入口，
  直接在 ORT 输出的底层内存上解码。
- **改法**：`CtcDecoder.Decode` 已经接受 `ReadOnlySpan<float>`，只需把
  `logitsTensor` 转成 `DenseTensor<float>` 取 `.Buffer.Span`（或用
  `logitsResult.AsEnumerable` 之外的 span 访问路径），切 `[..needed]` 后直接传入，
  不再 `ToArray()`。
- **需真机复测**：ORT 1.24.2 的 `DisposableNamedOnnxValue` 在 .NET 上是否稳定暴露
  底层 span，需在真机确认；若不可行，退而求其次只复制前 `validFrames * vocabSize` 个
  元素（当前实现是先全量 `ToArray()` 再切片，白复制了一遍全量）。

### W-23 RecognitionBuffer 每次预览 O(N) 重拼（R3-08）

- **证据**：`windows/Asr/RecognitionBuffer.cs` 用 `List<float[]> _chunks` + `_joined` 缓存，
  `Append` 每次把 `_joined` 置 null → 每 600ms 一次的预览都要把**全部**已累积音频
  重新拼接一遍，开销与总录音时长成正比。
- **macOS 对应**：`b94b31e`（R3-08）改成单条持续追加的扁平缓冲，
  `append` 退化成 O(chunk) 均摊。
- **改法**：改成 `float[] _samples` + `int _n` 的倍增数组（或 `List<float>` +
  `CollectionsMarshal.AsSpan`），`Preview`/`Finalize` 直接取 `AsSpan(0, _n)`。
  注意 `Preview` 与 `Append` 的并发：macOS 靠 Swift Array 的写时复制拿快照，
  C# 侧要么在锁内复制一份、要么用「只增不改 + 读到的长度快照」的无锁读法
  （后者更省内存，但要保证扩容时旧数组仍然有效——用不可变引用交换即可）。
- **验证**：扩充 `RecognitionBufferTests`；性能以真机 120s 录音的 CPU 占用为准。

### W-24 下载进度回调不节流（R3-09）

- **证据**：`windows/Asr/ModelDownloader.cs:174` 每读 64KB 就回调一次，
  241MB → 约 **3800 次**，每次都 `UiDispatcher.Post` 触发
  `UpdateTray()` + `SyncSetupWindow()` 的全量 UI 刷新。
- **macOS 对应**：`b94b31e`（R3-09）节流到「变化 ≥0.5% 或距上次 ≥200ms」，
  首尾两次不节流。
- **改法**：在 `PerformDownloadAsync` 里加 `_lastReportedProgress` /
  `_lastReportTime`（`Stopwatch`），同样的判据。
- **验证**：单测（假 HTTP handler 喂 N 个 chunk，断言回调次数上界）。

### W-25 低级键盘钩子被系统摘除后不自愈

- **证据**：`windows/Services/HotkeyService.cs` 装完钩子后没有任何存活性检查。
- **macOS 对应**：`HotkeyService` 的 tap 回调里处理
  `tapDisabledByTimeout` / `tapDisabledByUserInput` → `reenableTap()`，
  注释写明「若不重新启用，全局热键会静默失效直到重启应用」。
- **Windows 的同类问题**：`WH_KEYBOARD_LL` 的回调若超过
  `HKEY_CURRENT_USER\Control Panel\Desktop\LowLevelHooksTimeout`（默认 5000ms，
  部分系统 300ms）未返回，系统会**直接把钩子卸掉且不通知**——现象与 macOS 完全一致：
  热键突然永久失效。本项目的钩子回调里有 `Marshal.PtrToStructure` +
  `GetAsyncKeyState` + `UiDispatcher.Post`，正常路径很快，但 UI 线程被
  长任务阻塞时回调会跟着卡住（钩子回调是在**安装线程的消息泵**上分发的）。
- **改法**（这是 Windows 侧的**新增设计**，不是直译，需真机验证）：
  1. 钩子回调里维护一个「最近一次收到按键」的时间戳；
  2. 一个 30s 周期的 Timer：若期间用户有输入（`GetLastInputInfo` 的
     `dwTime` 有推进）但钩子回调时间戳没推进，判定钩子已失效 → `Stop()` + `Start()` 重装；
  3. 重装成功/失败都记日志（不记按键内容）。
- **验证**：真机——用一段人为阻塞 UI 线程 6 秒的调试开关触发系统摘钩，
  确认 30s 内自动恢复。**在真机验证通过前，这条应标注为「代码审查通过、未真机验证」。**

### W-26 录音期间音频设备变化无处理（F-15 / R2-03）

- **证据**：`windows/Services/AudioCaptureService.OnCaptureStopped` 只记日志。
  拔掉 USB 麦克风时，WASAPI 会以异常停止采集，用户看到 HUD 一直「录音中」但没有任何进展。
- **macOS 对应**：`AVAudioEngineConfigurationChange` 通知 → 走与正常停止相同的
  尾音刷出路径把已录内容交给会话完成识别，再通过 `onDeviceChanged` 明确告知
  「输入设备已变化，本次录音已结束」；不做自动重建/自动恢复。
- **改法**：`OnCaptureStopped` 中 `e.Exception is not null` 时（或注册
  `IMMNotificationClient` 的 `OnDefaultDeviceChanged`）→ 走 `Stop()`（发尾音）+
  新增 `OnDeviceChanged` 回调 → 控制器转成 HUD 警告。
- **验证**：手工——录音过程中拔掉 USB 麦克风，确认已录内容正常上屏且有提示。

---

## 6. P3：体验、分发与文档

### W-27 HUD 缺 4 个瞬态与电平波形

macOS `RecordingHUDController` 的公开接口：`showHUD` / `hideHUD` / `showPreview` /
`setRecognizing` / **`updateLevel`** / **`showSuccess`** / **`showError`** /
**`showCanceled`** / **`flashWarning`** / `previewOpacity`。
Windows `RecordingHud` 只有前 4 个 + `ApplyOpacity`。

- `UpdateLevel`：依赖 W-16 先提供 `OnLevel`（音频线程算 RMS，
  macOS 是在 `AudioCaptureService.append` 里直接算并回调）。
  WinForms 侧画一条简单的电平条/波形即可，`Invalidate` 频率沿用现有 50ms Timer。
- `ShowSuccess` / `ShowError` / `ShowCanceled` / `FlashWarning`：
  一次性提示 + 可取消的自动隐藏（macOS 用 `DispatchWorkItem`，
  Windows 用 one-shot `Timer` + 显式取消，注意 `b94b31e`/`bb25282` 里两条教训：
  `flashWarning` 要在 `Recognizing` 阶段也生效；自动隐藏任务必须可取消，
  否则会把后一次提示提前收掉）。
- 宽度随预览增长（`bb25282` 恢复了这个行为）：Windows 侧可选，优先级最低。

### W-28 托盘缺「暂停听写」

macOS `AppState.paused` + 状态栏「暂停听写」菜单项。Windows `AppState` 无 `Paused`，
托盘也没有对应菜单项。改法：`AppState` 加 `Paused`，`TrayController` 加可勾选菜单项，
`AppCoordinator.TogglePause()` 对齐 macOS（暂停时 `_controller.Stop()` + 隐藏 HUD）。

### W-29 首启不自动下载模型

- **macOS 对应**：`0277615` + `d572f86` 的 `AutomaticModelDownloadPolicy`——
  每个进程对缺失模型自动尝试**一次**，失败不在同进程内无限重试，下次启动再试
  并复用已保存的断点数据。
- **Windows 现状**：`ReevaluateReadinessAsync` 的 `ModelMissing` 分支只
  `OpenSetup(SetupTab.Recognition)`，等用户手点「开始下载模型」。
- **改法**：直译 `AutomaticModelDownloadPolicy`（一个 `bool _hasAttempted` 即可）。
  Windows 的 `.part` + `Range` 续传天然满足「复用断点」。

### W-30 默认值与独有特性的有意分歧

| 项 | macOS | Windows | 处理 |
| --- | --- | --- | --- |
| `idle_unload_minutes` 默认 | 10 | 5 | 统一为 10（无理由分歧） |
| 单段上限 | 120s | 300s | 统一为 120s（见 W-09） |
| 默认热键 | `fn` | `Ctrl+F2` | **保持分歧**，Windows 无 fn 键 |
| `preview_window` 自校准 | 无 | 有 | **保留 Windows 独有**，在 DESIGN 记为有意分歧 |
| `SetProcessWorkingSetSize` | 无 | 有 | **保留 Windows 独有**（macOS 无对应概念） |

### W-31 安装包签名

- **macOS 对应**：`b94b31e`（R3-16）——`build_xcode.sh` 增加**可选**的
  Developer ID 签名（逐 framework 单独签，不用 `--deep`）+ notarytool 公证 + 装订，
  不设置环境变量时行为完全不变。
- **Windows 对应物**：`signtool` Authenticode 签名（对 `dist\<rid>\VoiceTyper.exe`
  与 Inno Setup 产出的安装包各签一次）。不签名的安装包会触发 SmartScreen
  「未知发布者」警告，与 Gatekeeper 是同一类问题。
- **改法**：`build.bat` 读 `VOICETYPER_SIGN_THUMBPRINT` /
  `VOICETYPER_TIMESTAMP_URL`（默认 `http://timestamp.digicert.com`），
  未设置时跳过、行为不变；README 增加「签名」章节，与 macOS README 的
  「签名与公证（可选）」对称。

### W-32 版本号

Windows `VoiceTyper.csproj` 仍是 `3.0.0`，macOS 已到 `3.1.9`。
两个平台可以各自计版，但 README 里应写明「本 Windows 版本对齐 macOS 3.1.9 的行为基线」，
避免以后再出现这种「一边改了 11 个提交、另一边完全不知情」的漂移。
建议本轮拉齐完成后 Windows 直接发 `3.1.0`。

### W-33 文档同步

- `windows/README.md`：补「签名」章节；「测试」章节更新用例数；
  「日志与排障」补 Esc 取消、暂停听写、目标窗口变化三条新行为。
- `windows/DESIGN.md`：新增一节「与 macOS 的有意分歧」（W-30 表格），
  并把本轮未做的项（如 W-25 的钩子自愈方案）记入「已知风险/待办」。
- 根 `README.md`：若功能表列了平台能力矩阵，同步 Esc 取消/暂停等条目。

---

## 7. 明确不拉齐的项（附理由）

| macOS 能力 | 不拉齐的理由 |
| --- | --- |
| Accessibility（AX）直接插入路径（`kAXSelectedTextAttribute` / `kAXValue`） | Windows 的对应物是 UI Automation 的 `TextPattern`/`ValuePattern`，在 Electron/Chromium/Qt 类应用上覆盖率远低于 macOS AX，且 `ValuePattern.SetValue` 会整字段重建（丢 undo、丢富文本）。剪贴板 + SendInput 已是 Windows 上最可靠的通路。**只补 W-07 焦点校验与 W-08 剪贴板隐私标记，不引入 UIA 插入路径。** |
| `fn` 键作为热键 | fn 由键盘固件处理，不产生扫描码，Windows 拿不到。保持 `Ctrl+F2` 默认。 |
| TCC 式的辅助功能/输入监控权限门 | Windows 无对应概念；`WH_KEYBOARD_LL` 与 `SendInput` 不需要授权（只受 UIPI 提权边界限制，已由 `IsForegroundWindowElevated` 处理）。麦克风隐私设置的探测已有。 |
| SF Symbol 状态栏动效 | 无对应物，Windows 已用自绘状态色点表达同等信息。 |
| 空闲卸载后归还工作集 | 反向：这是 **Windows 独有**的 `SetProcessWorkingSetSize`，保留。 |
| `preview_window` RTF 自校准 | 反向：**Windows 独有**，macOS 固定 15s。保留，并考虑将来反向移植到 macOS。 |

---

## 8. 测试补强清单

macOS 现有 17 个测试文件 / 112 个用例；Windows 只有 6 个文件。按上面的改动，
建议新增/扩充（括号内是对应的 macOS 测试文件）：

| Windows 测试 | 覆盖 | 对应 macOS |
| --- | --- | --- |
| `AppConfigValidationTests`（新） | clamp、NaN、裸键热键回落 | `AppConfigTests` |
| `LlmEndpointTests`（新） | scheme/host 白名单、后缀去重、非法输入 | `LLMEndpointTests` |
| `LlmCorrectorTests`（扩） | tags-only 不丢文本、`finish_reason=length`、异常不含响应体、`TestAsync` 抛错 | `LLMCorrectorTests` |
| `AudioChunkerTests`（新） | 定长切分、`Drain` 尾音、空输入 | `AudioChunkerTests` |
| `LocalAsrSessionTests`（新） | 预览跳过、finalize 看门狗、120s 上限 + `OnSessionCapped`、`Close` 后抑制 | `LocalASRSessionTests` |
| `AsrServiceTests`（新） | 并发加载互斥、语言重放、`SuspendedForIdle` 不自触发 | `ASRServiceTests` |
| `FbankParityTests`（扩） | 全零输入的对数下限回归 | `FbankParityTests` |
| `LfrCmvnTests`（新） | 尾帧补齐分支的结构校验 | `LFRCMVNTests` |
| `ModelDownloaderTests`（新） | 进度节流上界、HTTP 非 2xx、续传 | `ModelDownloaderTests` |
| `VoiceTyperControllerTests`（新） | 单一收尾入口、拒绝重叠听写、插入失败兜底 | `VoiceTyperControllerTests` |

> 前置改造：`AsrService` / `VoiceTyperController` 需要像 macOS 那样把
> 模型定位、引擎构造、热键服务、音频采集、文本插入抽成可注入的接口/委托
> （macOS 侧是 `VoiceTyperControllerDependencies.swift` + 构造参数）。
> 这部分改造本身没有行为变化，但是后续所有单测的基础，应放在阶段 2 的开头做。

**xUnit 的已知局限**（`FbankParityTests` 文件头已写明）：缺夹具时用提前 `return`
代替跳过，测试仍显示为通过。可考虑引入 `Xunit.SkippableFact` 让「跳过」可见，
与 macOS 的 `XCTSkip` 对齐——这是可选项。

---

## 9. 实施顺序与工作量

| 阶段 | 内容 | 预估 | 门槛 |
| --- | --- | --- | --- |
| **0** | 在装有 .NET 10 SDK 的 Windows 机器上修 W-00 及后续暴露的编译错误，跑通 `dotnet build` / `dotnet test` | 0.5–1 天 | 两条命令全绿；这是**所有后续工作的前提** |
| **1** | P0 隐私与数值：W-01 / W-02 / W-03 + 对应测试 | 0.5 天 | 新增测试通过；`FbankParityTests` 用新夹具重跑通过 |
| **2** | 可测试性改造（依赖注入接缝）+ P1 上半：W-04 / W-05 / W-06 / W-13 / W-14 | 1.5–2 天 | 新增 4 个测试文件通过 |
| **3** | P1 下半（用户可感知行为）：W-07 / W-08 / W-08b / W-09 / W-10 / W-11 / W-12 / W-15 | 2–2.5 天 | 手工验证清单第 1–8 项通过 |
| **4** | P2 正确性：W-16 ~ W-24、W-26 | 2.5–3 天 | 新增测试通过 + 手工第 9–12 项 |
| **5** | W-25（钩子自愈，真机验证）+ P3：W-27 / W-28 / W-29 / W-30 | 2 天 | 真机 |
| **6** | W-31 签名 / W-32 版本 / W-33 文档 | 0.5–1 天 | 安装包在干净 Windows 上装得上、SmartScreen 无警告（若有证书） |

合计约 **10–13 个工作日**，其中阶段 0 的不确定性最大（从未编译过的代码库，
可能还有未知的编译/运行期问题）。

阶段 1–2 可以在没有 Windows 机器的情况下写代码，但**无法验证**；
强烈建议整个拉齐工作都在真机上进行。

---

## 10. 手工验证清单

按 `AGENTS.md`「涉及热键、权限、麦克风、剪贴板和真实推理的改动，还要给出手工验证步骤」：

1. **焦点变化**（W-07）：按住热键说话 → 松手前 Alt+Tab → 确认不插入、有提示、内容在剪贴板。
2. **剪贴板历史**（W-08）：听写一段 → Win+V → 确认历史里没有这条。
3. **剪贴板恢复**（W-08b）：复制一张图片 → 听写 → 1 秒后确认剪贴板仍是那张图片。
4. **录音上限**（W-09）：连续说满 2 分钟 → 确认自动收尾并上屏，不是静默丢音。
5. **Esc 取消**（W-10）：按住热键说话 → 按 Esc → HUD 显示「已取消」，不上屏。
6. **错误自愈**（W-11）：在管理员记事本里听写触发 UIPI 失败 → 托盘 2.5s 后回「就绪」。
7. **听写中保存设置**（W-12）：说话过程中点「保存并应用」→ 被拒绝且内容不丢；
   只改透明度时可以随时保存且不打断。
8. **权限自愈**（W-15）：关掉麦克风权限 → 打开权限页 → 去系统设置打开 →
   界面自动变绿，**且系统设置窗口全程不被抢焦点**。
9. **快速连按**（W-16）：快速按松热键 20 次 → 每次都有结果、不串台、不丢尾字。
10. **空闲卸载**（W-17）：设 `idle_unload_minutes: 1` → 等 1 分钟 →
    确认托盘显示「引擎已空闲卸载」且**不会立刻自动重新加载**；再按热键能正常听写。
11. **设备变化**（W-26）：录音中拔掉 USB 麦克风 → 已录内容正常上屏 + 提示。
12. **模型下载**（W-24 / W-29）：清空模型目录 → 首启 → 确认自动开始下载、
    进度条平滑不卡顿、中途取消后重启能续传。
13. **钩子自愈**（W-25，真机）：触发一次系统摘钩 → 确认 30s 内热键恢复。

---

## 11. 顺带值得反向移植到 macOS 的两项

拉齐不必是单向的。Windows 侧有两处 macOS 没有、且是实打实的改进：

1. **`preview_window` 按实测 RTF 自校准**（`AsrService.CalibratePreviewWindowIfNeeded`）：
   macOS 固定 15s 窗口；在低端机上这个窗口偏大。
2. **`.part` 文件自身长度即续传状态**：比 macOS 的 `.resume` 副文件方案简单，
   且不会踩到 CFNetwork 对畸形 resume data 直接 abort 进程的坑
   （`bb25282` 记录的那个坑）。

这两项建议单独立项，不混在本次拉齐里。
