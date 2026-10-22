# VoiceTyper macOS · R8 用户体验打磨实施规格

[← macOS 目录](../README.md) · [架构设计](../DESIGN.md) · [变更记录](../CHANGELOG.md)

| 项 | 内容 |
| --- | --- |
| 状态 | **方案已定，待实施**（本文是实施依据；实施结果与验证状态回写 `DESIGN.md` §11.5 与 `CHANGELOG.md`） |
| 审查基线 | `7bc80c4` / macOS 3.5.1 |
| 目标版本 | 第一、二批均为 3.5.2（版本号不递增），第三、四批按进度定 |
| 编号 | `R8-<组><序号>`，例如 `R8-A1`。代码注释引用编号时只写编号，不写修复经过 |
| 读者 | 实施 Agent / 开发者；产品决策者看 §1–§3 即可 |

> **证据口径**（沿用 `windows/docs/REVIEW_HISTORY.md` §5.1）：
> **S** = 源码可直接证明；**C** = 结合系统 API 契约确认，实际表现仍需运行验证；
> **V** = 待验证风险；**R** = 改进建议。本文全部结论来自代码阅读，**没有在真机上复现过**。
> 交付时必须区分「单元测试通过」与「真机验证通过」，不得把估算写成实测（`AGENTS.md`）。

---

## 1. 目标与原则

产品已经可用、口碑不错。R8 不改识别管线的数值契约，只打磨「最后一公里」，按重要性排序：

1. **说出的话永远找得回来**：识别失败、超时、插入没生效、误按 Esc，用户都有办法拿回文本。
2. **不打扰**：热键和 Esc 不再「穿透」到前台应用；等待时清楚告诉用户在等什么、能做什么。
3. **更懂我**：输出格式统一（中英文之间加空格）、专有名词可定制、常用开关一键可达。

### 1.1 不变量（任何一项改动都不得破坏）

- **识别契约不动**：fbank / LFR-CMVN / CTC 解码 / `TextPostprocessor` / 模型 I/O 与两平台共享的金标准夹具
  （`Tests/VoiceTyperTests/Fixtures/`）保持原样。R8 的文本处理一律放在 `TextPostprocessor` **之后**的新层里。
- **LLM 输入保持 ASR 原貌**：发给 LLM 的仍是未格式化的 ASR 文本——`LLMCorrector` 的 few-shot 输入就是按
  「中英文紧挨着」的真实形态写的（见 `LLM/LLMCorrector.swift` 的 `fewShotMessages` 注释）。
- **隐私**：音频不出设备；识别文本不写日志、不落盘（「最近结果」只在内存）；`DictationMetrics` 不得新增
  `String` 存储属性（有反射测试钉死）；API Key 只在 Keychain。
- **线程**：UI 更新在主线程；推理只在 `asrQueue`；事件 tap 回调里不得同步等待主线程、不得做重活。
- **本地化**：新增的每条 `L(...)` / `LF(...)` 必须在 `Support/Localization/Strings+en.swift` 补英文，
  占位符个数与顺序一致（`LocalizationTests` 会失败）。中文文案中英文之间加空格（现有文案已全部符合，
  扫描结果 0 处违例，新增文案保持）。
- **不静默吞异常**：沿用 `AppLog` 分类记日志。
- **已否决方案不复活**：麦克风常开预录（pre-roll）、按住 / 切换自动判别、`config.yaml` 热重载、
  菜单「打开配置目录」（见 `DESIGN.md` §11.1、§11.2、§12）。

---

## 2. 已定决策

| # | 决策 | 结论 |
| --- | --- | --- |
| R8-D-1 | 中英文之间是否加空格 | ✅ **统一加空格**，开不开智能校对结果一致；不提供开关。规则与 `correction.md` 第 7 条对齐（中文与英文单词之间加半角空格；中文与纯数字之间、全角标点前后不加）。详细规则见 R8-F2 |
| R8-D-2 | 「最近结果」存放位置 | ✅ **只在内存**，最多 10 条，退出即清；菜单可一键清空。不落盘、不进日志 |
| R8-D-3 | 密码输入框里按热键 | ✅ **不开始听写**，浮窗说明原因。理由：听写结果若开了智能校对会发给第三方服务；口述密码本身也不安全 |
| R8-D-4 | 热键是否拦截 | ✅ 有辅助功能权限时改用主动 tap，**只吞被 VoiceTyper 消费的按键**（组合键主键、用于取消的 Esc）；Fn 与单独修饰键的 `flagsChanged` 永不拦截 |
| R8-D-5 | 识别失败时的兜底 | ✅ 与 Windows W-01 对齐：**超时**才把已识别部分复制到剪贴板；其他失败只放进「最近结果」，不覆盖用户剪贴板 |
| R8-D-6 | 数据驱动项 | ✅ 松键补录（R8-D2b）、AX 插入回读后回退粘贴（R8-A3b）、延迟提供剪贴板（R8-A4）**先采数据 / 先验证**，达到门槛再实施 |

---

## 3. 批次总览

| ID | 标题 | 优先级 | 证据 | 批次 | 主要文件 | Windows 同步 |
| --- | --- | --- | --- | --- | --- | --- |
| R8-F2 | 中英文之间统一加空格 | P1 | S | 一 | 新增 `Text/OutputFormatter.swift`、`VoiceTyperController` | **必须**（共享用例夹具） |
| R8-A1 | 终稿看门狗按时长缩放 + 失败兜底 | P1 | S | 一 | `LocalASRSession`、`VoiceTyperController` | 已有（W-01），对齐即可 |
| R8-A2 | 最近结果（内存）与单例插入服务 | P1 | S | 一 | 新增 `Core/DictationHistory.swift`、`AppCoordinator`、`StatusBarController` | 可选（Windows 只有「上一次」） |
| R8-B1 | 热键与 Esc 不穿透 | P1 | S | 一 | `HotkeyService`、新增 `Services/HotkeyEventFilter.swift` | 已有（WR-007） |
| R8-B2 | 安全输入与密码框 | P1 | C | 一 | 新增 `Support/SecureInput.swift`、`VoiceTyperController`、菜单头 | 建议（UIA `IsPassword`） |
| R8-C1 | 校对回落不再被「已输入」盖掉 | P2 | S | 一 | `LLMCorrector`、`LocalASRSession`、HUD | 已有（WT-16） |
| R8-C2 | 校对中再按热键跳过校对 | P2 | R | 一 | `LocalASRSession`、`VoiceTyperController`、HUD | 已有（C3） |
| R8-C3 | 120 秒上限倒计时 | P2 | S | 一 | HUD | 已有（C3） |
| R8-D1 | 预览改为事件驱动（跟嘴） | P2 | R | 二 | `AudioCaptureService`、`LocalASRSession` | 已有（A1） |
| R8-D2a | 采集「松键时仍在说话」度量 | P2 | R | 二 | `VoiceTyperController`、`DictationMetrics` | 已有（`tail_speech`） |
| R8-A3a | AX 插入回读度量 + 网页控件不做整值替换 | P1 | V | 二 | `TextInsertionService` | 不需要 |
| R8-A5 | 剪贴板备份提前到识别阶段 | P2 | S | 二 | `TextInsertionService`、`VoiceTyperController` | 已有（§22.2） |
| R8-B3 | 卡键看门狗 + 锁屏 / 睡眠处理 | P2 | V | 二 | `VoiceTyperController`、`HotkeyService`、`AppCoordinator` | 已有（B3/B4） |
| R8-C4 | 操作提示（结束 / 取消） | P3 | R | 二 | HUD | 部分 |
| R8-C5 | 下载进度详情 + 就绪提示 | P2 | S | 二 | `AppCoordinator`、新增 `Support/DownloadStats.swift`、设置 / 引导 | 已有（E3、F-01） |
| R8-G1 | 菜单栏快捷切换 | P2 | R | 二 | `StatusBarController`、`AppCoordinator` | 可选 |
| R8-H1 | 更新后重新授权引导 | P2 | R | 二 | `AppCoordinator`、引导 / 权限页 | 不需要 |
| R8-F1 | 自定义词汇（已移交 `VOCABULARY_SPEC.md`） | P1（价值） | R | 三 | `Text/`、`LLMCorrector`、`correction.md`、新设置页 | **必须**（schema + 提示词） |
| R8-G2 | 麦克风设备列表 + 测试麦克风 | P2 | S | 三 | 设置「通用」、新增 `Services/MicLevelTester.swift` | 已有（D1/E2） |
| R8-E1 | 录音紧凑态改小胶囊 | P2 | S | 三 | HUD | 不需要 |
| R8-E2 | 浮窗落在焦点窗口所在屏 | P2 | S | 三 | HUD | 不需要 |
| R8-E3 | 「跟随光标」改为跟随插入点 | P2 | R | 三 | HUD、新增 `Services/FocusInspector.swift` | 已有（§22.3） |
| R8-F3 | 连续口述的衔接空格 | P3 | R | 三 | `OutputFormatter`、`TextInsertionService` | 建议 |
| R8-A3b | AX 回读判定未生效时回退粘贴 | P1 | V | 三（数据驱动） | `TextInsertionService` | 不需要 |
| R8-D2b | 松键后补录约 150ms | P2 | R | 三（数据驱动） | `VoiceTyperController` | 已有判据（A6 未做） |
| R8-A4 | 延迟提供剪贴板数据，读完即恢复 | P2 | V | 三（验证驱动） | `TextInsertionService` | 不需要 |
| R8-G3 | 切换界面语言后「立即重启」 | P3 | R | 四 | 设置「通用」 | 不需要 |
| R8-G4 | 智能校对服务预设 | P3 | R | 四 | 设置「识别」 | 建议 |
| R8-G5 | 复制诊断信息 | P3 | R | 四 | `AppCoordinator`、设置 | 已有（D3） |
| R8-D3 | 预览尾部变化淡显 | P3 | R | 四 | HUD | 已有（C1） |

**实施顺序**：批次内按表中顺序。第一批里 R8-F2、R8-A1、R8-A2 是基础（R8-A1、R8-C1 依赖 R8-A2 的历史与格式化），
R8-B1 风险最高，单独提交、单独验证。

---

## 4. 第一批（3.5.2）：不丢字 · 不打扰

### R8-F2 中英文之间统一加空格

**现状**（S）：`ASR/TextPostprocessor.swift:16` 起的两条正则会**删除**中英交界处的空格（与服务端对齐，属于识别契约，
不能改）；而 `Resources/correction.md` 第 7 条要求校对时**加**空格。结果是开不开智能校对，输出风格不一样。

**目标行为**：最终上屏文本、浮窗预览、「最近结果」里的文本，中文与英文单词之间都有一个半角空格，规则确定、可重复，
与是否启用 LLM 无关。

**实现要点**

1. 新建 `Sources/VoiceTyper/Text/OutputFormatter.swift`（新目录无需改生成脚本，`generate_xcodeproj.rb` 按
   `Sources/VoiceTyper/**/*.swift` 收集，但**新增文件后必须重跑一次**脚本）：

   ```swift
   /// 识别文本上屏前的确定性格式化。位于 TextPostprocessor（识别契约）之后，
   /// 不参与金标准比对；规则与 correction.md 第 7 条一致。
   enum OutputFormatter {
       /// 中文与英文单词之间补一个半角空格。只增不删，幂等。
       static func applyCJKLatinSpacing(_ text: String) -> String
       /// 上屏前的完整格式化链。R8-F1 / R8-F3 会在这里扩展。
       static func formatForOutput(_ text: String) -> String
   }
   ```

2. **规则**（按字符边界判断，只在边界两侧都不是空白时插入一个 U+0020）：
   - **汉字集合 H**：`U+3400–U+4DBF`、`U+4E00–U+9FFF`、`U+F900–U+FAFF`。全角标点（`U+3000–U+303F`、`U+FF00–U+FFEF`）
     **不属于** H，因此全角标点前后永不加空格。
   - **ASCII 词元**：连续的 ASCII 可见字符（`!`–`~`）组成的最长片段。词元里至少有一个 `[A-Za-z]` 时，才算
     「英文单词」；纯数字 / 符号的词元（`3`、`50%`、`2026-10-17`）不算——与「中文与数字之间不加空格」一致。
   - **左边界**（汉字后接英文单词）：汉字后面紧跟的字符是英文单词的首字符，且该字符 ∈ `[A-Za-z0-9]` → 插入空格。
   - **右边界**（英文单词后接汉字）：英文单词的尾字符 ∈ `[A-Za-z0-9+#)\]]` 且后面紧跟汉字 → 插入空格。
     （`+#` 照顾 `C++`、`C#`；`)]` 照顾 `foo()`。尾字符是 `, . ; : ! ?` 等句读时**不加**，标点后不插空格。）
   - **跳过整段**：文本含假名（`U+3040–U+30FF`）或谚文（`U+1100–U+11FF`、`U+3130–U+318F`、`U+AC00–U+D7A3`）时原样返回——
     日文 / 韩文排版不用这条规则，局部套用只会更乱。
   - **只增不删**：已有的空格、换行一律保留；LLM 已经加过空格的文本再跑一遍结果不变（幂等）。
   - 实现建议用一次线性扫描（先切出词元再看边界），不要用多条正则叠加，便于和 Windows 逐行对照。

3. **接入点**（全部在 `VoiceTyperController`，`LocalASRSession` 保持输出原始文本）：
   - `session.onPartial`：`previewText = text`（原始，供 R8-A1 兜底）；对外 `onPreviewUpdate?(OutputFormatter.formatForOutput(text))`。
   - `handleFinalText`：`trimmed` 之后、插入之前 `let output = OutputFormatter.formatForOutput(trimmed)`；插入、复制、
     `onRecognizedText`、「最近结果」一律用 `output`。
   - R8-A1 的兜底复制同样先格式化。
   - **不改** `LLMCorrector` 的输入：LLM 收到的仍是原始 ASR 文本。

4. **共享用例夹具**：新建 `Tests/VoiceTyperTests/Fixtures/text_format_cases.json`，Swift 与 C# 两边的测试都读它
   （同时更新 `Fixtures/README.md` 说明来源与用途；Windows 侧后续在 `VoiceTyper.Tests.csproj` 里按现有方式 `Link`）：

   ```json
   {
     "version": 1,
     "cjk_latin_spacing": [
       {"input": "我用Python写代码", "expected": "我用 Python 写代码", "note": "基本左右边界"}
     ]
   }
   ```

   初始用例至少覆盖下表（实施时可增补，**不得删改既有用例的期望值**，规则变更须先改本文）：

   | 输入 | 期望 | 覆盖点 |
   | --- | --- | --- |
   | `我用Python写代码` | `我用 Python 写代码` | 左右边界 |
   | `我用Mac和iPad` | `我用 Mac 和 iPad` | 多个词元、文末 |
   | `iPhone的电池` | `iPhone 的电池` | 文首英文 |
   | `我们3个人` | `我们3个人` | 纯数字不加 |
   | `提升了25%的转化率` | `提升了25%的转化率` | 数字 + 符号不加 |
   | `用5G网络` | `用 5G 网络` | 含字母的数字词元 |
   | `Python3写的脚本` | `Python3 写的脚本` | 词元以数字结尾 |
   | `v1.2版本` | `v1.2 版本` | 词元内含句点 |
   | `C++代码和C#开发` | `C++ 代码和 C# 开发` | `+` `#` 结尾 |
   | `调用foo()函数` | `调用 foo() 函数` | `)` 结尾 |
   | `好的，OK。` | `好的，OK。` | 全角标点前后不加 |
   | `好的OK.我们走` | `好的 OK.我们走` | 英文句点后不加 |
   | `那个登录页面的问题不是 bug，是 feature` | 同输入 | 已有空格，幂等 |
   | `第一行\n第二行用Git` | `第一行\n第二行用 Git` | 多行 |
   | `日本語のAPIを使う` | 同输入 | 含假名整段跳过 |
   | `한국어API` | 同输入 | 含谚文整段跳过 |
   | `https://github.com/oyasmi看看` | `https://github.com/oyasmi 看看` | URL 词元整体不拆 |
   | `Hello world` | 同输入 | 纯英文不变 |
   | （空串） | （空串） | 空输入 |

**测试**：新增 `OutputFormatterTests.swift`，逐条读夹具；另加「对每条期望值再跑一次结果不变」的幂等断言。
`VoiceTyperControllerTests` 增补：插入服务收到的是格式化后的文本；预览回调是格式化后的文本。

**文档**：`README.md` / `README.en.md` 的输出说明加一句「中英文之间自动加空格」；`DESIGN.md` §11.5 记录本决策；
`CHANGELOG.md`。**Windows**：同一份夹具 + C# 等价实现，列入 Windows 下一批（见 §8）。

---

### R8-A1 终稿看门狗按时长缩放 + 失败兜底

**现状**（S）：
- 看门狗固定 30s（`Core/VoiceTyperController.swift:408` 调 `session?.finalize(timeout: 30)`），且计时从
  `finalize()` 被调用时开始，**包含等待引擎加载**的时间（`ASR/LocalASRSession.swift:146` 起）。
- 等引擎最多 5s（`waitForEngineThenFinalize`，50 × 100ms），模型加载失败时也要白等 5s。
- 失败分支 `.failed` 只报错（`VoiceTyperController.swift:489`），用户说了两分钟的内容全部丢失，没有找回途径。

**目标行为**
- 慢机、长录音不再误判超时；真卡死仍会被发现。
- 识别超时：浮窗提示「识别超时，已把识别出的部分复制到剪贴板」，并进入「最近结果」。
- 其他识别失败：浮窗提示「识别失败，可在菜单栏「最近结果」里找回已识别的部分」（仅当有预览文本时追加后半句），
  文本进入「最近结果」，**不覆盖剪贴板**（R8-D-5）。
- 松键后在等模型加载时，浮窗状态行显示「识别中 · 正在加载模型…」；录音中等加载、已攒满 1s 音频时显示
  「录音中 · 模型加载中…」（对齐 Windows F-02）。

**实现要点**
1. `VoiceTyperController` 新增
   `static func finalizeWatchdogTimeout(audioSeconds: Double) -> TimeInterval { min(max(audioSeconds * 5, 30), 300) }`，
   `onTailChunk` 里用 `session.timings.receivedSamples / 16000` 计算。
2. `LocalASRSession`：
   - 看门狗的启动从 `finalize()` 挪到 `runFinalize(on:)`（推理真正派发到 `asrQueue` 的时刻）。
   - 等引擎上限改为 60s（600 × 100ms）；新增构造参数 `engineUnavailable: () -> Bool`（由 `ASRService.makeSession`
     传入，`state` 为 `.failed` / `.modelMissing` 时返回 true），为 true 时立即以「识别引擎尚未就绪」失败，不再空等。
   - `ASRSessionTimings` 新增 `finalizeTimedOut: Bool`，超时回调前置位。
   - 新增 `var onEngineWait: ((EngineWaitStage) -> Void)?`，`enum EngineWaitStage { case recording, finalizing }`：
     录音中 `pendingAudio.count ≥ 16000` 时触发一次 `.recording`；`waitForEngineThenFinalize` 第 3 次轮询（约 300ms）
     仍未就绪时触发一次 `.finalizing`。
3. `VoiceTyperController.finish(_:)`：在清空 `previewText` **之前**取出 `let partial = previewText`。
   `.failed` 分支：
   - `metrics.timings.finalizeTimedOut && !partial.isEmpty` → `let output = OutputFormatter.formatForOutput(partial)`，
     `textInsertionService.copyToClipboard(text: output)`，`onResultAvailable?(output, .partialAfterTimeout)`，
     报错文案用 `L("识别超时，已把识别出的部分复制到剪贴板")`。
   - 否则 `!partial.isEmpty` → `onResultAvailable?(output, .partialAfterFailure)`，报错文案在原 message 后追加
     `L("可在菜单栏「最近结果」里找回已识别的部分")`。
4. `RecordingHUDController` 新增 `setEngineWait(_ stage:)`：只改状态行文案，警告闪现结束后恢复为它；阶段切换时复位。
5. 度量：`summaryLine()` 末尾追加 `finalize_timeout=0|1`（**追加在末尾**，不打乱既有字段顺序）。

**测试**（`LocalASRSessionTests` / `VoiceTyperControllerTests`）：看门狗公式 5 例（2s→30、10s→50、60s→300…）；
超时置位 `finalizeTimedOut`；`engineUnavailable` 为 true 时立即失败；超时有预览 → 复制且历史 1 条；
普通失败有预览 → 不复制、历史 1 条；无预览 → 两者都不发生；`onEngineWait` 每阶段至多一次。

**手工验证**：设置「空闲卸载 5 分钟」并关闭启动预加载 → 重启后立刻按住热键说 3 秒 → 浮窗应出现「录音中 · 模型加载中…」，
结果正常上屏。超时路径可在 Debug 构建临时把公式下限改成 0.1s 验证（不得提交）。

---

### R8-A2 最近结果（内存）与单例插入服务

**现状**（S）：Esc 取消、焦点变化、插入失败、粘贴兜底在 1s 后恢复剪贴板——一旦目标应用没接住，文本就找不回来了。
另：`VoiceTyperController` 默认参数 `TextInsertionService()`，每次保存设置重建控制器都会新建一个插入服务，
`pendingRestore`（待恢复的原始剪贴板）随之丢失（与 Windows WT-08 同类问题）。

**目标行为**
- 菜单栏菜单在 header 之后新增：
  - 「复制上一次结果」：没有结果时禁用。
  - 「最近结果」子菜单：最多 10 条，新的在上；每条显示前 30 个字符（换行替换为空格，超出加 `…`）；非正常输入的条目
    加后缀「（未输入）」「（已取消）」「（部分）」；末尾分隔线 +「清空最近结果」。
  - 点击任一条：写入剪贴板（沿用 concealed / transient 标记），浮窗提示「已复制到剪贴板」——**即使浮窗位置设为
    「不显示」也要提示**（用户主动操作）。
- 焦点变化 / 插入失败的报错文案保持现状（文本已复制）。

**实现要点**
1. 新建 `Core/DictationHistory.swift`：

   ```swift
   @MainActor
   final class DictationHistory {
       enum Kind { case inserted, copiedFocusChanged, copiedInsertFailed, cancelled, partialAfterTimeout, partialAfterFailure }
       struct Entry: Identifiable, Equatable { let id: UUID; let text: String; let createdAt: Date; let kind: Kind }
       static let capacity = 10
       private(set) var entries: [Entry] = []   // 新的在前
       var onChange: (() -> Void)?
       func record(_ text: String, kind: Kind, now: Date = Date())   // 空白文本忽略；与最新一条文本相同则只更新 kind 与时间
       func clear()
   }
   ```

2. `VoiceTyperController` 新增 `var onResultAvailable: ((String, DictationHistory.Kind) -> Void)?`，在
   `handleFinalText` 的 `.inserted` / `.focusChanged` / `.failed` 三个分支、R8-A1 的失败分支，以及 `.cancelled`
   （**仅当** `partial` 非空，kind = `.cancelled`）调用。空结果、短录音丢弃、手势丢弃不记录。
3. `AppCoordinator` 持有 `DictationHistory` 与**唯一**的 `TextInsertionService`，构造控制器时注入后者
   （`VoiceTyperController(config:asrService:textInsertionService:)`）。菜单点击复制走同一个实例。
4. `StatusBarController`：新增 `onCopyHistoryEntry: ((UUID) -> Void)?`、`onClearHistory`；在 `menuNeedsUpdate(_:)`
   里按 `DictationHistory.entries` 重建子菜单。条目标题只放截断文本，**不设 toolTip**。
5. `RecordingHUDController.showCopiedToClipboard()`：0.9s 一次性提示，不受 `suppressesProgressHUD` 影响。
6. 隐私：不得把条目文本写入任何日志；退出时随进程消失。`README.md` / `README.en.md` 隐私小节补一句
   「最近 10 条结果只保存在内存，退出即清除，可在菜单中清空」。

**测试**：`DictationHistoryTests`（容量淘汰、去重、空白忽略、清空、onChange）；控制器各结局是否按表调用
`onResultAvailable`；`AppCoordinator` 重建控制器后插入服务是同一实例（可用 `ObjectIdentifier` 断言）。

**手工验证**：① 正常听写 → 菜单出现条目；② 听写中切到别的应用 → 条目带「（未输入）」；③ 识别中按 Esc →
条目带「（已取消）」；④ 点击条目后在 TextEdit 粘贴，内容正确；⑤ 清空后菜单项禁用；⑥ 退出重开，列表为空。

---

### R8-B1 热键与 Esc 不穿透

**现状**（S）：`Services/HotkeyService.swift:190` 用 `.listenOnly` 创建 tap，回调总是原样放行事件（`:282` 的注释已写明
Esc 会照常传给前台应用）。后果：
- 组合键热键的主键同时送进前台应用。以 ⌥Space 按住说话为例：先插入一个不换行空格，按住期间按键自动重复还会连续插入。
- 用来取消的 Esc 同时触发前台应用的 Esc 行为：退出全屏、关闭对话框、Vim 切换模式、聊天软件清空输入框等。
- 默认 Fn 热键不受影响；改用组合键的用户每次都受影响。

**目标行为**：有辅助功能权限时（本应用必需权限之一）热键主键与「用于取消的 Esc」**不再到达**前台应用；其他所有按键、
修饰键、鼠标事件一律放行；没有权限或主动 tap 创建失败时退回 `listenOnly`，行为与现在一致。

**实现要点**
1. 把「事件 → 动作 + 是否吞掉」抽成纯值类型 `Services/HotkeyEventFilter.swift`（与 `ModifierTapRecognizer` 同风格，
   只在 worker 线程使用）：

   ```swift
   struct HotkeyEventFilter {
       enum Input { case keyDown(keyCode: Int64, flags: UInt64, isRepeat: Bool), keyUp(keyCode: Int64), flagsChanged(keyCode: Int64, flags: UInt64), mouseDown }
       enum Action { case press, release, cancel, gestureCancelled }
       struct Decision { var actions: [Action]; var consume: Bool }
       mutating func handle(_ input: Input, acceptsCancelWhenInactive: Bool) -> Decision
   }
   ```

   现有 `handle(eventType:event:)` 里的 Fn / 组合键 / 单独修饰键 / Esc 分支逻辑原样迁入，`HotkeyService` 只负责
   把 `CGEvent` 转成 `Input`、把 `Action` 派发到主线程、按 `consume` 决定回调返回 `nil` 还是原事件。
2. **吞键规则**：

   | 事件 | 条件 | 是否吞 |
   | --- | --- | --- |
   | 组合键主键 `keyDown`（含自动重复） | 修饰键匹配且是目标键码；或已处于激活态的同键码重复 | 吞，并记入 `consumedKeyCodes` |
   | 组合键主键 `keyUp` | 键码在 `consumedKeyCodes` 中 | 吞，并移除 |
   | Esc `keyDown` | 本次触发了 `.cancel` | 吞，并记入 `consumedKeyCodes` |
   | Esc `keyUp` | 键码在 `consumedKeyCodes` 中 | 吞，并移除 |
   | 门禁态（`blockedReason` 非空）下按下热键 | 同组合键规则 | 吞（这仍是我们的热键，放行会打出字符） |
   | `flagsChanged`（Fn、单独修饰键、任何修饰键） | — | **永不吞**（吞掉会造成系统修饰键状态错乱） |
   | 鼠标事件、其他按键 | — | 永不吞 |

   **配对不变量**：吞了 `keyDown` 就必须吞对应的 `keyUp`；放行了 `keyDown` 就必须放行 `keyUp`。`stop()` 时清空
   `consumedKeyCodes`。
3. tap 选项：`AXIsProcessTrusted()` 为 true 时用 `.defaultTap`，创建失败则以 `.listenOnly` 重试；日志记一行
   `AppLog.hotkey.info` 说明当前模式（不含按键内容）。辅助功能权限运行中被撤销时，tap 可能被系统禁用，沿用现有
   `reenableTap()` 与就绪重评路径。
4. **性能约束**：主动 tap 的回调延迟会拖慢全系统输入。回调里只允许 O(1) 计算与 `DispatchQueue.main.async`，不得新增锁
   竞争、不得 `sync` 到主线程。
5. 文案：引导「试一试」的排障提示「热键被别的应用抢走了…」改为「热键与系统快捷键冲突（如 Spotlight 的 ⌘Space、
   切换输入法的 ⌃Space）；可以在「设置 → 通用」里换一个热键」。

**测试**：`HotkeyEventFilterTests`：组合键按下 / 重复 / 松开均吞且配对；先松修饰键再松主键仍配对；修饰键不匹配时放行
且 `keyUp` 也放行；Esc 只在录音中 / `acceptsCancelWhenInactive` 时吞；`flagsChanged` 永不吞；单独修饰键识别器行为与
`ModifierTapRecognizerTests` 一致（迁移后既有测试必须全绿）。

**手工验证**（每项记录结果）：
1. 热键设为 ⌥Space（按住模式），在 TextEdit 里按住 3 秒说话 → 不应出现任何空格，只有识别文本。
2. 热键设为 ⌃⌥D，在终端里按住说话 → 终端不应收到字符。
3. Safari 全屏播放视频时按住热键再按 Esc 取消 → 视频不应退出全屏；**不在听写时**按 Esc → 正常退出全屏。
4. 热键设为 ⌃Space，记录系统输入法是否仍会切换（V：系统快捷键可能在 session tap 之前处理，结论写进 DESIGN）。
5. 撤销辅助功能权限 → 日志显示退回 listenOnly，热键仍可用（按门禁提示走）。
6. 连续快速打字 1 分钟（含按住热键期间打字）→ 无可感知的输入延迟。

---

### R8-B2 安全输入与密码框

**现状**（C）：
- 密码框、终端「安全键盘输入」、1Password 等开启安全输入（Secure Event Input）时，事件 tap 收不到 `keyDown/keyUp`，
  **组合键热键静默失效**，用户看到的是「按了没反应」。
- 修饰键的 `flagsChanged` 通常仍能收到（V，需真机确认），于是 Fn / 单独修饰键热键可以在密码框里听写；若开了智能校对，
  口述内容会被发到第三方服务。

**目标行为**
1. 焦点控件是密码框时按下热键：**不开始录音**，浮窗显示锁形图标与「当前是密码输入框，VoiceTyper 不会在这里听写」，
   2s 后消失；即使浮窗设为「不显示」也提示（解释类信息）。
2. toggle 模式下录音中途焦点移到密码框：插入前再查一次，若是密码框则不插入，改为复制到剪贴板，提示
   「目标是密码输入框，结果已复制到剪贴板」，并进入「最近结果」（kind = `.copiedFocusChanged`）。
3. 系统安全输入开启、且当前热键是组合键时：菜单 header 第三行改为「「%@」开启了安全输入，组合键热键暂时收不到」
   （取不到应用名时用「某个应用」）；「设置 → 通用 → 热键」下方显示同一句并附「Fn 与单独修饰键热键不受影响」；
   引导「试一试」排障列表增加这一条。

**实现要点**
1. 新建 `Support/SecureInput.swift`：
   - `static func isEnabled() -> Bool`：`IsSecureEventInputEnabled()`（Carbon）。
   - `static func ownerPID() -> pid_t?`：尽力而为，读 IORegistry 根节点的 `IOConsoleUsers`，取当前用户字典的
     `kCGSSessionSecureInputPID`；任何一步失败返回 nil（V，需真机确认字段存在）。应用名用 `NSRunningApplication(processIdentifier:)`。
2. 新建 `Services/FocusInspector.swift`（R8-E3、R8-F3 也会用）：`@MainActor protocol FocusInspecting { func isFocusedElementSecure() -> Bool }`，
   实现读系统级 `kAXFocusedUIElementAttribute` 的 `kAXSubroleAttribute == kAXSecureTextFieldSubrole`。注入
   `VoiceTyperController`（测试用假实现）。
3. `VoiceTyperController.beginRecording`：`active == nil` 之后、开会话之前检查；命中时调用新回调
   `onSecureFieldRefused`，度量 outcome 新增 `secureField = "secure_field"` 并照常上报一行。
4. `handleFinalText` 插入前再检查一次，命中走第 2 条。
5. 菜单 header：`StatusBarController.menuWillOpen` 时向协调器要一次安全输入状态（`AppCoordinator` 现算，不轮询）。
   设置页在 `onAppear` 与「重新检测」时现算。

**测试**：控制器：密码框时不调用 `audioCaptureService.start`、上报 `secure_field`；插入前变成密码框 → 复制不插入；
文案拼装的纯函数（有 / 无应用名、Fn 热键时不显示）。

**手工验证**：Safari 登录页密码框按住 Fn → 拒绝提示；终端开启「安全键盘输入」后：组合键热键 → 菜单 header 有说明；
Fn 热键 → 记录是否仍能触发（V）；关闭后提示消失。

---

### R8-C1 校对回落不再被「已输入」盖掉

**现状**（S）：校对失败时 `LocalASRSession` 先 `onWarning("智能校对未成功，已使用识别原文")` 再立即 `onFinal`，
紧接着「已输入」的成功提示把警告盖掉（`ASR/LocalASRSession.swift:311` 的注释承认了这点）。用户永远不知道这次没校对，
API Key 过期了也察觉不到。

**目标行为**：校对回落时，成功提示变为「已输入 · 未校对」，下一行写原因，显示 2.5s：

| 原因 | 中文 | English |
| --- | --- | --- |
| 超时 | 智能校对超时，已输入识别原文。 | Smart correction timed out; the raw transcript was inserted. |
| 网络 | 无法连接智能校对服务，已输入识别原文。 | Could not reach the correction service; the raw transcript was inserted. |
| 鉴权（401/403） | 智能校对鉴权失败，请到「设置 → 识别」检查 API Key。已输入识别原文。 | Correction service rejected the API key. Check Settings → Recognition. The raw transcript was inserted. |
| 限流（429） | 智能校对请求过于频繁，已输入识别原文。 | Correction service is rate-limiting requests; the raw transcript was inserted. |
| 服务端（5xx） / 其他 | 智能校对服务出错，已输入识别原文。 | Correction service returned an error; the raw transcript was inserted. |
| 截断 / 空结果 | 智能校对没有返回有效结果，已输入识别原文。 | Correction returned no usable result; the raw transcript was inserted. |

浮窗为「不显示」时：鉴权失败仍提示（配置类问题，每次都会失败）；其余原因不提示。

**实现要点**
1. `LLMCorrector.CorrectionReport` 新增 `fallbackReason: FallbackReason?`
   （`enum FallbackReason: String { case timeout, network, auth, rateLimited = "rate_limited", server, emptyResult = "empty" }`），
   由 `correctWithReport` 把 `URLError.timedOut` / 其他 `URLError` / `LLMError.httpStatus(code)` / 语义回落映射过来。
2. `ASRSessionTimings` 新增 `llmFallbackReason`；删除 `completeWithASRText` 里的 `onWarning(...)` 那一行。
3. `VoiceTyperController` 在 `.inserted` 分支读 `metrics.timings.llmFallbackReason`，经新回调
   `onInsertedWithNote(String?)`（或给 `onStateChange(.idle)` 前置一个 note）交给协调器 → `showSuccess(note:)`。
4. 度量末尾追加 `llm_fail=<reason|->`。

**测试**：`LLMCorrectorTests` 用现有 `URLProtocol` 桩覆盖六种映射；控制器：回落时 note 非空、成功时为 nil。

---

### R8-C2 校对中再按热键跳过校对

**现状**（R）：校对最长要等 `llm.timeout`（默认 5s），期间按热键只得到「上一段听写尚未完成」（`VoiceTyperController.swift:287`），
按 Esc 则整段丢弃。

**目标行为**：浮窗处于「校对中」时状态行为「校对中… · 再按 %@ 跳过」；此时按热键 → 立即上屏识别原文，浮窗按 R8-C1
显示「已输入 · 未校对」，原因「已跳过智能校对」（新增一档 `skipped`，不算失败，不受「不显示」例外影响）。

**实现要点**
1. `LocalASRSession`：保存校对 `Task` 句柄；新增 `var isCorrecting: Bool`、`func skipCorrection() -> Bool`——
   在校对中时取消该 `Task`（`URLSession` 的 async 请求随任务取消而取消），`timings.llmResult = .skipped`（新枚举值），
   以原文调用 `onFinal`。校对 `Task` 返回后检查已跳过则什么都不做。
2. 触发时机（`VoiceTyperController`）：
   - 普通热键（hold / toggle）：`handleHotkeyPress` 中，`active?.phase == .recognizing && session.isCorrecting` 时调用
     `skipCorrection()` 并 return；hold 模式随后到达的松开事件因 `phase != .recording` 自然无效。
   - 单独修饰键：只在**干净单击**（`handleHotkeyRelease` 路径）时跳过，避免 ⌘C 之类的组合快捷键误触发。
   - 识别尚未出结果（不在校对中）时保持原有提示。
3. HUD：`setCorrecting(hotkeyDisplay:)` 拼状态行；流光遮罩宽度随文案同步（`syncShimmerFrame`）。

**测试**：跳过后 `onFinal` 恰好一次且是原文；LLM 迟到结果被忽略；未在校对中时按热键行为不变；单独修饰键按下不跳过、
干净单击才跳过。

---

### R8-C3 120 秒上限倒计时

**现状**（S）：到达上限（`LocalASRSession.maxSessionSamples`，120s）时只闪 1.2s 警告就自动结束，长段口述的用户毫无预期。

**目标行为**：录音进行到剩余 ≤ 15s 时，计时位置改为橙色「还剩 %d 秒」，每 0.5s 刷新；到点后行为不变。

**实现要点**：把上限暴露为 `LocalASRSession.maxSessionSeconds`（`static let`，`maxSessionSamples` 由它推出）；
`RecordingHUDController.startTimer` 的回调里计算剩余秒数（HUD 的 `startDate` 与真实录音起点最多相差 150ms 延迟显示，可接受）；
离开录音阶段恢复常规色。

**测试**：剩余秒数与文案的纯函数（`elapsed` 104.9 → nil、105 → 15、119.6 → 1）。

---

## 5. 第二批（3.5.2）：跟嘴 · 跟手 · 稳

### R8-D1 预览改为事件驱动

**现状**：采集块固定 600ms（`Services/AudioCaptureService.swift:106`，`chunkSamples = 9600`），每块到达才可能跑一次预览，
说话到出字通常要等上不到 1 秒。M4 上 15s 满窗预览约 166ms，算力有余量。

**目标**：说话到预览出字 P50 ≤ 500ms（以度量为准），稳态推理占空比不超过 2/3。

**实现要点**（与 Windows A1 同思路，**只改调度，不碰识别契约**）：
1. `chunkSamples` 默认改为 3200（200ms）。
2. `LocalASRSession.schedulePreview` 的触发条件全部满足才跑：无预览在跑；未进入终稿；自上次预览以来有语音；自上次预览**开始**
   累计 ≥ 4800 个新样本（300ms）；当前时刻 ≥ `previewNotBefore`。`previewNotBefore = 上次预览结束 + 上次耗时 / 2`。
   不引入定时器：下一个采集块最迟 200ms 后到达，会再检查一次。
3. 终稿可能排在一个在飞预览之后（ORT 的 ObjC 绑定没有 `SetTerminate`，见 `DESIGN.md` §11.1），最多等一次预览，接受。
4. 度量末尾追加 `preview_avg`（预览从发起到文字送达主线程的平均耗时）与 `final_wait`（`finalize` 派发到推理真正开始的时间）。
5. 同步更新 `RecognitionBuffer` 顶部注释里「0.6s 为客户端送帧间隔」的稳态 CPU 估算。

**验收**：同一段 20s 口述，改动前后对比 `previews`（应明显增多）、`preview_avg`（应下降）、活动监视器里 CPU（不长期贴满一个核）。
在 M1 等较慢机型上至少测一次。

### R8-D2a 采集「松键时仍在说话」度量

在控制器里保留最近 150ms 的电平样本（带时间戳的小环形缓冲，电平回调已在主线程），松键时若最后 100ms 内最大电平 ≥
`AppConstants.silenceRMSThreshold`，度量末尾追加 `tail_speech=1`，否则 0。**本批只采数据**，补录见 R8-D2b。

### R8-A3a AX 插入回读度量 + 网页控件不做整值替换

**现状**（V）：`TextInsertionService` 在「选中文本」不可写时，用 `kAXValue` 重写整个输入框（`:157`）：破坏撤销、丢富文本；
网页里由前端框架接管的输入框可能只改到辅助功能层，下一次按键文字就消失。AX 返回 success 后不再回退粘贴。

**实现要点**
1. **收窄整值替换**：从焦点元素沿 `kAXParentAttribute` 向上最多 20 层查找 role 为 `AXWebArea` 的祖先；找到则不做
   `kAXValue` 整值替换，直接走粘贴。`kAXSelectedText` 路径不受影响。
2. **回读度量**（本批只记录、不回退）：AX 写入前读一次 `kAXValue`（可读且长度 ≤ 100k 时）与选区；写入成功后等 50ms 再读，
   判定 `verified`（值变化且包含插入文本）/ `unchanged`（值完全没变）/ `unknown`（不可读）。度量末尾追加
   `insert_verify=ok|unchanged|unknown|-`。日志不得包含值内容。
3. 收集一个版本的数据后按 R8-A3b 决定是否回退。

**手工验证矩阵**（每个应用记录 `insert_path` 与 `insert_verify`）：TextEdit、备忘录、Pages、Safari 文本框 / 富文本编辑器、
Chrome 文本框 / contenteditable、VS Code、Slack、微信、飞书、终端、iTerm2、Xcode。

### R8-A5 剪贴板备份提前到识别阶段

**现状**（S）：粘贴兜底时才在主线程同步读取剪贴板全部类型（`:266`），剪贴板里有大图或延迟提供的数据时会卡住主线程。

**实现要点**：`TextInserting` 协议新增 `prepareForInsert()` / `discardPreparedBackup()`；控制器在进入 `.recognizing` 时调用
`prepareForInsert()`（主线程，与 `asrQueue` 上的推理并行，不在「松键 → 出字」的关键路径上）；插入时若
`pasteboard.changeCount` 与快照一致则直接使用，否则重新快照；所有不插入的收尾都调 `discardPreparedBackup()`。
仍在等待中的 `pendingRestore` 继承逻辑优先于提前快照。度量末尾追加 `clip_backup`（毫秒）。若真机数据显示提前快照本身也很慢，
再评估移到后台线程（`NSPasteboard` 跨线程读取须先确认安全）。

### R8-B3 卡键看门狗 + 锁屏 / 睡眠处理

1. **卡键**：按住模式录音期间，每 250ms 查一次热键的物理状态——Fn 用 `CGEventSource.flagsState(.combinedSessionState)`
   的 `.maskSecondaryFn`；组合键检查所需修饰键（**不检查主键**：被吞的主键不一定更新物理状态，见 Windows §15.2 的经验，须真机确认）；
   单独修饰键用设备相关的左右位（复用 `ModifierTapRecognizer` 的位定义）。连续两次判定已松开 → 按松键处理，并调用
   `HotkeyService.resetActiveState()`（新增，在 worker 线程复位 `isActive` / 识别器，避免下一次按下被当成重复）。
2. **锁屏 / 睡眠 / 切换用户**：`AppCoordinator` 订阅 `NSWorkspace.willSleepNotification`、`sessionDidResignActiveNotification`
   与分布式通知 `com.apple.screenIsLocked`：录音阶段静默丢弃（度量 outcome 新增 `interrupted`，不弹「已取消」）；识别阶段不动。
   订阅 `didWakeNotification`、`sessionDidBecomeActiveNotification`、`com.apple.screenIsUnlocked`：控制器在跑时重新安装 tap
   （`suspendHotkeyListening` + `resumeHotkeyListening`），失败走既有就绪重评。

### R8-C4 操作提示

- toggle 模式录音中：状态行在设备名后追加「 · 再按 %@ 结束」（中英文案都要测宽度，`inputDeviceNameLimit` 视情况收紧）。
- hold 模式：前 5 次录音在状态行追加「 · Esc 取消」，计数存 `UserDefaults`（`hint.escCancelShownCount`）。
- 「减少动态效果」下文案不变。

### R8-C5 下载进度详情 + 就绪提示

1. 新建 `Support/DownloadStats.swift`：总字节数取 `ModelDownloader` 已 pin 的文件清单之和；已下载 = 进度 × 总量；
   速度取最近 8s 窗口，数据不足 1s 或无进展时不显示速度。文案：「已下载 %@ / %@ · %d%% · %@/s，约剩 %@」，缺速度时省略后两段。
   用于设置「识别」模型卡片、引导「语音模型」一步、菜单 header（`engineStatusText()`，`App/AppCoordinator.swift:972`）。
2. 下载完成且模型加载就绪时，若引导和设置窗口都不可见：权限已齐 → 浮窗 3s「语音模型已就绪 · 按住 %@ 说话」（toggle 模式用
   「按一下 %@ 开始说话」）；权限未齐 → 不提示（引导 / 设置窗口会接手）。自动重试用尽时浮窗提示一次失败并指向「设置 → 识别」。
   这两条提示不受「不显示」影响。

### R8-G1 菜单栏快捷切换

在「暂停听写」下方新增三个子菜单：「识别语言」（自动 / 中文 / 英文 / 粤语 / 日语 / 韩语）、「麦克风」（自动 / 跟随系统 /
R8-G2 落地后的设备列表）、「智能校对」开关项（未配置 Base URL 时禁用并提示「在设置中配置」）。选择后经
`AppCoordinator.applyConfig` 保存（与设置页同一事务）；听写进行中这些项禁用（`applyConfig` 本就会拒绝）。

### R8-H1 更新后重新授权引导

`UserDefaults` 记录 `lastLaunchedVersion` 与 `permissionsWereAllGranted`。启动时版本号变化、上次权限齐全而这次不齐 →
权限页顶部显示横幅：「刚更新到 %@。本机签名的版本更新后，系统会把它当成新应用，需要重新授权：请在「系统设置 → 隐私与安全性」
里先移除旧的 VoiceTyper 条目，再重新添加。」并给每项权限的「打开系统设置」按钮。权限齐全后横幅消失。签名为 Developer ID 时
（`CodeSigningInfo` 已能判定）不显示。

---

## 6. 第三批：更懂我

### R8-F1 自定义词汇

> **已被 [`VOCABULARY_SPEC.md`](VOCABULARY_SPEC.md) 取代**：新规格增加了 CTC 解码期热词偏置作为主力，并细化了下面两层。
> 本节保留作为历史记录，实施以新规格为准。

**目标**：人名、产品名、术语按用户写法输出。SenseVoice 不支持热词偏置，做两层：

1. **确定性替换**（离线、零延迟，开不开 LLM 都生效）。
2. **LLM 词表提示**（启用智能校对时）。

**配置 schema**（`config.yaml` 新增段，`AppConfig` 解码容错与其余字段一致）：

```yaml
vocabulary:
  - term: "Claude"            # 期望的写法；也作为 LLM 词表
    aliases: ["克劳德"]        # 可能被识别成的写法，可为空
  - term: "VoiceTyper"
    aliases: ["voice typer"]
```

限制：最多 200 条；`term` / 每个 alias ≤ 50 字符；越界在 `validated()` 里截断并记 warning。

**替换规则**：alias 含汉字 → 子串替换；纯 ASCII → 按单词边界、忽略大小写替换；长 alias 优先；替换结果不再参与本轮匹配
（避免链式替换）。

**处理链顺序**（写进 `OutputFormatter.formatForOutput` 的文档注释）：

```
TextPostprocessor（识别契约）
  → 词汇替换
  → LLM 校对（输入附词表）
  → 词汇替换（LLM 可能改回去；幂等）
  → R8-F2 中英文空格
  → （R8-F3 衔接空格）
```

预览同样走「词汇替换 → 中英文空格」。

**LLM 提示**：词表放在最后一条 user 消息里、`<asr_text>` 之前，形如 `<glossary>Claude、VoiceTyper</glossary>`；system prompt 与
few-shot 不变（保住前缀缓存）。`correction.md` 增加一条规则：「若用户消息含 `<glossary>`，其中是用户的专有名词表；听起来像表中
词语的片段按表中写法还原，表外内容照常处理。」**`correction.md` 与 Windows、`client-server/` 共用一份，改动须三方同步**
（见 `DESIGN.md` §5.4）。

**界面**：设置窗口新增第 4 页「词汇」（`SetupTab.vocabulary`）：两列表格（写法 / 可能被识别成，逗号分隔）、增删、
「试一试替换」输入框即时显示结果。保存语义与「通用」页一致（即时生效）。

**测试**：替换规则单测；处理链顺序单测；`LLMCorrector.buildPayload` 带词表时 system 与 few-shot 不变、末条消息含 glossary；
共享夹具 `vocabulary_cases.json`（两平台共用）。

### R8-G2 麦克风设备列表 + 测试麦克风

- 「设置 → 通用 → 麦克风」在「自动 / 跟随系统」下方列出 `AudioInputDevice.allInputDevices()`，选中存 UID（配置已支持，
  `GeneralSettingsView.swift:47` 目前只做展示）；下拉展开时重新枚举；所选设备不存在时显示「（未连接）」，录音时按现有策略回落。
- 「测试麦克风」：新建 `Services/MicLevelTester.swift`，独立 `AVAudioEngine`，只转发电平、不缓存音频；最长 20s；3s 无声提示
  检查静音与设备；开始听写、换设备、关闭窗口时停止。电平条复用 HUD 波形的分贝映射。

### R8-E1 录音紧凑态改小胶囊

现状（S）：紧凑态宽度即 `defaultHudWidth`（850pt，封顶屏宽 50%），只有「录音中 + 波形」时也是横贯半屏的长条。
（已实施，与本条目标有偏离：按用户决定起步 400pt，状态行实测更宽时放宽至多 460pt，见 DESIGN §11.5「浮窗宽度策略与整行滚动」。）
目标：紧凑态宽度按内容计算（约 240–300pt，以状态行与计时的实测宽度为准），出现预览文字后再按现有规则展开；宽度过渡沿用 0.16s 动画。
更新 `HUDLayoutTests`；深浅色、四种位置、减少动态效果下肉眼确认。

### R8-E2 浮窗落在焦点窗口所在屏

现状（S）：`targetScreen()`（`UI/RecordingHUDController.swift:610`）取鼠标所在屏。改为：前台应用 `kAXFocusedWindowAttribute` 的
位置与尺寸 → 所在屏（AX 坐标原点在主屏左上，需换算到 Cocoa 左下原点：`y = 主屏高度 - axY - 高度`）；取不到回落鼠标所在屏。
只在 `showHUD` 时计算一次并缓存到本次听写结束。

### R8-E3 「跟随光标」改为跟随插入点

`FocusInspector.caretRect()`：焦点元素的 `kAXSelectedTextRange` → `kAXBoundsForRangeParameterizedAttribute`（长度 0 取不到时试长度 1）
→ 屏幕坐标；浮窗放在插入点下方 8pt，下方放不下翻到上方；取不到回落鼠标位置。配置值仍是 `near_cursor`（兼容），显示名改为
「跟随输入位置」，`help` 说明「取不到输入位置时跟随鼠标」。

### R8-F3 连续口述的衔接空格

插入前用 AX 读插入点前一个字符（`kAXStringForRangeParameterizedAttribute`，取不到则不处理）：前一个字符是 `[A-Za-z0-9]` 或英文句读
`.,;:!?`、且新文本以 `[A-Za-z]` 开头 → 前补一个空格；前一个字符是汉字且新文本以英文单词开头、或前一个字符是 `[A-Za-z0-9]` 且
新文本以汉字开头 → 前补一个空格（与 R8-F2 一致）。读取在识别阶段完成，不增加松键后的等待。

### R8-A3b AX 回读判定未生效时回退粘贴（数据驱动）

**门槛**：R8-A3a 的数据中出现可复现的 `unchanged` 应用。做法：`unchanged` 时再等 100ms 复查一次仍未变化，才回退粘贴；
`unknown` 维持现状。风险是异步更新的控件被误判后出现**重复插入**，必须按 R8-A3a 的矩阵逐个验证。

### R8-D2b 松键后补录（数据驱动）

**门槛**：R8-D2a 的 `tail_speech=1` 占比 ≥ 20%。做法：hold / toggle 正常松键时，浮窗立即切「识别中」，`audioCaptureService.stop()`
延后 150ms；期间 Esc 仍可取消；设备变化、达到上限不补录。度量 `release_to_finalize` 会相应增加约 150ms，属预期。

### R8-A4 延迟提供剪贴板数据（验证驱动）

粘贴兜底改为 `NSPasteboardItem.setDataProvider(_:forTypes: [.string])`，concealed / transient 标记仍立即写入；目标应用请求数据时
提供文本并记下时刻，150ms 后恢复；兜底超时从 1s 放宽到 3s。**先验证**：Maccy / Paste 等剪贴板工具是否会提前请求数据（若会，
「被读取」信号失真，方案作废）；远程桌面、虚拟机、Electron 应用是否能稳定粘贴。provider 对象必须被强引用到恢复完成。

---

## 7. 第四批：打磨

| ID | 规格要点 |
| --- | --- |
| R8-G3 | 界面语言 Picker 下方提示旁加「立即重启」按钮：保存配置后用 `NSWorkspace.openApplication(at:configuration:)` 拉起新实例再 `NSApp.terminate`；听写进行中禁用 |
| R8-G4 | 智能校对 Base URL 旁加「预设」菜单：OpenAI、DeepSeek、通义千问、豆包（火山方舟）、本地 Ollama；只填 Base URL 与示例模型名，不填 Key；预设地址实施时按各家**当前**官方文档核对并在 DESIGN 注明核对日期。顺带统一保存语义：校对配置改为「测试通过或点保存」二选一，文案说清楚 |
| R8-G5 | 设置新增「诊断」区：最近 20 次 `summaryLine()`（只含数字与枚举）+ 版本、系统版本、签名类型、热键类型、HUD 位置；「复制诊断信息」。不含识别文本、设备名、路径 |
| R8-D3 | 预览与上一次的公共前缀之后的部分以次级色显示约 300ms 再变为常规色；「减少动态效果」下不做 |

---

## 8. 横切要求

### 8.1 新增度量字段（一律追加在 `summaryLine()` 末尾）

| 字段 | 批次 | 取值 |
| --- | --- | --- |
| `finalize_timeout` | 一 | 0 / 1 |
| `llm_fail` | 一 | `timeout` / `network` / `auth` / `rate_limited` / `server` / `empty` / `-` |
| `llm_result` 新值 `skipped` | 一 | 既有字段 |
| outcome 新值 `secure_field`、`interrupted` | 一 / 二 | 既有字段 |
| `preview_avg`、`final_wait` | 二 | 毫秒 |
| `tail_speech` | 二 | 0 / 1 |
| `insert_verify` | 二 | `ok` / `unchanged` / `unknown` / `-` |
| `clip_backup` | 二 | 毫秒 / `-` |

`DictationMetricsTests` 要覆盖新字段的缺省值与顺序；新增字段只能是数字或枚举。

### 8.2 关键新增文案（第一批）

| 中文（键） | English |
| --- | --- |
| 复制上一次结果 | Copy Last Result |
| 最近结果 | Recent Results |
| 清空最近结果 | Clear Recent Results |
| （未输入） | (not inserted) |
| （已取消） | (cancelled) |
| （部分） | (partial) |
| 已复制到剪贴板 | Copied to clipboard |
| 识别超时，已把识别出的部分复制到剪贴板 | Recognition timed out. The recognized part was copied to the clipboard. |
| 可在菜单栏「最近结果」里找回已识别的部分 | You can recover the recognized part from Recent Results in the menu bar. |
| 识别中 · 正在加载模型… | Recognizing · Loading model… |
| 录音中 · 模型加载中… | Recording · Loading model… |
| 当前是密码输入框，VoiceTyper 不会在这里听写 | This is a password field. VoiceTyper won't dictate here. |
| 目标是密码输入框，结果已复制到剪贴板 | The target is a password field. The result was copied to the clipboard. |
| 「%@」开启了安全输入，组合键热键暂时收不到 | “%@” has turned on Secure Input, so key-combination hotkeys can't be received right now. |
| 某个应用 | An app |
| 已输入 · 未校对 | Inserted · Not corrected |
| 已跳过智能校对 | Smart correction skipped |
| 校对中… · 再按 %@ 跳过 | Correcting… · Press %@ to skip |
| 还剩 %d 秒 | %d s left |

（R8-C1 原因文案见该节表格。英文文案可润色，但不得砍成残句。）

### 8.3 工程与文档

- 新增 / 删除 / 移动 Swift 文件或夹具后：`cd macos && ruby scripts/generate_xcodeproj.rb`。
- 每批完成前：`xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper -destination 'platform=macOS' test` 全绿
  （真实模型测试缺模型时跳过，可先跑 `scripts/fetch_model.sh`）。
- 每批交付同步：`CHANGELOG.md`（新版本小节，标注「尚未在真机验证」的部分）、`DESIGN.md` §11.5（实际做法、与本文的差异、验证状态）、
  `README.md` + `README.en.md`（功能与隐私描述、新配置字段）、版本号（`generate_xcodeproj.rb` 两处 `MARKETING_VERSION`、
  `DESIGN.md` §1.3 与 D3）。新增配置字段（R8-F1）时还要同步根 `README.md`（`AGENTS.md` 要求）。
- 提交风格沿用仓库：`feat(macos): R8 第一批——…`，正文列编号；R8-B1 单独一个提交。

### 8.4 Windows 同步清单

| 项 | 要求 |
| --- | --- |
| R8-F2 | **必须**：C# 实现同一规则，读同一份 `text_format_cases.json`（csproj 里按现有夹具方式 `Link`）。两平台规则只能一起改 |
| R8-F1 | **必须**：同一 schema、同一份 `vocabulary_cases.json`；`correction.md` 三方同步 |
| R8-A2 | 可选：Windows 现在只有「复制上一次识别结果」，可扩展为最近 10 条 |
| R8-B2 | 建议：Windows 用 UI Automation 的 `IsPassword` 判断密码框 |
| 其余 | Windows 已有等价实现或不适用，见 §3 表 |

Windows 侧改动遵守 `windows/docs/REVIEW_HISTORY.md` 的遗留与否决项，结论区分「代码审查通过」与「真机验证通过」。

---

## 9. 交付记录模板（每个编号一份，写进 PR 描述或 DESIGN §11.5）

```text
编号：
提交：
改动后的行为：
接口 / 配置 / 度量字段变更：
自动化测试及结果（含跳过原因）：
真机验证（机型、系统版本、步骤与结果；未做写「未验证」）：
与本规格的差异及理由：
Windows 影响：
```

---

## 10. 开放问题（不在本规格范围，需产品决定后另立条目）

- 句末句号策略：短语或聊天场景是否去掉末尾「。」（`correction.md` 已对 LLM 路径做了「词语性短语去句号」，本地路径未做）。
- 「最近结果」是否需要全局快捷键直接粘贴上一次结果。
- 开始 / 结束提示音（默认关闭）、录音时自动降低媒体音量。
- Developer ID 签名与公证（R8-H1 只能缓解，根治需要账号）。

---

## 11. 给实施 Agent 的开工指引

1. **先读**：本文 §1–§3、`AGENTS.md`、`macos/DESIGN.md` §11–§12（尤其已否决项）、`windows/docs/REVIEW_HISTORY.md` §4。
2. **从第一批开始，按 R8-F2 → R8-A2 → R8-A1 → R8-C1 → R8-C2 → R8-C3 → R8-B2 → R8-B1 的顺序做**：前三项是后面的依赖；
   R8-B1 改动的是全局输入链路，放最后、单独提交。
3. **先写测试**：R8-F2 先落 `text_format_cases.json` 和 `OutputFormatterTests`，再写实现；R8-B1 先把现有逻辑原样迁进
   `HotkeyEventFilter` 并让既有测试全绿，再加吞键规则。
4. **不要碰** `TextPostprocessor`、fbank / LFR-CMVN / CTC、`Fixtures/` 里的既有金标准文件；不要改 LLM 的输入文本；
   不要往日志或磁盘写识别文本。
5. **每完成一项**：重跑 `generate_xcodeproj.rb`（如有新文件）→ 全量测试 → 按 §9 模板记录。热键、剪贴板、麦克风、浮窗相关的项写出
   手工验证步骤，**没在真机跑过就写「未验证」**。
6. **规格与实现冲突时**：以「不丢字、不打扰」的目标为准做最小偏离，在 DESIGN §11.5 写明差异与理由；涉及 §2 已定决策的改动先问人。
