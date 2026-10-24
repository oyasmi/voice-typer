# VoiceTyper macOS · 自定义词汇（热词）实施规格

[← macOS 目录](../README.md) · [架构设计](../DESIGN.md) · [变更记录](../CHANGELOG.md) · [R8 规格](UX_R8_SPEC.md)

| 项 | 内容 |
| --- | --- |
| 状态 | **已实施，随 3.6.0 发布；参数待校准**。2026-10-22 四个阶段的代码完成，2026-10-23 在 macOS 上编译、全量测试通过并经真机听写，Windows 设置页补齐。实施结果、与初稿的差异和验证状态见 `DESIGN.md` §11.7；本文已按实施结果修订，修订处标注「（实施修订）」 |
| 审查基线 | `b86aa28` |
| 取代 | `UX_R8_SPEC.md` R8-F1（自定义词汇）。R8-F1 的两层（确定性替换、LLM 词表）在本文中保留并细化，另增识别层偏置 |
| 编号 | `VOC-<序号>`，例如 `VOC-3`。代码注释引用编号时只写编号 |
| 读者 | 实施 Agent / 开发者；产品决策者看 §1–§3 即可 |

> **证据口径**沿用 `UX_R8_SPEC.md`：**S** = 源码或模型文件可直接证明；**C** = 结合 API / 算法契约确认；
> **V** = 待验证；**E** = 估算。本文**未做任何效果验证**：偏置参数（§5.6）是按算法推导给出的初值，
> 必须在实施后用 §10.5 的评估工具和真实录音校准。交付时不得把估算写成实测。

---

## 1. 背景与目标

### 1.1 问题

不常用的词、用户特有的高频词（人名、产品名、项目代号、行业术语）识别不出来，典型表现：

| 类型 | 例子 | 根因 |
| --- | --- | --- |
| 同音 / 近音别字 | 张三丰 → 张三峰、飞书 → 非书 | 正确字在声学上有分，但不是该帧第一名 |
| 英文词被拆分、改大小写 | VoiceTyper → voice typer | 模型没见过该词，只能按读音拼出常见片段 |
| 跨文字 | Claude → 克劳德 | 用户按中文习惯读，模型输出汉字 |
| 数字与汉字 | 三体 → 3体 | 模型自带 ITN |

SenseVoice-Small 没有热词输入（不像 SeACo-Paraformer）。

### 1.2 目标

1. **离线、零网络依赖地**修正上述错误，不依赖 LLM（LLM 校对默认关闭）。
2. 用户只需写出**正确写法**；可选地写一个「听起来像」，不必穷举所有错法。
3. **没听到的不硬塞**：热词只在声学上说得通时才生效，不误改正常文本。
4. 录音中的预览与最终结果一致地生效。
5. 未配置词汇的用户行为**逐字节不变**。

### 1.3 非目标

- 不换模型、不重新导出 ONNX、不引入外部语言模型。
- 不从用户在前台应用里的事后修改中自动学习（需要 AX 持续读取，隐私与复杂度都不划算）。
- 本轮不做 Windows（§11 阶段 4 移植），但配置 schema 与共享夹具从第一天起按两平台设计。

---

## 2. 已定决策

| # | 决策 | 结论 |
| --- | --- | --- |
| VOC-D1 | 主力机制 | **CTC 解码期热词偏置**（前缀束搜索 + 上下文图）。替换表和 LLM 词表是辅助层，不是主力 |
| VOC-D2 | 用户数据模型 | 一张词汇表，每条 = `term`（正确写法）+ 可选 `aliases`（听起来像 / 常被识别成）。三层共用 |
| VOC-D3 | aliases 的语义 | aliases **既参与识别偏置，也参与确定性替换**：先把识别结果稳定在 alias 上，再替换成 term。这是跨文字（克劳德→Claude）能稳定生效的关键 |
| VOC-D4 | 识别契约 | 词汇表为空、或偏置强度为「关」时，走现有贪心解码，**金标准夹具与现有输出逐字节不变**。有词汇时整句改走束搜索，这是本文对 R8「识别契约不动」不变量的唯一、显式的放宽 |
| VOC-D5 | 强度 | 全局 4 档：关 / 轻 / 标准 / 强，默认「标准」。不提供逐条强度 |
| VOC-D6 | 短词 | 只有 1 个 token 的写法（单个汉字、单个 BPE 片段的英文词）**不参与偏置**，只参与替换与 LLM 词表。判定看写法的原样合写变体：它本身是 1 个 token，整个写法就不参与（实施修订，§5.2） |
| VOC-D7 | 拼音模糊匹配 | **不做**（§12） |
| VOC-D8 | 容量 | 最多 200 条；每条 aliases ≤ 10 个；term / alias ≤ 50 字符 |
| VOC-D9 | 隐私 | 词汇只存在 `config.yaml`；日志与度量只记条数、命中次数与耗时，**不记任何词条文本** |

---

## 3. 总体设计

```
                      ┌──────────────── 词汇表（config.yaml: vocabulary）────────────────┐
                      │  term + aliases                                                 │
                      └───────┬───────────────────────┬───────────────────────┬─────────┘
                              │ 编译为 token 序列      │ 编译为替换规则         │ term 列表
                              ▼                       ▼                       ▼
音频 → fbank → LFR/CMVN → ONNX → ctc_logits ──► ① CTC 偏置解码 ──► TextPostprocessor ──► ② 替换 ──► ③ LLM 校对（可选，附词表）
                                               （空表 = 贪心）       （不变）              │                │
                                                                                       │                ▼
                                                                    预览 / 终稿 ◄── ② 替换（幂等）→ 中英文空格 → 上屏
```

| 层 | 解决什么 | 何时生效 | 失败代价 |
| --- | --- | --- | --- |
| ① CTC 偏置 | 同音近音、英文拼法、数字汉字；把识别结果稳定到 term 或 alias 上 | 强度 ≠ 关，且词条有 ≥ 2 个 token | 误触发会改动用户原话，由 §5.4 的声学门限约束 |
| ② 确定性替换 | alias → term；英文大小写与空格归一（voice typer → VoiceTyper） | 总是 | 子串误替换，由规则约束（§6） |
| ③ LLM 词表 | 上下文推断；前两层都没兜住的变体 | 启用智能校对时 | 网络延迟；模型不听话 |

为什么 ① 可行（**S**）：

- `SenseVoiceEngine.recognize` 已经拿到完整的 `ctc_logits`（`[帧数, 25055]`），`CTCDecoder` 目前只取每帧最大值。
  正确字通常就在那一帧的前几名候选里，只是没排到第一。
- `tokens.json`（25055 条）的结构对热词编译非常友好（对本机 ModelScope 缓存的同一文件统计）：
  - **汉字一字一 token**：10123 个纯汉字 token 全部是单字，没有多字 token，也没有带 `▁` 的汉字 token；
  - 英文是 SentencePiece BPE 片段，`▁` 表示词首，**区分大小写**（`▁Code` 与 `▁code` 都存在）；
  - 26 个大小写字母、`0`–`9`、`. - + @ # _` 都有单字符 token，因此**任意 ASCII 写法都能拼出来**；
  - 数字只有单个数字 token（没有 `▁1`、`15`）；
  - 特殊 token `<|…|>` 的 id 从 24884 开始；blank 是 id 0（`<unk>`）。

---

## 4. 配置（VOC-1）

### 4.1 schema

`config.yaml` 新增顶层段，`AppConfig` 新增 `vocabulary: VocabularyConfig`，解码容错与其余段一致（`decodeIfPresent` + 默认值，
旧配置无需迁移）：

```yaml
vocabulary:
  bias: standard            # off | light | standard | strong，识别偏置强度，默认 standard
  entries:
    - term: "张三丰"
    - term: "Claude"
      aliases: ["克劳德"]
    - term: "VoiceTyper"     # voice typer / voice-typer 这类空格、连字符、大小写变体无需写 alias（§6.2）
```

`bias` 未知值回落 `standard`。Windows 移植时使用同一 schema。

### 4.2 校验（`AppConfig.validated()`）

逐条执行，越界只截断、记 warning（**只记条数**）：

1. `term` 去首尾空白，内部的换行、制表等空白收敛为一个半角空格（实施修订）；为空则丢弃整条。长度 > 50 截断。
2. `aliases` 同样规整后去空、去重（不区分大小写）；与本条 `term` 相同（不区分大小写）的丢弃；超过 10 个截断。

手写 YAML 时允许简写（实施修订）：`- 张三丰`（只有 term），`aliases: "克劳德"`（单个字符串）。
3. `term` 重复（不区分大小写）的条目合并 aliases，保留首次出现的写法。
4. 超过 200 条截断。

---

## 5. 识别层：CTC 热词偏置

### 5.1 模块与接口（VOC-2）

新增目录 `Sources/VoiceTyper/ASR/Hotword/`（新增文件后执行 `ruby scripts/generate_xcodeproj.rb`）：

| 文件 | 职责 |
| --- | --- |
| `HotwordCompiler.swift` | 词表前缀树；把 term / alias 编译成 token 序列（§5.2），产出编译报告 |
| `ContextGraph.swift` | Aho–Corasick 上下文图与打分状态（§5.3），不可变、可跨线程共享 |
| `CTCPrefixBeamSearch.swift` | 带偏置的 CTC 前缀束搜索（§5.4） |

接口改动：

```swift
protocol SenseVoiceRecognizing: AnyObject, Sendable {
    func recognize(_ samples: [Float]) throws -> String
    func setLanguage(_ language: ASRLanguage)
    /// 只在 asrQueue 上调用。编译在调用线程同步完成，返回报告供设置页显示。
    func setVocabulary(_ vocabulary: VocabularyConfig) -> HotwordCompileReport?
    /// 最近一次 recognize 的解码统计，只在 asrQueue 上读。
    var lastDecodeStats: DecodeStats { get }
}
// 默认实现：setVocabulary 返回 nil，lastDecodeStats 返回 .greedy —— 测试假实现无需改动。
```

- `SenseVoiceEngine` 持有 `tokens`，在 `init` 时**懒构建**词表前缀树（第一次 `setVocabulary` 时），之后复用。
- `setVocabulary` 编译出新的 `ContextGraph` 并整体替换引用；`entries` 为空或 `bias == .off` 时置为 `nil`。`bias == .off` 时
  仍编译并返回报告（不建图），设置页据此说明哪些词条只做替换（实施修订）。
- `recognize` 末尾：`graph == nil` → 现有 `CTCDecoder.decode`（贪心，**一行不改**）；否则 → `CTCPrefixBeamSearch.decode`，
  两者都把拼好的原始串交给 `TextPostprocessor.process`。
- `ASRService.updateConfig` 比照 `setLanguage`：词汇变化时 `asrQueue.async { engine?.setVocabulary(...) }`；
  引擎加载完成时，与 `setLanguage(latestLanguage)` 同处补一次 `setVocabulary(latestVocabulary)`。编译报告经回调回到主线程。
- `DecodeStats`：`mode`（greedy / beam）、`hotwordHits: Int`（终稿最优路径上完成匹配的次数）、`decodeNanos: UInt64`。

### 5.2 热词编译（VOC-3）

**输入**：每条的 term 与 aliases，统称「写法」（surface）。**输出**：若干 token id 序列，挂到上下文图上。

**步骤 1：生成原始串变体。** 模型输出的原始 token 串里，英文词首带 `▁`、汉字不带。按下列规则把写法改写成原始串：

- 写法切成「运行段」：连续 ASCII 字母数字为一段；单个汉字 / 假名 / 谚文为一段；ASCII 标点（`. - + @ # _`）为一段；空格只作分隔符。
- **词首 `▁`**：ASCII 段位于空格之后时，前面加 `▁`；位于**串首**或汉字之后时**两种都生成**（`TextPostprocessor` 注释所述，
  中英交界处 `▁` 不一致，**S**）。串首也算（实施修订）：写法开头的英文词在中文句子里通常跟在汉字或全角标点后面，真实
  logits 里这时没有 `▁`（`，audio▁driven▁text`，**S**）。
- **拆词变体**：ASCII 段内部在「小写→大写」「字母↔数字」处可以拆开，拆开处补 `▁`（`VoiceTyper` → `▁Voice▁Typer`）。
  模型没见过的合成词，最可能被读成拆开的几个常见词。
- **大小写变体**：原样、全小写、首字母大写。

变体按以下优先级生成，去重后**每个写法最多取 8 条**：原样+合写 → 原样+拆词 → 小写+拆词 → 小写+合写 → 首字母大写（合写、拆词），
每一种先带 `▁`、再不带（实施修订：原稿把不带 `▁` 的版本整体排在后面，会在 8 条上限下被截掉，而它恰是中文语境里最常见的形态）→
第二优切分（步骤 2）。

**步骤 2：切分为 token。** 没有 sentencepiece 模型（下载清单只有 `tokens.json`，**S**），用词表前缀树做动态规划：

- 前缀树只收普通 token：排除 id 0–2（`<unk>` `<s>` `</s>`）与 `<|…|>` 特殊 token。
- 对原始串求 **token 数最少**的切分，取前 2 优；token 数相同时，优先第一个片段更长的（近似 BPE 的贪心合并）。
- 任意字符在前缀树里找不到（如生僻扩展区汉字、emoji、全角符号），该变体作废。

**步骤 3：过滤与汇总。**

- 原样合写变体的最优切分只有 1 个 token：整个写法记为 `tooShort`，不产出任何序列（VOC-D6，实施修订）。否则丢弃其中只有
  1 个 token 的序列。依据（**S**）：`issues` 本身是单 token `▁issues`，但首字母大写变体 `▁I s s ue s` 曾在强档把原文 `isue`
  改成 `▁issuesue`——模型能直接输出的词，偏置别的大小写只会制造误改。
- 所有写法的序列合计上限 **4000** 条，超出按词条顺序截断并记 warning。
- 编译报告逐条给出状态：`biased`（至少一条序列入图）/ `tooShort`（只有单 token）/ `unsupportedCharacters` / `truncated`。

### 5.3 上下文图与打分（VOC-4）

所有序列建一棵 token 前缀树，再补 Aho–Corasick 失配指针（`fail`）。每个节点有：

- `depth`：从根到该节点的 token 数；
- `S(node)`：从根到该节点累计的偏置分，`S(node) = b × min(depth, Dmax)`，`b` 为每 token 偏置，`Dmax = 4`；
- `isEnd`：是否为某条序列的终点。

**每个假设携带的偏置状态**：`(node, committed, anchor, hits)`。

- `committed`：已完整匹配的热词锁定的分；
- `anchor`：当前路径上最近一次完整匹配时的 `S` 值（没有则为 0）；
- 假设的偏置 `bias = committed + S(node) − anchor`，后两项是「正在匹配、尚未完成」的暂存分。

**发射 token k 时的转移**：

```
if node 有子节点 k:  node' = child(node, k); anchor' = anchor
else:                node' = AC 失配链上第一个有子节点 k 的祖先的 k 子节点（找不到则 root）; anchor' = 0
if node'.isEnd:      committed' = committed + S(node') − anchor'; anchor' = S(node'); hits' = hits + 1
```

**语义**（**C**，与 icefall / WeNet 的上下文偏置同构）：

- 正在匹配的热词每多对上一个 token，排名分 +`b`，让它能在束里活到匹配完成；
- **中途失配，暂存分全部撤回**：只对上前缀的路径拿不到任何净收益，不会因为前缀被偏置而改动原文；
- 完整匹配后锁定；`张三` 与 `张三丰` 同时在表时，`张三` 锁定后仍可继续走到 `张三丰`；
- `Dmax` 封顶，使暂存分最多 `4b`，避免长词中途失配时撤回过多、变得「过粘」；
- 已知限制：一个热词是另一个热词的中缀或后缀、且二者重叠出现时，重叠部分可能被重复计分或漏计。极少见，不处理。

**终稿**：束搜索结束时撤回暂存分，`final = 声学分 + committed`。

### 5.4 带偏置的前缀束搜索（VOC-5）

符号：第 t 帧 logits 行 `x_t[k]`，`m_t = max_k x_t[k]`，规范化分 `ℓ_t[k] = x_t[k] − m_t`（最大值为 0）。

**不做 log-softmax（C）**：每个假设每帧恰好消费一个 token 或 blank，对一整帧减同一个常数不改变任何假设之间的排名
（`logaddexp(a+c, b+c) = logaddexp(a,b)+c`）。因此只需减去帧最大值，既省掉每帧 25055 次 `exp`，也无需关心导出的
`ctc_logits` 是否已归一化。偏置 `b` 与门限 `G` 都是同一帧内的分差，语义不变。累加器用 `Double`。

**开头的标签帧**：从第 0 帧起，只要该帧 argmax 是 `<|…|>` 特殊 token（SenseVoice 前置的语言 / 情感 / 事件 / ITN 查询帧），
就按贪心取值、不展开；遇到第一帧 argmax 不是特殊 token 时开始束搜索，初始前缀就是这些标签 token。束搜索中特殊 token
不作为候选。这样不必写死「4 帧」，也不让标签的备选项占用束宽。

**每帧的候选集**（blank 永远是候选）：

```
C_t(h) = { k ∈ TopK_t ∪ RootChildren ∪ Children(h.node) : ℓ_t[k] ≥ −G },  k ∉ {0, 1, 2} 且不是 <|…|>
```

- `TopK_t`：该帧分数最高的 K 个非 blank token；
- `RootChildren`：所有热词序列的首 token，**每帧只筛一次**（与假设无关）；
- `Children(h.node)`：该假设正在匹配的热词的下一个 token；`h.node` 是图根时它就等于 `RootChildren`，不再重复检查（实施修订，纯优化）；
- 另含前缀末 token（停留），同样受门限约束（实施修订：保证门限内的停留不因没进前 K 名而被剪掉；贪心的 argmax 总在 TopK 里）；
- **声学门限 G** 是防误触发的硬约束：与该帧最高分差距超过 `G` 的 token，偏置再大也不考虑。

**跳帧**：若 `x_t[0] − max_{k≥1} x_t[k] > G`（该帧没有任何非 blank token 落在门限内），所有假设只做 blank 扩展：
`pb' = logaddexp(pb, pnb) + ℓ_t[0]`，`pnb' = −∞`。这与候选集规则完全一致，不是近似；CTC 输出很尖锐，
预计大部分帧走这条捷径（**E**）。

**扩展规则**（标准 CTC 前缀束搜索，对数域；`last` 为前缀末 token）：

```
blank:              pb(y)    ⊕= total(y) + ℓ[0]
k == last（停留）:   pnb(y)   ⊕= pnb(y) + ℓ[k]
k == last（新发射）: pnb(y+k) ⊕= pb(y)  + ℓ[k]       // 重复 token 之间必须隔 blank
k != last:          pnb(y+k) ⊕= total(y) + ℓ[k]
```

`⊕=` 是 `logaddexp` 累加；`total = logaddexp(pb, pnb)`。新前缀 `y+k` 的偏置状态由 §5.3 的转移从 `y` 推出。偏置状态是 token
序列的确定性函数，同一前缀从不同路径到达时偏置状态必然相同，**按前缀合并即可**。

**剪枝**：每帧按 `rank = total + bias` 取前 `B` 个。同分时按前缀 token id 字典序，保证可复现。

**前缀表示**：前缀用「节点表 + (父节点, token) → 子节点」的哈希表示，避免每次扩展都复制数组。结束时从最优节点回溯得到 token 序列。

**输出**：按 `final` 取最优，拼 token 串交给 `TextPostprocessor.process`，与贪心路径完全相同。

### 5.5 「一个热词何时被采用」的直观解释

忽略对齐求和的细节，把热词路径和原路径各看作一条序列，热词在一段音频上被采用的条件大致是：

```
Σ（每个 token 的声学分差，均 ≤ G） < b × min(n, Dmax)      （n = 热词 token 数）
```

- 近分候选：累计分差较小时，`b` 可以帮助热词胜出；logits 分差并不等同于是否同音。
- 弱候选：超过 `G` 直接不是候选，即使前面的字已确定，也不能用前缀奖励购买门限外的尾字。
- 凭空插入：每个 token 都要和 blank 比；blank 领先不多时仍有误插风险，需要用反例约束，不能仅凭静音帧通常尖锐就排除。

这条解释写进设置页的帮助文案（§8），也是用户调强度时的心智模型。

### 5.6 参数（VOC-6）

| 档位 | `b`（每 token 偏置） | `G`（声学门限） |
| --- | --- | --- |
| 轻 | 0.75 | 2.0 |
| 标准（默认） | 1.25 | 3.0 |
| 强 | 1.75 | 4.0 |

固定参数：束宽 `B = 8`，`TopK = 10`，`Dmax = 4`。预览与终稿用同一组参数。

**2026-10-24 收敛调整（仍为 E）**：发布初值为轻 `(1.0, 4.0)`、标准 `(1.5, 5.0)`、强 `(2.5, 7.0)`。
用户反馈无关句子也会被积极改成热词后，同时降低累计奖励、收紧候选门限。标准档最高累计奖励从 6 降到 5；
共享夹具新增短词、长词、共享前缀的弱尾字与 blank 误插反例，并保留近分纠错、嵌套词扩展等既有正例。
这只验证合成分数下的取舍，**不等于真实录音的误触发率或召回率已校准**；仍需用 §10.5 的工具校准，
调整依据与验证结果见 `DESIGN.md` §11.7。参数集中定义在 `CTCPrefixBeamSearch.Parameters`，不进 `config.yaml`。

### 5.7 性能预算（E）

- 编码器输出约 60ms 一帧（LFR n=6）：10s 音频约 167 帧，120s 上限约 2000 帧。
- 每帧固定开销：`vDSP_maxvi` 求非 blank 最大值（贪心本来就有一次）。
- 非跳帧额外开销：按 256 个 token 分块扫描（块最大值在门限外就跳过，实施修订）收集 `TopK` 与门限内的热词首 token，再做
  `B × |C|` 次扩展（约 8 × 20）。
- 估计 10s 音频、200 条词汇时解码 < 5ms，120s 时 < 40ms，相对 `DESIGN.md` §4.3 的推理耗时（15s 166ms，30s 262–397ms）可忽略。
  **必须实测**（§10.4），实测值写回 `DESIGN.md`。开发机（Intel i5-8260U）上的合成最坏情况见 `DESIGN.md` §11.7，Apple Silicon 待测。
- 编译：词表前缀树 25k token 只建一次；200 条词汇编译预计 < 10ms，在 asrQueue 上执行，不阻塞主线程。

### 5.8 回退与安全阀

- 束搜索抛出任何内部不一致（如回溯失败），记 `AppLog.asr.error` 并**回退贪心结果**，本次听写不失败。
- 强度「关」是用户可见的总开关；它只关识别偏置，替换和 LLM 词表照常。
- 如果 §10.3 的「无关词汇金标准不变」测试在实施中失败，说明束搜索在非热词文本上与贪心有分歧：先排查实现；
  确认是算法固有差异时，**不改金标准**，改为「贪心锚定」：只把束搜索结果中包含完整热词匹配的片段替换进贪心结果，并回写本节。

---

## 6. 文本层：确定性替换（VOC-7）

新增 `Text/VocabularyReplacer.swift`：由 `VocabularyConfig` 编译出的不可变值类型，`apply(_:)` 为纯函数。

### 6.1 规则

对每条词汇，为 term 本身和每个 alias 各编译一个匹配模式，目标都是 term：

| 写法类型 | 匹配方式 |
| --- | --- |
| 含汉字 / 假名 / 谚文 | 按运行段拼接，汉字与 ASCII 段交界处允许 0–1 个空白，其余逐字匹配 |
| 含 ASCII 段 | 不区分大小写；段两端要求不与 `[A-Za-z0-9]` 相邻（词边界）；§5.2 的拆词处、原写法中的空格或连字符处允许 `[\s\-]?` |

例：`VoiceTyper` 匹配 `voice typer`、`Voice-Typer`、`voicetyper`，替换为 `VoiceTyper`；`GPT-4o` 匹配 `gpt 4o`、`GPT4o`；
`克劳德` 子串匹配，替换为 `Claude`。

### 6.2 匹配与幂等

1. 所有模式在**原文**上找出全部匹配；
2. 按起点升序、同起点取最长、不重叠贪心选取；同起点同长度时 **term 模式优先于 alias 模式**（实施修订）；
3. 一次性拼出结果，替换产物不再参与本轮匹配（不链式替换）。汉字写法换成英文 term 时，若会与相邻的英文字母数字粘连，补一个半角空格
   （实施修订：否则 `克劳德code` 会变成 `Claudecode`）；
4. 整轮重复到结果不再变化，最多 4 轮（实施修订）。单轮做不到幂等：`克劳德code` → `Claude code`，要到下一轮才被 `Claude Code`
   统一，而处理链里替换会跑两次。规则 2 的 term 优先保证已经是某个 term 的文本不会被别的词条的 alias 再改，所以多轮不会变成
   链式替换（`非书→飞书`、`飞书→Lark` 同在表里时，`非书` 只到 `飞书`）。

term 自身的模式总会匹配到已经正确的写法（替换成自己），等于天然的保护区：`Claude Code` 已在原文中时，
alias `Claude` 不会再把它替换成 `Claude Code Code`。**幂等是硬性要求**：`apply(apply(x)) == apply(x)`，单测用夹具与随机拼接做性质测试。

### 6.3 处理链（写进 `OutputFormatter.formatForOutput` 的文档注释）

```
CTC 解码（VOC-5）→ TextPostprocessor（识别契约，不变）
  → VocabularyReplacer.apply        —— LocalASRSession.completeWithASRText，LLM 之前
  → LLM 校对（附词表，VOC-8）
  → OutputFormatter.formatForOutput：
        VocabularyReplacer.apply    —— LLM 可能改回去；幂等，未启用 LLM 时是空操作
      → applyCJKLatinSpacing（R8-F2）
```

- `formatForOutput` 改为 `formatForOutput(_ text: String, vocabulary: VocabularyReplacer)`。`VoiceTyperController` 持有当前的 replacer，
  配置应用时重建；现有 4 处调用（预览、取消、失败兜底、终稿）一并传入。
- `LocalASRSession` 在创建时拿到 replacer 快照（与 `llmCorrector` 一样经 `ASRService.makeSession` 注入）；听写中不会改配置
  （`applyConfig` 本就拒绝）。
- R8 不变量「LLM 输入保持 ASR 原貌」在此处有意放宽：LLM 收到的是替换后的文本。替换只改词条、不改空格与标点，
  few-shot 示例所描述的「中英文紧挨着」的形态不受影响。

---

## 7. LLM 层：词表提示（VOC-8）

沿用 R8-F1 的设计：

- 词表放在最后一条 user 消息里、`<asr_text>` 之前：`<glossary>Claude、VoiceTyper、张三丰</glossary>`。
  system prompt 与 few-shot 不变（保住前缀缓存）。只放 term，不放 aliases（aliases 已被替换层处理）。
- `LLMCorrector.correctWithReport(_:glossary:)` 新增参数；空数组时消息与现状逐字节相同。
- `Resources/correction.md` 增加一条规则：「若用户消息含 `<glossary>`，其中是用户的专有名词表；听起来像表中词语的片段按表中写法还原，
  表外内容照常处理。」**`correction.md` 与 Windows、`client-server/` 共用一份，改动须三方同步**（`DESIGN.md` §5.4）。
- 词表全量发送。200 条 × 50 字符是理论上限，正常使用远低于此，不做相关性筛选。

---

## 8. 界面（VOC-9）

### 8.1 设置窗口「词汇」页

`SetupTab` 新增 `.vocabulary`（排在「识别」之后），新文件 `UI/Settings/VocabularySettingsView.swift`。

```
┌ 词汇 ─────────────────────────────────────────────────────────────┐
│ 列出容易认错的人名、产品名和术语，识别时会优先往这些写法靠。全部在本机完成。 │
│                                                                    │
│ 识别偏向   [ 关 | 轻 | 标准 | 强 ]   ⓘ                              │
│                                                                    │
│ ┌──────────────┬───────────────────────────┬──────┐                │
│ │ 写法          │ 听起来像（可选，逗号分隔）   │ 状态  │                │
│ ├──────────────┼───────────────────────────┼──────┤                │
│ │ 张三丰        │                           │  ✓   │                │
│ │ Claude       │ 克劳德                     │  ✓   │                │
│ │ 李           │                           │  ⚠   │ ← 单字只做替换  │
│ └──────────────┴───────────────────────────┴──────┘                │
│ [+] [−]                                     [从最近结果添加…]       │
│                                                                    │
│ 试一试替换  [ 用克劳德写一个voice typer的插件        ]              │
│            → 用 Claude 写一个 VoiceTyper 的插件                     │
└────────────────────────────────────────────────────────────────────┘
```

- **识别偏向 ⓘ** 的帮助文案：「越强越容易认出表里的词，也越可能把读音相近的普通词认成它。发音明显不同的词不会被改。
  觉得被误改时调低一档。」
- **状态列**来自编译报告：✓ 已生效；⚠ 悬停说明「只有一个字 / 一个词片，不参与识别偏向，仍会做替换」或「含无法识别的字符」；
  模型未加载时显示「—」，悬停「模型加载后检查」；已有报告但查不到的行（刚输入、尚未提交）同样显示「—」，悬停「保存后检查」（实施修订）。
- **试一试**只演示替换层（离线、即时）；识别偏置的效果请直接听写体验，文案里写明。
- 保存语义与「通用」页一致：编辑后即时生效（经 `AppCoordinator.applyConfig`）。听写进行中禁用编辑。
- Windows 的「词汇」页功能相同，保存语义沿用 Windows 设置窗口：表格改动进草稿，与其他页一起「保存并应用」；听写中保存被拒绝、
  草稿保留，不禁用编辑（实施修订，`windows/DESIGN.md` §33.4）。

### 8.2 从最近结果添加

点「从最近结果添加…」弹出 sheet：列出 `DictationHistory` 的最近结果（只在内存，**S**）。用户在文本里选中被认错的片段 →
「写法」框预填该片段，用户改成正确写法 → 保存时生成 `term = 正确写法`、`aliases = [选中的片段]`。
这是降低填表成本的主要入口：用户不必事先猜错法，看到一次错就能加上。

---

## 9. 可观测性与隐私（VOC-10）

- 编译日志（`AppLog.asr.info`）：`词汇已编译：entries=… sequences=… biased=… too_short=… unsupported=… 耗时=…ms`。
- `ASRSessionTimings` 新增 `hotwordHits: Int`、`decodeNanos: UInt64`、`decodeMode`，在终稿推理后从 `engine.lastDecodeStats` 读取；
  `DictationMetrics` 的单行日志追加 `vocab_hits=… decode_ms=…`。遵守「`DictationMetrics` 不得新增 `String` 存储属性」的反射测试。
- **任何日志都不出现词条、alias 或识别文本。**

---

## 10. 测试（VOC-11）

### 10.1 单元测试

| 测试文件 | 覆盖 |
| --- | --- |
| `HotwordCompilerTests` | 用合成小词表：词首 `▁`、汉字后两种 `▁`、拆词、大小写变体、最少 token 切分与次优切分、单 token 过滤、不支持字符、8 条 / 4000 条上限。有真实 `tokens.json` 时追加：`张三丰` → 3 个单字 token；`VoiceTyper` 至少有一条变体由 `▁Vo`/`ice` 等真实片段组成；没有模型时跳过 |
| `ContextGraphTests` | 逐步打分：匹配加分、失配撤回、完整匹配锁定、`张三`/`张三丰` 嵌套、`Dmax` 封顶、AC 失配跳转、终稿撤回暂存分 |
| `CTCPrefixBeamSearchTests` | 合成 logits：①无热词时对尖锐分布与贪心一致（随机性质测试）；②同音替换：原字 −0.3、热词字 −1.5 → 采用热词；③门限：热词字 −9 → 不变；④部分匹配 `ABD` 不泄漏偏置；⑤重复 token `AA` 需隔 blank；⑥开头标签帧走贪心；⑦跳帧与不跳帧结果一致；⑧同分可复现 |
| `VocabularyReplacerTests` | §6 全部规则；幂等性质测试；不链式替换；词边界（`Claude` 不替换 `Claudette`） |
| `OutputFormatterTests` | 处理链顺序：替换在中英文空格之前 |
| `LLMCorrectorTests` | 带词表时 system 与 few-shot 不变、末条消息含 `<glossary>`；空词表时与现状逐字节相同 |
| `AppConfigTests` | schema 解码容错、§4.2 校验、旧配置无 `vocabulary` 段 |

### 10.2 共享夹具（两平台共用，放 `Tests/VoiceTyperTests/Fixtures/`）

- `vocabulary_cases.json`：替换层的输入 / 词汇 / 期望输出。
- `ctc_hotword_cases.json`：合成小词表 + 逐帧 logits + 热词 token 序列 + 档位 → 期望 token 序列。覆盖 §10.1 的 ②–⑦，
  另含三档近分召回 / 弱候选拒绝、长词累计奖励、共享前缀弱尾字、blank 误插与档位差异，共 23 例。
  Windows 移植时 C# 读同一份。

两份文件的 `description` 写明「规则变更须先改 `macos/docs/VOCABULARY_SPEC.md`；既有用例的期望值不得删改」，与
`text_format_cases.json` 的约定一致；同步更新 `Fixtures/README.md`。

### 10.3 金标准回归（需要真实模型，无模型时跳过）

- 空词汇：`speech_zh_en_mixed.wav` 输出与 `speech_zh_en_mixed.reference.txt` 逐字相同（现有测试，不改）。
- **无关词汇**：配置 50 条与该音频内容无关的词汇、强度「强」，输出与**同一引擎的贪心结果**逐字相同。这条钉死「有词汇时整句改走束搜索
  不会改动无关文本」（实施修订：比对本机贪心而不是 Python reference，后者本来就只要求编辑距离 ≤ 2，混进了跨构建 ORT 的差异）。

### 10.4 性能测试

合成 2000 帧 × 25055、尖锐分布（约 75% blank 帧）+ 200 条词汇，测 `CTCPrefixBeamSearch.decode` 耗时；测试只断言一个宽松上限
（实施取 2s：XCTest 按 Debug 构建运行，比 Release 慢约 10 倍），防止数量级退化。真实数值从真机度量日志的 `decode_ms` 取。

### 10.5 离线评估工具（调参用，默认跳过）

`HotwordEvaluationTests`：环境变量 `VT_HOTWORD_EVAL_DIR` 指向一个目录（不入库），内含若干 `*.wav` 与同名 `*.txt`（期望文本），
以及一份 `vocabulary.yaml`。对关 / 轻 / 标准 / 强四档分别输出：

- **召回**：期望文本含某词条、输出也含它的比例；
- **误触发**：期望文本不含某词条、输出却含它的次数；
- **字错误率**（相对期望文本）与平均解码耗时。

推荐的采集方法：挑 10–20 个用户真实会认错的词，每个词各录 3 句含它的话；另录 30 句不含任何词条的日常话，用来测误触发。
§5.6 的参数校准以「标准档误触发为 0、召回尽量高」为目标。

### 10.6 手工验证步骤

1. 不配词汇：听写若干句，确认与升级前一致；度量日志 `decode_mode=greedy`。
2. 挑 5 个当前会认错的词（含同音人名、英文合成词、按中文读的英文名各至少一个），先各听写 3 次记录错误，再加入词汇表重复。
3. 词汇表放 50 条，听写 20 句不含词条的日常话，确认没有被改动的字；分别在「标准」「强」下各做一次。
4. 录音中观察预览：热词在预览里出现，松键后终稿一致。
5. 开启 LLM 校对：确认词条写法在校对后保持；关闭网络时校对回落，替换层仍生效。
6. 120 秒长句 + 200 条词汇：对比 `decode_ms` 与 `release_to_finalize`，确认没有可感知的变慢。
7. 「从最近结果添加」：从一条认错的结果里选中片段添加，再说一遍确认被修正。
8. 编辑词汇时正在听写：编辑控件禁用；听写结束后恢复。

---

## 11. 实施分期

| 阶段 | 内容 | 交付标准 |
| --- | --- | --- |
| 1 · 核心 | VOC-1 配置、VOC-2–6 识别层、VOC-7 替换层、VOC-10 度量、§10.1–10.4 测试与共享夹具 | 单测与金标准测试通过；可手写 YAML 使用 |
| 2 · 界面 | VOC-9 设置页（不含「从最近结果添加」）、本地化、用户文档 | 手工步骤 1–6 通过 |
| 3 · 增强 | VOC-8 LLM 词表（三方同步 `correction.md`）、「从最近结果添加」、§10.5 评估工具跑一轮并回写参数 | 手工步骤 5、7 通过；参数校准结论写入 `DESIGN.md` |
| 4 · Windows | C# 移植：`CtcDecoder` 增加束搜索、上下文图、替换层；读同一份共享夹具 | 夹具全部通过；区分「代码审查通过」与「真机验证通过」 |

阶段 1 合入后，Windows 侧在 `windows/docs/REVIEW_HISTORY.md` 登记为待移植遗留项。

---

## 12. 已否决方案

| 方案 | 否决理由 |
| --- | --- |
| 只靠 LLM 校对 | 默认关闭、要联网、增加 0.5–2s 延迟、对录音中预览无效；小模型对词表的服从不稳定。保留为第三层 |
| 只靠替换表 | 要用户预知每种错法，同音变体组合爆炸；短 alias 误伤正常文本。保留为第二层 |
| 拼音模糊匹配（汉字转拼音后按读音近似替换） | 没有声学证据，短词撞音多（误触发不可控）；它覆盖的同音场景，CTC 偏置在声学门限约束下做得更准 |
| 换用带热词能力的模型（SeACo-Paraformer 等） | 推翻模型契约、下载器与两平台共享夹具；失去 SenseVoice 的多语种与自带标点 / ITN |
| N-gram 语言模型浅融合 | 需要额外的语言模型资源与构建流程；上下文图已覆盖「偏向少量指定词」这一需求 |
| 逐条强度 / 逐条开关 | 用户无法理解数值含义；全局四档 + 删除词条已足够 |
| 从前台应用里的事后修改自动学习 | 需要持续 AX 读取用户文本，隐私与复杂度不可接受；以「从最近结果添加」代替 |

---

## 13. 文档同步清单

实施时随代码一并更新：

- `macos/README.md` / `README.en.md`：新增「自定义词汇」使用说明（不写实现细节）。
- `macos/DEVELOPMENT.md`：`vocabulary` 配置字段、度量日志新字段、评估工具用法。
- `macos/DESIGN.md`：新增 §11.7 记录实施结果、实测性能、参数校准与待真机验证项；§5.2 ASR 子系统补上偏置解码分支。
- `macos/CHANGELOG.md`：用户可感知的新增项。
- `Tests/VoiceTyperTests/Fixtures/README.md`：两份新夹具。
- 根 `README.md`：功能列表补一句。
