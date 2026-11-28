# VoiceTyper · 校对效果诊断记录规格（`ASR_HINTS_SPEC` 附加规格）

[← `<asr_hints>` 主规格](ASR_HINTS_SPEC.md) · [macOS 目录](../README.md) · [架构设计](../DESIGN.md)

| 项 | 内容 |
| --- | --- |
| 隶属 | [`ASR_HINTS_SPEC.md`](ASR_HINTS_SPEC.md) 的附加规格（Addendum） |
| 状态 | **设计已定，随 `feat/asr-hints` 分支探索验证** |
| 审查基线 | `0665464`（`feat/asr-hints`）/ 3.6.1 |
| 编号 | `TRACE-<序号>`（如 `TRACE-1`），已定决策 `TRACE-D<序号>`。代码注释引用编号时只写编号 |
| 读者 | 实施 Agent / 开发者；产品决策者看 §1–§3 与 §6 即可 |

> **证据口径**沿用主规格：**S** = 源码可直接证明；**C** = 结合 API / 契约确认；**V** = 待验证；**E** = 估算。
> 本附加规格涉及显式 opt-in 的本机文本落盘，所有隐私隔离与容错条款均为强制约束。

---

## 1. 背景与目标

### 1.1 为什么需要本功能

VoiceTyper 既有的 `DictationMetrics` 与主日志（macOS `OSLog`、Windows `app.log`）严守“零用户文本”红线（**S**），只能看到耗时与计数，无法看到“ASR 识别成了什么、提取了哪些 `<asr_hints>`、大模型最终改成了什么”。

在探索和打磨 `<asr_hints>` 与校对 Prompt 期间，开发者与早期试用用户需要一种**默认关闭、显式开启、仅存本机、随时可清空**的观测手段，用于：

1. **真实场景效果观察**：直接对照每次听写的 `raw_text` → `replaced_text` → `asr_hints` → `llm.output_text` → `final_text`，判断 `<asr_hints>` 是帮了忙还是造成了误改；
2. **数据驱动调参**：基于真实录音积累的 `best_margin` 分布，精准校准阈值 $\tau$、过滤规则与上限；
3. **Prompt 离线回归重放**：直接读取积累下来的 `.jsonl` 真实样本，在修改 `correction.md` 或 `fewShotMessages` 后批量重放对比，**无需重新口述录音**即可验证 Prompt 改动是否引入回归。

### 1.2 核心原则（隐私与架构红线）

1. **严格默认关闭（Opt-in）**：配置项 `llm.trace_log` 默认为 `false`；用户未主动勾选前，绝不创建日志文件、绝不落盘任何听写文本。
2. **与主日志彻底物理隔离**：诊断记录写入独立的本机 JSONL 文件（`correction_trace.jsonl`），**绝不写入** macOS `OSLog`、Windows `app.log` 或 `DictationMetrics`。用户分享主日志排障时不会意外泄露听写内容，`DictationMetrics` 无 `String` 存储属性的反射测试继续保持不变。
3. **不留存凭证与高危上下文**：采用**严格白名单**记录字段，绝不写入 API Key、`base_url`、词汇表原文、音频数据、剪贴板内容或前台窗口标题；密码输入框本就被 R8-B2 拦截，不会产生记录。
4. **有界存储与一键销毁**：文件设置单文件上限与滚动备份硬顶（总上限 15 MB），并提供设置页「打开日志位置」与「清空日志」按钮，用户可随时查看并彻底删除全部记录。
5. **零主流程影响**：后台异步写入；任何磁盘/权限故障只向主日志记脱敏错误分类，绝不阻断或延迟文本上屏。

---

## 2. 已定决策

| # | 决策 | 结论 |
| --- | --- | --- |
| TRACE-D1 | 日志载体 | **独立本机 JSONL 文件**（`correction_trace.jsonl`），不混入 macOS `OSLog` 或 Windows `app.log` |
| TRACE-D2 | 配置字段 | 新增 `llm.trace_log: false`（默认关闭）与非必填 `llm.trace_log_path: ""`（留空用默认路径）；仅在 `llm.enabled == true` 且实际进入校对阶段时记录。旧配置缺失自动回落默认，无需迁移 |
| TRACE-D3 | 存储位置与权限 | 默认写到系统临时目录：macOS `/tmp/voice_typer/correction_trace.jsonl`（目录 `0700`、文件 `0600`，校验非符号链接与属主）；Windows `%TEMP%\VoiceTyper\correction_trace.jsonl`（本机非漫游临时目录）。用户可通过 `llm.trace_log_path` 自定义持久路径 |
| TRACE-D4 | 滚动上限 | 单文件上限 **5 MB**，保留最多 **2** 个备份（`.1` / `.2`），总容量硬顶 **15 MB**（约 1–2 万条记录） |
| TRACE-D5 | 记录覆盖范围 | 记录每次进入 LLM 校对阶段的真实听写（含 `corrected`、`fell_back`、`skipped`）；**不记录**预览、空识别、短于 300ms 丢弃、Esc 取消、未开校对、以及设置页「测试校对」 |
| TRACE-D6 | 文本完整链 | 同条记录同时保留 `asr.raw_text`、`asr.replaced_text`、`llm.output_text`、`llm.final_text`，明确区分「ASR」「词汇替换」「LLM 校对」「后置格式化」四段贡献 |
| TRACE-D7 | Token 用量采集 | 从 OpenAI 兼容响应的 `usage` 对象顺带提取 `prompt_tokens`、`completion_tokens` 与可选 `cached_tokens`（服务端未返回时记 `null`，不报错） |
| TRACE-D8 | 入口与清理 | 设置页「识别 → 智能校对」内提供开关，勾选后展开非必填「日志路径」输入、明确本机隐私提示与「在访达中显示 / 打开日志目录」「清空日志（二次确认）」；开启期间给出可见提示防止长期忘关 |

---

## 3. 配置与设置页交互（`TRACE-1`）

### 3.1 配置 Schema（`config.yaml`）

在 `llm` 段末尾新增两个字段（两平台同字段名、同默认值）：

```yaml
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0.0
  max_tokens: 800
  timeout: 5.0
  trace_log: false
  trace_log_path: ""   # 非必填；留空使用默认临时路径（macOS: /tmp/voice_typer/correction_trace.jsonl）
```

- **macOS**（`Core/AppConfig.swift`、`Core/ConfigStore.swift`）：
  - `LLMConfig` 新增 `var traceLog: Bool = false` 与 `var traceLogPath: String = ""`，`CodingKeys` 映射 `trace_log` / `trace_log_path`，`init(from:)` 用 `decodeIfPresent` 缺省回落。
  - `ConfigStore.serializedYAML` 在 `llm:` 段末尾写出 `  trace_log: true/false` 与 `  trace_log_path: "..."`。
- **Windows**（`Core/AppConfig.cs`）：
  - `LlmConfig` 新增 `[YamlMember(Alias = "trace_log")] public bool TraceLog { get; set; } = false;` 与 `[YamlMember(Alias = "trace_log_path")] public string TraceLogPath { get; set; } = "";`，并在 `Clone()` 中复制。
  - `ConfigParityTests` 补充双向往返与缺省字段验证。
- **路径校验与规范化（`AppConfig.validated()` / 路径解析器）**：
  1. 去首尾空白；为空串时保留 `""`（运行期解析为默认路径）；
  2. 含 NUL 或换行等控制字符时回落为 `""` 并记 warning（不把原串写入日志）；
  3. 运行期解析规则：
     - macOS 支持 `~` 展开（`NSString.expandingTildeInPath`），Windows 支持环境变量展开（`Environment.ExpandEnvironmentVariables`）；
     - 展开后必须为**绝对路径**，相对路径回落默认路径并记 warning；
     - 若目标已是一个目录、或原串以 `/` / `\` 结尾，则自动拼接文件名 `correction_trace.jsonl`；否则直接作为 JSONL 文件路径使用。

### 3.2 设置页 UI

置于现有「设置 → 识别 → 智能校对」展开区（`if vm.llmEnabled` 内，紧跟「超时（秒）」之后、按钮行之前）；仅在勾选「记录校对日志」时展开路径与操作行，未勾选时只占一行，避免界面臃肿：

```text
┌ 智能校对 ──────────────────────────────────────────────────────────┐
│ [✓] 启用智能校对                                                    │
│   Base URL      [ https://...                                  ]  │
│   API Key       [ ••••••••••••                                 ]  │
│   模型           [ gpt-4o-mini                                  ]  │
│   温度           [━━━●━━━━━━━━] 0.0                                │
│   超时（秒）                                               [ 5 ]   │
│   [✓] 记录校对日志（用于效果评估）                                   │
│       日志路径    [ 留空默认 /tmp/voice_typer/correction_trace.jsonl ] │
│       开启后会将识别原文、声学备选与校对结果保存在本机，不会上传。      │
│       默认写到 /tmp/，重启后自动清空；如需长期保留可自定义路径。        │
│                                    [ 在访达中显示 ]  [ 清空日志 ]    │
│                                                                   │
│                                      [ 测试校对 ] [ 保存并应用 ]    │
└───────────────────────────────────────────────────────────────────┘
```

交互细则：

1. **保存语义**：`trace_log` 与 `trace_log_path` 均跟随「智能校对」区块的「保存并应用」提交（与 `llm.*` 其余字段一致，纳入草稿脏检查与 `syncAppliedConfig` 同步）。
2. **「在访达中显示」/「打开日志目录」**：针对当前已解析的目标路径——
   - macOS：若目标文件存在则 `NSWorkspace.shared.activateFileViewerSelecting([traceURL])`；若尚未生成则创建其父目录并打开目录。
   - Windows：若文件存在则 `explorer.exe /select,"<path>"`，否则创建并打开其父目录。
3. **「清空日志」**：
   - 仅清空当前解析路径对应的主文件与 `.1`/`.2` 滚动备份（若之前换过路径，不自动删旧路径的文件）；
   - 弹出确认对话框：「确定删除本机保存的全部校对日志？此操作不可撤销。」确认后调用 `CorrectionTraceLogger.clear()`。
4. **窗口高度检查**：macOS `SetupWindowController.contentHeight`（当前 `540pt`，**S**）在未勾选 `trace_log` 时基本不变；勾选展开路径与说明后需确认滚动或略微调高窗高，确保控件不被裁切；Windows `SetupForm` 同步检查 DPI 缩放适配。
5. **防遗忘提示**：当 `llm.enabled && llm.trace_log` 均已生效时，在菜单栏头部状态副文案或「智能校对」状态项旁补充克制提示（例如「智能校对（记录中）」），提醒用户当前正落盘本机评估日志。
6. **本地化**：所有新增中文文案须同步补齐 `macos/Sources/VoiceTyper/Support/Localization/Strings+en.swift` 与 `windows/Support/Localization/Strings.en.cs`（保持中英文之间半角空格规范，通过 `LocalizationTests`）。

---

## 4. 文件存储、格式与滚动策略（`TRACE-2`）

### 4.1 路径与权限

| 平台 | 默认路径（`trace_log_path` 留空时） | 权限 / 安全约束 |
| --- | --- | --- |
| **macOS** | `/tmp/voice_typer/correction_trace.jsonl` | 子目录 `0700`、主文件及备份 `0600`（仅当前用户可读写）；因 `/tmp` 为全局可写目录，打开前必须用 `lstat` / `O_NOFOLLOW` 校验子目录与目标文件**非符号链接且属主为当前 `getuid()`**，不满足则拒绝写入并记 warning。系统重启会自动清理 `/tmp/`，自带隐私兜底；如需跨重启长期积累，可通过 `llm.trace_log_path` 指定用户目录路径 |
| **Windows** | `%TEMP%\VoiceTyper\correction_trace.jsonl` | 由 `Path.Combine(Path.GetTempPath(), "VoiceTyper", "correction_trace.jsonl")` 解析；属于本机非漫游临时目录，沿用当前用户私有 ACL，不随域账户漫游 |

> 默认目录与文件名常量分别集中定义在 `macos/Sources/VoiceTyper/Support/Constants.swift` 与 `windows/Support/Constants.cs`；写入器支持路径解析函数（输入 `traceLogPath` 输出最终 `URL`/`string`）及测试临时目录覆盖。

### 4.2 JSONL 格式与滚动机制

- **编码**：UTF-8（无 BOM），每条记录编码为单行紧凑 JSON（不 pretty-print，保证 `wc -l` / `jq` / `pandas.read_json(..., lines=True)` / Python 逐行解析零障碍），末尾追加 `\n`。
- **滚动规则**：
  - 单文件阈值 `maxBytes = 5 * 1024 * 1024`（5 MB），最大备份数 `backupCount = 2`；
  - 写入后文件大小 $\ge \text{maxBytes}$ 时滚动：删 `.2` → `.1` 重命名为 `.2` → 主文件重命名为 `.1` → 后续写入新建主文件（重新设置 `0600` 权限）；
  - 启动时若已有主文件且 $\ge \text{maxBytes}$ 同样先滚动。
- **异步与串行**：
  - macOS 使用独立 `actor CorrectionTraceLogger`（或低优先级 `.utility` 串行 `DispatchQueue`）；
  - Windows 使用独立锁保护的异步/后台队列（或轻量单锁追加，写入 < 1ms，与 `AppLog` 类似但独立锁、互不阻塞）；
  - `clear()` 必须在同一串行队列/锁内执行，关闭/刷新句柄后再删除主文件与 `.1`/`.2`，避免与在途写入竞态。
- **失败隔离**：
  - 磁盘满、IO 错误、编码失败一律捕获并通过主日志 `AppLog.llm.warning("写入校对诊断日志失败: \(errorType)")` 记脱敏错误类型，**绝不打印记录内容**，绝不向用户抛错或阻断上屏；
  - 写入失败后冷却 60 秒再重试打开（沿用 Windows `AppLog.ReopenDelay` 策略），避免磁盘满时每次听写刷屏报错。

---

## 5. 日志条目 Schema（`TRACE-3`）

每条记录对应一次进入 LLM 校对阶段的听写会话，遵循以下 **Schema v1**（JSON 字段统一使用 `snake_case`，跨平台逐字段一致）：

```json
{
  "schema_version": 1,
  "ts": "2026-11-28T20:15:32.418+08:00",
  "session_id": "a3f1",
  "app_version": "3.6.1",
  "platform": "macos",
  "audio_sec": 6.4,
  "asr": {
    "language_setting": "auto",
    "decode_mode": "beam",
    "vocab_hits": 1,
    "asr_ms": 112,
    "decode_ms": 0.9,
    "raw_text": "这个接口需要保证密等性，用克劳德来检查一下",
    "replaced_text": "这个接口需要保证密等性，用Claude来检查一下"
  },
  "hints": {
    "margin_threshold": 2.5,
    "raw_count": 2,
    "active_count": 1,
    "dropped_by_vocab": 1,
    "items": [
      {
        "surface": "密等",
        "alternatives": ["幂等", "密度"],
        "best_margin": 0.62
      }
    ]
  },
  "llm": {
    "model": "gpt-4o-mini",
    "temperature": 0.0,
    "glossary_count": 12,
    "hints_sent": true,
    "result": "corrected",
    "fallback_reason": null,
    "thinking_param": "sent",
    "llm_ms": 640,
    "prompt_tokens": 512,
    "completion_tokens": 24,
    "cached_tokens": 448,
    "output_text": "这个接口需要保证幂等性，用 Claude 来检查一下。",
    "final_text": "这个接口需要保证幂等性，用 Claude 来检查一下。"
  }
}
```

### 5.1 字段说明

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schema_version` | `int` | 固定 `1`；后续增删字段时用于分析脚本向后兼容 |
| `ts` | `string` | 本地时区 ISO-8601 时间戳（精确到毫秒），便于按时间筛选 |
| `session_id` | `string` | 4 位十六进制会话短 ID，与 `DictationMetrics` 的 `session=` 严格一致，便于关联系统指标 |
| `app_version` | `string` | 当前应用版本号（如 `"3.6.1"`） |
| `platform` | `string` | `"macos"` 或 `"windows"` |
| `audio_sec` | `number` | 输入语音有效时长（秒，保留 1 位小数，`receivedSamples / 16000.0`） |
| `asr.language_setting` | `string` | 用户配置的识别语言（`auto / zh / en / yue / ja / ko`） |
| `asr.decode_mode` | `string` | `"greedy"` 或 `"beam"` |
| `asr.vocab_hits` | `int` | 终稿热词偏置命中次数 |
| `asr.asr_ms` | `int` | `finalize` 阶段本地 ASR 推理总耗时（毫秒） |
| `asr.decode_ms` | `number` | CTC 解码 + hint 提取耗时（毫秒，保留 1 位小数） |
| `asr.raw_text` | `string` | SenseVoice 解码经 `TextPostprocessor` 后的原始文本 |
| `asr.replaced_text` | `string` | 经 `VocabularyReplacer.apply` 后、实际填入 `<asr_text>` 发给 LLM 的文本 |
| `hints.margin_threshold` | `number` | 本次提取使用的 logit 分差门限 $\tau$（便于跨版本调参对照） |
| `hints.raw_count` | `int` | `AcousticHintExtractor` 原始产出的提示条数 |
| `hints.active_count` | `int` | 经词汇表一致性过滤后、实际发给 LLM 的提示条数 |
| `hints.dropped_by_vocab` | `int` | 因已被替换层修正或命中精确 `term` 而被过滤掉的条数 |
| `hints.items` | `array` | 实际发给 LLM 的每条提示：`surface`、`alternatives`、`best_margin`（保留 2 位小数）；无提示时为 `[]` |
| `llm.model` | `string` | 配置的 `llm.model` 名称 |
| `llm.temperature` | `number` | 配置的 `llm.temperature` |
| `llm.glossary_count` | `int` | 随本次请求发送的 `<glossary>` 词条数（**只记数量，不记词表原文**） |
| `llm.hints_sent` | `bool` | 本次请求是否附带了 `<asr_hints>` 标签（等价于 `hints.active_count > 0`） |
| `llm.result` | `string` | `"corrected"` \| `"fell_back"` \| `"skipped"` |
| `llm.fallback_reason` | `string?` | 仅 `fell_back` 时有值（`timeout / network / auth / rate_limited / server / empty`），其余 `null` |
| `llm.thinking_param` | `string?` | `"sent"` \| `"omitted_by_cache"` \| `"rejected_then_omitted"`；`skipped` 时为 `null` |
| `llm.llm_ms` | `int` | 校对请求耗时（毫秒；`skipped` 时为等待至跳过前的时长） |
| `llm.prompt_tokens` | `int?` | 响应 JSON `usage.prompt_tokens`；服务端未返回或未完成时为 `null` |
| `llm.completion_tokens` | `int?` | 响应 JSON `usage.completion_tokens`；服务端未返回或未完成时为 `null` |
| `llm.cached_tokens` | `int?` | 响应 JSON `usage.prompt_tokens_details.cached_tokens`（验证前缀缓存是否命中）；未返回为 `null` |
| `llm.output_text` | `string?` | LLM 校对返回的文本（已剥标签）；`fell_back` 或 `skipped` 时为 `null` |
| `llm.final_text` | `string` | 经 `OutputFormatter.formatForOutput` 处理后、实际用于上屏的最终文本 |

### 5.2 显式排除项（白名单红线）

以下信息**严禁**出现在 `correction_trace.jsonl` 中：
- `api_key`、`Authorization` 头、`base_url`（可能含私有内网域名或 URL query 密钥）；
- 完整 `<glossary>` 词条内容；
- 原始音频样本或 `ctc_logits` 数组；
- 前台应用名称、窗口标题、剪贴板原文。

---

## 6. 代码落点与调用链路（`TRACE-4`）

### 6.1 Token 用量解析扩展（`LLMCorrector`）

在 `LLMCorrector.swift` / `LlmCorrector.cs` 新增轻量值类型：

```swift
struct TokenUsage: Equatable, Sendable {
    let promptTokens: Int?
    let completionTokens: Int?
    let cachedTokens: Int?
}
```

- `CorrectionReport` 新增 `var tokenUsage: TokenUsage? = nil`；
- `parseOutcome` 在解析顶层 `json["usage"]` 时容错提取 `prompt_tokens`、`completion_tokens` 及 `prompt_tokens_details.cached_tokens`（任一字段缺失或类型不符时置 `nil`，**绝不影响正常校对解析**）。

### 6.2 采集与落盘时机（`LocalASRSession` → `VoiceTyperController`）

1. `LocalASRSession` 在 `completeWithASRText(_ asrText: String, hints rawHints: [AcousticHint])` 内保留本次校对会话的轻量内存快照（`CorrectionTraceDraft`：含 `asrRawText`、`asrReplacedText`、`rawHintsCount`、`droppedByVocab`、`activeHints`、`llmOutputText`、`tokenUsage`、`thinkingParam`）；**仅当 `traceEnabled == true` 时才构造该快照**，关闭时零额外内存拷贝。
2. 校对收尾（`.corrected` / `.fellBack` / `.skipped`）经 `onFinal` 回到 `VoiceTyperController` 完成 `OutputFormatter.formatForOutput` 得到 `finalText` 后，由控制器统一拼装完整 `CorrectionTraceRecord`（补上 `session_id`、`audio_sec`、阶段毫秒耗时、`language_setting`、`model`、`temperature`、`final_text`）并异步投递给 `CorrectionTraceLogger.append(record)`。
3. `AppCoordinator` 持有单一 `CorrectionTraceLogger` 实例并注入控制器；当用户在设置页修改 `llm.trace_log_path` 并保存时，更新该实例的目标路径（不自动删除旧路径下的历史文件），「清空日志」操作作用于当前生效路径。

### 6.3 与主规格 `ASR_HINTS_SPEC.md` 的联动

- 本附加规格**不阻塞 Step 0**，但**建议与阶段 1（macOS 核心）同步落地**：一旦进入真机试用，开发者打开 `llm.trace_log` 即可在日常真实听写中无感收集第一手语料，持续迭代 $\tau$ 与 `correction.md`。
- 即使在 `<asr_hints>` 尚未通过门禁或实验性关闭提取时，该日志器也能独立记录 `hints.items = []` 的纯 1-best 校对基线，同样支持离线 Prompt 对比。

---

## 7. 配套分析与 Prompt 迭代闭环（`TRACE-5`）

在 `macos/scripts/eval_asr_hints.py` 中增加 `--from-trace <path/to/correction_trace.jsonl>` 辅助子命令（只读、零副作用），支持一键生成两类分析视图：

1. **线上实测统计报告（`--mode trace-summary`）**：
   - 总记录数、平均语音时长、各 `llm.result` 占比、平均 `asr_ms` / `decode_ms` / `llm_ms`；
   - `hints_sent == true` 占比、平均每句 `active_count`、`dropped_by_vocab` 总数；
   - 按 `best_margin` 分桶统计（`[0, 1.0)`、`[1.0, 1.5)`、`[1.5, 2.0)`、`[2.0, 2.5]`）以及各桶内「LLM 确实采纳了备选词」的比例；
   - 对比 `hints_sent == true` 与 `false` 时的平均 `prompt_tokens`、`cached_tokens` 命中率与 `llm_ms`；
   - 导出所有 `replaced_text != output_text` 且 `hints_sent == true` 的对照列表，便于人工快速标注“修对 / 误改 / 无关”。
2. **离线 Prompt 回归重放（`--mode replay`）**：
   - 直接读取历史 `correction_trace.jsonl` 中的 `asr.replaced_text` 与 `hints.items`；
   - 用待测的新版 `correction.md` / 新 few-shot / 新模型重新发起校对，自动输出与历史 `llm.output_text` 存在差异的逐条 diff，几分钟即可验证一次 Prompt 调整的真实影响。

---

## 8. 测试与手工验证（`TRACE-6`）

### 8.1 单元测试（macOS + Windows 同步）

| 测试文件 | 覆盖点 |
| --- | --- |
| `AppConfigTests` / `ConfigParityTests` | `llm.trace_log` 默认 `false`、`llm.trace_log_path` 默认 `""`；YAML 读写往返；旧配置缺少两字段时回落默认；含控制字符或相对路径时按 §3.1 回落默认路径 |
| `CorrectionTraceLoggerTests` | 单行合法 JSONL 写出与 Schema v1 必需字段；默认路径解析与自定义目录/文件路径解析（含 `~` 展开）；文件权限 `0600` 及符号链接拒绝（macOS）；超过 `maxBytes` 触发 `.1`/`.2` 滚动且最多保留 2 个备份；切换路径后写入新文件且不影响旧文件；`clear()` 删除主文件及全部备份；写入失败（只读/非法路径）不抛错、不崩溃 |
| `LLMCorrectorTests` | 响应含完整 `usage`（含 `cached_tokens`）时正确解析 `tokenUsage`；响应不含 `usage` 或格式异常时 `tokenUsage` 为 `nil` 且校对结果不受影响 |
| `LocalASRSessionTests` / `VoiceTyperControllerTests` | `trace_log == false` 时不调用写入器；`trace_log == true` 时分别验证 `corrected`、`fell_back`、`skipped` 各写 1 条且字段齐全；未开校对、空识别、Esc 取消、设置页「测试校对」均**不写入** |
| `DictationMetricsTests` | 既有无 `String` 存储属性反射测试继续通过（证明主日志未被污染） |

### 8.2 手工验证步骤

1. **默认关闭确认**：升级后不改动任何配置，开启智能校对听写 3 次，确认 `/tmp/voice_typer/correction_trace.jsonl`（Windows：`%TEMP%\VoiceTyper\correction_trace.jsonl`）**不存在**。
2. **默认路径与字段核查**：在「设置 → 识别 → 智能校对」勾选「记录校对日志」（路径留空）并「保存并应用」，分别听写：① 清晰日常句；② 含近音模糊词的句子；③ 含词表 alias 的句子；④ 断网触发回落；⑤ 校对中再按热键跳过。点「在访达中显示 / 打开日志目录」，核对默认路径下的 `.jsonl` 共 5 行，每行均为合法 JSON，且 `raw_text` / `replaced_text` / `hints.items` / `output_text` / `final_text` / `audio_sec` 准确、不含 API Key 或 Base URL。
3. **权限核对（macOS）**：执行 `ls -ld /tmp/voice_typer && ls -la /tmp/voice_typer/correction_trace.jsonl`，确认目录为 `drwx------`（`0700`）、文件为 `-rw-------`（`0600`）。
4. **自定义路径核查**：将「日志路径」改为自定义路径（如 `~/Desktop/vt_trace.jsonl` 或一个已有目录）并保存，听写 1 次确认新路径成功生成记录，点「在访达中显示 / 打开日志目录」能正确定位到新文件。
5. **一键清空与关闭**：点击「清空日志」确认后当前路径文件消失；取消勾选并保存，再听写 2 次确认文件不再重新生成。
6. **主日志隔离核对**：检查 macOS `log show` / Windows `app.log`，确认主日志中仍无任何听写文本。
