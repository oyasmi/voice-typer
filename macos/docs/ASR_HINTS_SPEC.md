# VoiceTyper · 局部声学低置信度备选（`<asr_hints>`）探索与实施规格

[← macOS 目录](../README.md) · [架构设计](../DESIGN.md) · [自定义词汇规格](VOCABULARY_SPEC.md) · [R8 规格](UX_R8_SPEC.md)

| 项 | 内容 |
| --- | --- |
| 状态 | **探索设计已定，待 Step 0 离线验证**（本规格为 `feat/asr-hints` 分支的探索与实施依据；必须先通过 §8 的 Go / No-Go 门禁再合入双端生产代码，验证数据与实施结论回写 `macos/DESIGN.md` 与 `windows/DESIGN.md`） |
| 审查基线 | `1b6e7d3` / 3.6.1 |
| 开发分支 | `feat/asr-hints` |
| 编号 | `HINT-<序号>`（如 `HINT-1`），已定决策 `HINT-D<序号>`。代码注释引用编号时只写编号 |
| 读者 | 实施 Agent / 开发者；产品决策者看 §1–§3 与 §8.4 即可 |

> **证据口径**沿用 `VOCABULARY_SPEC.md`：**S** = 源码或模型文件可直接证明；**C** = 结合 API / 算法契约确认；
> **V** = 待验证；**E** = 估算。本文中的门限参数与收益预期全部属于 **E / V**，**尚未在真实口述录音上验证**；
> 交付时必须严格区分「离线实验结果」「单元测试通过」与「真机验证通过」，不得把估算写成实测（`AGENTS.md`）。

---

## 1. 背景、目标与不变量

### 1.1 问题与机会

开启「智能校对（LLM 校对）」时，云端大模型目前只看得到本地 ASR 输出的 **1-best 文本**（以及可选的 `<glossary>` 专名词表）。这带来两个盲区：

| 盲区 | 例子 | 仅看 1-best 为什么难修 |
| --- | --- | --- |
| **近音偏移 / 轻声吞音** | 幂等 → 密等 / 密度、鉴权 → 健全、降级 → 将近、落盘 → 老盘 | 1-best 偏离了标准同音读法，大模型缺少声学线索，只能凭上下文硬猜甚至不敢改 |
| **语义两可的同音句** | 把**公事/公式**发一下、**期限/期权**到期 | 1-best 表面通顺，按 `correction.md` 保守原则（“拿不准就保留原文”）模型不会动它；但声学上两者 logit 可能几乎打平 |
| **未进词表的生僻术语 / 英文词** | 词表未收的框架名、缩写或中英混读片段 | 1-best 拼歪成近似片段，而次优声学候选仍保留更接近原词的线索 |

与纯云端转写工具不同，VoiceTyper 在本地推理环节已经完整握有 SenseVoice-Small 输出的逐帧 `ctc_logits`（`[validFrames, 25055]`，**S**），且两平台都已实现零拷贝读取与基于分块的向量化扫描（**S**）。在 `finalize` 阶段将**局部低置信度位置的 Top-2/3 声学备选**打包为 `<asr_hints>` 随 `<asr_text>` 一并送给校对模型，可以让大模型从“凭空猜原话”变为“在声学证据提示下做选择”。

### 1.2 为什么不做「整句 Top-3 N-best」

经典 ASR-EC 论文（如 Whisper / Conformer-AED / WFST+LM）多采用整句 Top-N 束搜索候选，但直接套用到 SenseVoice-Small（纯非自回归 CTC，无外部语言模型）存在三个结构性缺陷：

1. **长句信号被标点与语气词占满**：在 15–30s（50–100 字）听写里，纯 CTC 最不确定的帧常是标点（`，` vs `。` vs blank）、语气词或英文词首 `▁`；经 `TextPostprocessor` 清洗去重后，Top-3 经常塌缩为 1 句或仅差一个逗号，真正出错的实词排不进 Top-3。
2. **输入 Token 成本放大 ~3 倍**：无论句子是否清晰，多送 2 句完整文本都会显著拉高输入 token 与小模型首字延迟（TTFT）。
3. **小模型误改风险高**：在 3 句几乎相同的长文间自行 diff 容易引发串行缝合，把 2-best/3-best 的噪声混入原本正确的片段。

因此本方案选择 **「局部低置信度备选（`<asr_hints>`）」**：清晰句子 0 额外开销，只在声学真正拿不准的局部位置给出克制、精准的备选列表。

### 1.3 目标

1. **只增强、不退化**：仅在启用 LLM 校对且 `finalize` 产出有效声学歧义时附带 `<asr_hints>`；清晰语音与未启用校对的请求/结果与现状逐字节一致。
2. **高信噪比与硬上限约束**：全句最多 **5 处**提示，每处最多 **2–3 个**备选（默认上限 3，不凑数）；过滤标点、blank、特殊标签、填充词与等价写法。
3. **不破坏既有体系**：不破坏 Prompt 前缀缓存；不与自定义词汇的热词偏置（VOC-5）及确定性替换（VOC-7）打架；不新增用户设置项与配置字段。
4. **先验证再落码**：先通过 Step 0 离线实验在真实录音上验证净收益（修对数显著高于误改数），达标后再实施 macOS / Windows 双端代码。

### 1.4 非目标

- 不改变录音中实时预览（`preview()`）的行为，预览阶段永不提取 hints。
- 不在未开启 LLM 校对时以任何形式修改 1-best 文本或向用户界面展示候选。
- 不把 `<blank>` 作为“可删除该字”的备选暴露给 LLM（防止诱导过度删字，违背 `correction.md` 删词禁令）。
- 不新增 `config.yaml` 配置项或设置页 UI 开关。
- 不修改 SenseVoice 模型、特征前端（fbank / LFR / CMVN）、`TextPostprocessor` 与既有金标准夹具。

### 1.5 不变量（任何改动都不得破坏）

- **识别契约不动**：`SenseVoiceRecognizing.recognize(_:)`、`CTCDecoder.decode`、`TextPostprocessor.process` 与既有金标准测试（`EndToEndRecognitionTests`、`ctc_hotword_cases.json`）保持逐字不变。
- **隐私红线**：任何日志、度量均**不得**出现 hint 文本（原片段或备选词）；`DictationMetrics` 不得新增 `String` 存储属性（反射测试钉死）。
- **线程与性能**：提取只在 `asrQueue` / `AsrPump` 上执行，直接读取 `ctc_logits` 底层内存（零拷贝），不阻塞主线程 / UI 线程；任何内部异常只放弃 hints（回落为空数组），绝不导致听写或校对失败。
- **跨平台确定性**：macOS（Swift）与 Windows（C#）实现同一提取算法，同分排序规则完全固定，共用 `macos/Tests/VoiceTyperTests/Fixtures/asr_hints_cases.json`。

---

## 2. 已定决策

| # | 决策 | 结论 |
| --- | --- | --- |
| HINT-D1 | 候选形态 | 采用**局部低置信度备选（`<asr_hints>`）**，不做整句 Top-3 N-best（§1.2、§11） |
| HINT-D2 | 硬上限 | 全句最多 **`5` 处**提示（`maxHintsPerUtterance = 5`），每处 **`1–3` 个**备选（`maxAltsPerHint = 3`，仅收门限内备选，不凑数） |
| HINT-D3 | 触发时机 | **仅在 `finalize` 且已启用 LLM 校对时提取**；预览阶段、未启用校对、设置页「测试校对」均不触发 |
| HINT-D4 | 置信度度量 | 使用同帧 **Logit 分差** $\Delta(t, k) = x_t[y] - x_t[k]$，不做全词表 softmax；门限初值 $\tau = 2.5$（**E**，须经 Step 0 校准） |
| HINT-D5 | 两条解码路径统一 | 贪心路径在解码时顺带记录每 token 的非 blank 峰值核心帧；热词束搜索胜出路径（`hotwordHits > 0`）对胜出 token 序列做单路径 CTC 强制对齐取核心帧，并**跳过热词命中保护区间**（§4.1） |
| HINT-D6 | 位置消歧与词级呈现 | 连续 2–3 个低置信度汉字合并为词段联合打分；孤立单汉字自动扩展 1 字相邻上下文锚点；英文 BPE 子词归属到完整英文词 span 后生成词级备选（§4.4） |
| HINT-D7 | 词汇表优先级 | 已由 CTC 热词偏置命中、或经 `VocabularyReplacer.apply` 替换消失的片段不产出/丢弃 hint；已是精确 `term` 的片段不再挑战；备选词本身同步过一遍 `VocabularyReplacer.apply` 并去重（§5.3） |
| HINT-D8 | Prompt 放置与缓存 | `<asr_hints>` 放在最后一条 `user` 消息、`<asr_text>` 之前（与 `<glossary>` 并列），空 hints 时不附带标签；`systemPrompt` 新增 1 条规则，`fewShotMessages` 新增 1 组**正反合一**示例，整体保持前缀缓存稳定（§6） |
| HINT-D9 | 用户配置与 UI | **无新增配置项、无 UI 开关**，作为智能校对的透明内部增强 |
| HINT-D10 | 门禁先行 | 先完成 Step 0 离线评测（真实录音 + 多款快模型），满足 §8.4 Go 门槛后才进入 macOS / Windows 生产代码实施；若帧级提取（B1）在 Step 0 明显弱于束搜索 Diff（B2），则切换底层候选源为 B2，上层 `<asr_hints>` 协议与 Prompt 保持不变 |

---

## 3. 总体架构与处理链

```
音频（finalize 完整段）
  → fbank → LFR/CMVN → ONNX → ctc_logits [T, 25055]
                                   │
           ┌───────────────────────┴───────────────────────┐
           │ 未启用 LLM 校对 / preview()                     │ 已启用 LLM 校对（仅 finalize）
           ▼                                               ▼
  ① 原有 CTC 解码（不变）                         ① CTC 解码 + 发射核心帧对齐
           │                                         （热词命中区间标为受保护）
           ▼                                               │
  TextPostprocessor                                        ├──► 1-best 经 TextPostprocessor
           │                                               └──► ② AcousticHintExtractor（零拷贝读 logits）
           │                                                      └─► 最多 5 处 × 1–3 个局部备选
           │                                               │
           ▼                                               ▼
  VocabularyReplacer.apply ◄────────────────────── ③ VocabularyReplacer.apply(1-best)
           │                                         + 过滤失效 / term 保护 / 备选规整
           │                                               │
           │                                               ▼
           │                                       ④ LLM 校对（<glossary> + <asr_hints> + <asr_text>）
           │                                         （无有效 hints 时不带 <asr_hints>）
           │                                               │
           └───────────────────────┬───────────────────────┘
                                   ▼
                     OutputFormatter.formatForOutput → 上屏
```

各层职责边界清晰互补：

| 机制 | 解决什么 | 生效阶段 | 与 `<asr_hints>` 的关系 |
| --- | --- | --- | --- |
| 自定义词汇（VOC-5/7/8） | 用户明确录入的高频人名、项目代号、固定写法 | 离线 + 校对 | **优先级高于 `<asr_hints>`**；已命中或已替换的词段不再生成矛盾提示 |
| 1-best + `correction.md` | 常规同音字、标点、数字、填充词、口误、常见英文 | 校对 | 基础能力不变；清晰句子直接走此路径，零额外开销 |
| **`<asr_hints>`（本规格）** | 未进词表的近音偏移、吞音/口音、语义两可同音、低频中英混读 | 仅校对终稿 | 提供局部声学证据，辅助大模型做精准取舍 |

---

## 4. 声学备选提取算法（`HINT-1` – `HINT-4`）

新增纯逻辑模块：`macos/Sources/VoiceTyper/ASR/AcousticHintExtractor.swift`（Windows 对应 `windows/Asr/AcousticHintExtractor.cs`），不依赖 AppKit / WinForms / ONNX Runtime，可直接由单元测试与共享夹具驱动。

### 4.1 统一对齐输入：`AlignedToken`（`HINT-1`）

提取器不关心上游是贪心还是束搜索，统一消费如下中间序列：

```swift
struct AlignedToken: Equatable {
    /// 词表中的 token id（已去 blank 与连续重复）。
    let id: Int
    /// 该 token 的代表性发射帧索引（0..<numFrames）。
    let peakFrame: Int
    /// true 表示该 token 属于已命中的热词区间，禁止为它生成备选（HINT-D5）。
    let protectedByHotword: Bool
}
```

两条解码路径的对齐方式：

1. **贪心路径**（无词表、偏向为「关」、或束搜索零命中回退贪心）：
   - 沿用 `CTCDecoder.greedyTokens` 的逐帧 argmax 逻辑；
   - 同一个非 blank token `id` 连续出现的帧区间 `[start...end]` 合并为一个输出 token；
   - 取该区间内 **非 blank 优势最大的核心帧**作为 `peakFrame`：
     $$\text{peakFrame} = \arg\max_{t \in [\text{start}, \text{end}]} \bigl(x_t[\text{id}] - x_t[0]\bigr)$$
     同分取最早帧。理由：音素起止过渡帧靠近 blank，分差噪声大；核心帧最能反映模型对该音节的稳态声学判断（**C**）。
   - 所有 token 的 `protectedByHotword = false`。

2. **热词束搜索胜出路径**（`contextGraph != nil` 且 `result.hotwordHits > 0`）：
   - 已知最终胜出的 token 序列 $y_0, y_1, \dots, y_{L-1}$ 与 `logits[0..<T]`（跳过开头 `<|…|>` 标签帧后的部分）。
   - **单路径 CTC 强制对齐（Viterbi）**：
     - 构造标准扩展序列 $s$（长度 $2L+1$，偶数位为 blank `0`，奇数位 $2i+1$ 为 $y_i$）；
     - 仅沿合法 CTC 转移（停留、前进一步、或跨越 blank 从 $y_{i-1}$ 到不同的 $y_i$）求最大对数得分路径（每帧只读 $x_t[0]$ 与 $x_t[y_i]$，**不扫描 25055 维词表**）；
     - 复杂度 $O(T \cdot L)$，30s 音频（$T \approx 500, L \approx 80$）约 $4 \times 10^4$ 次标量加法，耗时 $< 0.1\text{ms}$（**E**）；
     - 回溯得到每个 $y_i$ 对应的连续发射帧区间，同样按 $\arg\max_t (x_t[y_i] - x_t[0])$ 取 `peakFrame`。
   - **热词区间保护**：
     - 用 `ContextGraph` 在 $y_0 \dots y_{L-1}$ 上线性步进一遍（$O(L)$）：每当状态到达 `isEnd`（节点深度为 $d$），将下标 `[i - d + 1 ... i]` 标记为 `protectedByHotword = true`。
     - 这样被热词偏置刚救回来的词绝不会被 `<asr_hints>` 反向拉回原错字。

3. **前置过滤开头特殊标签**：
   - 开头 `<|…|>` 特殊标签（语言/情感/事件/ITN）直接跳过，不进入 `AlignedToken` 后续分组。

### 4.2 Token 分类与过滤规则（`HINT-2`）

基于 `tokens.json`（25055 条）的实测结构（`VOCABULARY_SPEC.md` §3，**S**），在引擎初始化词表辅助数据时一次性预计算每 token 的静态属性表（与具体音频无关）：

| 属性 | 定义 | 用途 |
| --- | --- | --- |
| `isExcluded` | `TokenTrie.isExcluded(id:token:)`（id 0–2、空串、`<\|…\|>`） | 既不当 target 也不当 alt |
| `stripped` | 去掉词首 `▁` 后的字面串 | 归一化比较、拼装提示文本 |
| `hasLeadingSpace` | 原 token 是否以 `▁` 开头 | 英文词边界切分（§4.4） |
| `isSubstantive` | `stripped` 至少含一个实字（`[0-9A-Za-z\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7A3]`，与 `TextPostprocessor.substantivePattern` 同源） | **非实字（纯标点、纯空白、纯符号）既不当 target 也不当 alt** |
| `isCJKChar` | `stripped` 长度恰好为 1 且是 CJK 基本区汉字（`\u4E00...\u9FFF`） | 汉字连续合并与上下文锚点规则 |
| `isASCIIWordPart` | `stripped` 非空且全部为 ASCII 字母数字（`[0-9A-Za-z]`） | 英文 BPE 词级组装规则 |
| `isPureFiller` | `stripped` 属于固定中文填充词单字集合 `{"呃", "嗯", "啊", "额", "哦", "噢", "唉", "哎"}` | 不作为 target（避免浪费名额提示本该被 LLM 删除的填充词） |

> 注：中文单字助词（如 `的/地/得/了/吗/呢/吧`）暂**不**硬编码剔除，待 Step 0 真实录音统计其占坑比例后再决定是否过滤。

### 4.3 单帧低置信度扫描与 Logit Margin（`HINT-3`）

对于 `AlignedToken` 中每个满足 `!protectedByHotword && isSubstantive && !isPureFiller` 的位置 $i$，读取其核心帧 $t = \text{peakFrame}$ 的 logit 行 $x_t$，设 1-best token 为 $y = \text{id}$、基准分为 $s_y = x_t[y]$：

1. **分差定义**：对任意非 blank、满足 `!isExcluded && isSubstantive` 的竞争 token $k \neq y$，
   $$\Delta(t, k) = s_y - x_t[k]$$
   仅保留满足 $\Delta(t, k) \le \tau$（即 $x_t[k] \ge s_y - \tau$）的候选。
2. **等价去重（与 1-best 比较）**：
   - 将 `stripped[k]` 与 `stripped[y]` 比较：
     - 字面完全相同（如 `▁code` vs `code`）→ 丢弃；
     - 两者均为纯 ASCII 且不区分大小写相同（如 `▁Python` vs `▁python`）→ 丢弃（大小写规范化由 LLM 按 `correction.md` 第 1 条处理，不占用声学备选名额）。
3. **跨语种碎片抑制（可选开关，Step 0 验证）**：
   - 若 $y$ 是单汉字且竞争 token $k$ 是单个 ASCII 字母（如 `a`/`e`/`i`），通常为发音噪声而非真实英文词，默认过滤纯单字母 ASCII 作为汉字 target 的备选（多字母英文词片或数字仍保留）。
4. **分块向量化扫描（复用 VOC-5 思路）**：
   - 与 `CTCPrefixBeamSearch.collectFrameCandidates` 一致，以 `scanBlock = 256` 分块调用 `vDSP_maxv`（macOS）/ `Vector<float>`（Windows），整块最大值 $< \text{Float}(s_y - \tau).\text{nextDown}$ 直接跳过；
   - 仅对通过粗筛的块逐个精确校验并维护按 $(\Delta \text{ 升序}, \text{token id 升序})$ 排列的前 `frameTopK = 4` 个候选（留 1 个余量应对同字等价去重，去重后最多保留 `maxAltsPerHint = 3` 个）。
5. **不扫整段帧**：只对实字发射核心帧扫描（30s 音频约 80–120 帧，而非全部 500 帧），计算成本极低。

### 4.4 片段合并、上下文锚点消歧与英文词组装（`HINT-4`）

直接输出孤立单字（如 `「密」可能是：幂`）在句中出现同名字符时会引发位置歧义（可能误改句中另一个 `密`），而直接输出英文 BPE 子词（如 `「ub」可能是：ab`）对 LLM 几乎不可读。按以下**确定性三步规整**把逐 token 候选组装为自然可读的词级/片段级提示：

#### 步骤 1：词/段分组（Span Grouping）

先将 `AlignedToken` 序列（跳过开头 `<|…|>`）按顺序切分为两类基本单元，然后识别含低置信度 token 的片段：

- **英文完整词单元（ASCII Word Span）**：
  - 连续的 `isASCIIWordPart` token 归属同一个英文词，除非后一个 token 带 `hasLeadingSpace == true`（`▁` 表示新英文词开始）或中间隔着非 ASCII token。
  - 若该英文词内部包含 ≥ 1 个低置信度 token（且词内无 `protectedByHotword` 标记）：
    - 选词内 $\min \Delta$ 最小的至多 **2 个**低置信度子词参与组合（防止超长拼写组合爆炸）；
    - 对选中子词的 `(1-best ∪ 备选)` 做笛卡尔积，拼接成完整英文词，总分差为各子词 $\Delta$ 之和；
    - 剔除与 1-best 完整词不区分大小写相同的组合，按 `(sumDelta 升序, 字典序)` 取前 `maxAltsPerHint`（3）个；
    - 产出一条词级候选，其 `bestMargin = 最小有效组合的 sumDelta`。
- **连续低置信度汉字簇（CJK Cluster）**：
  - 相邻的、均触发低置信度的 `isCJKChar` token（且均非 `protectedByHotword`）合并为一个汉字簇，**单簇最多 3 个连续汉字**（超过 3 个时按从左到右贪心切分为长度 ≤ 3 的子簇）。
  - **长度 2–3 的汉字簇**：对簇内每个字的 `(1-best ∪ 前 2 个备选)` 做联合组合，过滤 `sumDelta > 1.5 * τ` 及等于 1-best 的组合，按 `(sumDelta 升序, 字典序)` 取前 `maxAltsPerHint`（3）个。
    - 例：`密(0)/幂(0.4)` + `等(0)/度(0.8)` → 原片段 `密等`，备选 `幂等 (0.4)`、`密度 (0.8)`、`幂度 (1.2)`。
  - **长度 1 的孤立汉字**：进入步骤 2 的上下文锚点扩展。
- **其他单 token（数字、假名、谚文、混合 token）**：直接作为独立单 token 候选，进入步骤 2 检查是否需要上下文锚点。

#### 步骤 2：单字上下文锚点扩展（消歧）

对于长度为 1 的单汉字候选（下标 $i$）：

1. 优先向**右**吸收 1 个相邻的普通汉字（下标 $i+1$，要求同为 `isCJKChar` 且未跨标点/英文边界），拼成 2 字片段（如 `密` + `等` → `密等`，对应备选同步拼成 `幂等`、`秘等`）；若右侧不可吸收，则向**左**吸收 1 个相邻汉字（下标 $i-1$）。
2. 在清洗后的整句 1-best 文本 `fullText` 中统计该 2 字片段的出现次数：
   - 若仅出现 **1 次**：位置唯一，停止扩展；
   - 若出现 **≥ 2 次**：再向另一侧吸收 1 个相邻汉字（最多扩到 3 字，即左右各至多 1 字）；若仍重复，不再继续扩展（防止拖入过长上下文）。
3. 同一锚点字符如与另一条候选片段重叠（例如 `A[B]C` 与 `B[C]D`）：
   - 若两条独立单字候选间隔恰好 1 个确定字且锚点扩展后重叠，保留 `bestMargin` 更小者优先选择锚点方向，另一条改向另一侧扩展；仍无法避免重叠时允许共存（两者 `surface` 不同，互不干扰）。

#### 步骤 3：全句排序、硬上限截断与输出排序

1. 丢弃清洗后 `surface` 为空、或 `alternatives` 为空的候选；
2. 对同一 `surface` 意外重复的条目去重（保留 `bestMargin` 最小者）；
3. 按 **`(bestMargin 升序, 原文起始 token 下标升序)`** 排序，**硬截断保留前 `maxHintsPerUtterance = 5` 处**；
4. 对胜出的 ≤ 5 处候选，**改按原文起始 token 下标升序重排**，使 `<asr_hints>` 的出现顺序与 `<asr_text>` 从左到右的阅读顺序完全一致。

### 4.5 参数初值（**E**，须经 Step 0 校准）

参数集中定义为 `AcousticHintExtractor.Parameters` 常量结构体，**不进入 `config.yaml`**：

| 参数 | 初值 | 含义与约束 |
| --- | --- | --- |
| `marginThreshold` ($\tau$) | `2.5` | 单帧备选采纳的 logit 分差上限；Step 0 扫 `1.5 / 2.0 / 2.5 / 3.0 / 3.5` |
| `maxHintsPerUtterance` | `5` | 全句提示处数硬上限（HINT-D2） |
| `maxAltsPerHint` | `3` | 单处备选数硬上限（1–3 个，不凑数，HINT-D2） |
| `maxMergedCJKLength` | `3` | 相邻低置信度汉字最大合并字数 |
| `jointMarginMultiplier` | `1.5` | 合并簇联合组合的总分差上限系数（`1.5 × τ`） |
| `frameTopK` | `4` | 单帧粗筛保留数（为去重留 1 个余量） |

### 4.6 性能预算（**E**）与安全阀

- **性能预算**：
  - 仅在 `finalize` 且启用 LLM 校对时执行一次；
  - 10s 音频约 30–40 个实字核心帧、30s 音频约 90–120 个核心帧、120s 上限约 350–450 个核心帧；
  - 分块 `vDSP_maxv` / `Vector<float>` 扫描单帧约 100 次向量化求最大值，预计 10s 音频 **< 0.3ms**，30s 音频 **< 1ms**，120s 上限 **< 4ms**（相对 100–400ms 的本地推理与 500–2000ms 的云端 LLM 往返完全可忽略）。须在真实设备 / 性能测试中实测并回写 `DESIGN.md`。
- **安全阀**：
  - 强制对齐出现数值异常（NaN / 路径不可达）或提取过程发生任何意外错误：记录 `AppLog.asr.warning` 并**返回空 hints**，绝不丢弃或改变已解出的 1-best 文本。

---

## 5. 接口与会话编排（`HINT-5`）

### 5.1 核心数据结构

```swift
// macos/Sources/VoiceTyper/ASR/AcousticHintExtractor.swift
struct AcousticHint: Equatable, Sendable {
    /// 清洗后的 1-best 局部片段（可能含上下文锚点）。
    let surface: String
    /// 按声学分差升序排列的备选片段（1...3 个，不含 surface 本身）。
    let alternatives: [String]
    /// 最强备选的 logit 分差；仅用于排序与离线评估，不发送 LLM，不落日志。
    let bestMargin: Double
}

struct RecognitionResult: Equatable, Sendable {
    let text: String
    let hints: [AcousticHint]
}
```

Windows 对应（`windows/Asr/AcousticHintExtractor.cs`）：

```csharp
internal sealed record AcousticHint(string Surface, IReadOnlyList<string> Alternatives, double BestMargin);
internal readonly record struct RecognitionResult(string Text, IReadOnlyList<AcousticHint> Hints);
```

### 5.2 引擎与缓冲区接口（向后兼容既有测试替身）

- **macOS `SenseVoiceRecognizing`**（`SenseVoiceEngine.swift`）：
  ```swift
  protocol SenseVoiceRecognizing: AnyObject, Sendable {
      func recognize(_ samples: [Float]) throws -> String
      func recognizeWithHints(_ samples: [Float]) throws -> RecognitionResult
      // ... 既有成员不变
  }
  extension SenseVoiceRecognizing {
      func recognizeWithHints(_ samples: [Float]) throws -> RecognitionResult {
          RecognitionResult(text: try recognize(samples), hints: [])
      }
  }
  ```
- **Windows `ISenseVoiceRecognizing`**（`SenseVoiceEngine.cs`）：
  ```csharp
  internal interface ISenseVoiceRecognizing
  {
      string Recognize(ReadOnlySpan<float> samples);
      RecognitionResult RecognizeWithHints(ReadOnlySpan<float> samples) =>
          new(Recognize(samples), Array.Empty<AcousticHint>());
  }
  ```
- **`SenseVoiceEngine` 内部复用推理主干**：抽取私有 `runInference(_:decode:)`，让 `recognize(_:)` 与 `recognizeWithHints(_:)` 共用 fbank → LFR/CMVN → ORT → 零拷贝 logits 指针/Span，仅末步解码闭包不同。
- **`RecognitionBuffer`**：
  - 保留 `preview() -> String` 与 `finalize() -> String` 不变；
  - 新增 `finalizeWithHints() throws -> RecognitionResult`（调用 `engine.recognizeWithHints(audioSnapshot())`）。

### 5.3 会话编排与词汇替换联动（`LocalASRSession`）

1. **`runFinalize` 按需开关**：
   - 检查 `llmCorrector != nil`（在主线程捕获为不可变 `Bool` 常量 `shouldCollectHints` 传入 `asrQueue` / `AsrPump` 闭包）：
     - `false` → 调用原 `buffer.finalize()`（零额外计算，行为逐字节不变）；
     - `true` → 调用 `buffer.finalizeWithHints()`，得到 `RecognitionResult(text, rawHints)`。
2. **`completeWithASRText(_ asrText: String, hints rawHints: [AcousticHint] = [])`**：
   - 先执行 `let text = vocabulary.apply(asrText)`（保持 VOC-7 顺序不变）；
   - 对 `rawHints` 执行**替换后一致性过滤**（纯函数 `VocabularyReplacer.reconcileHints(_:inReplacedText:)`）：
     1. 若 `hint.surface` 不再出现在 `text` 中（说明该处已被 `VocabularyReplacer` 确定性替换），**丢弃该条**；
     2. 若 `hint.surface` 本身精确等于词汇表中任一 `term`（不区分大小写，说明原文已经是用户显式配置的正确词），**丢弃该条**；
     3. 对每条保留的 `hint.alternatives` 逐项运行 `vocabulary.apply(alt)`（例如声学备选里的 alias `克劳德` 自动规整为 `Claude`，与 `<glossary>` 一致），去除与 `hint.surface` 相同项及重复项；若清洗后 `alternatives` 为空，丢弃该条；
     4. 过滤掉任意包含 `<`、`>` 或换行符的异常字符串（结构安全防御）。
   - 将最终 `activeHints` 记入 `timings.asrHintCount = activeHints.count`，并传入 `llmCorrector.correctWithReport(text, glossary: vocabulary.terms, hints: activeHints)`。

---

## 6. LLM 校对提示词与协议（`HINT-6`）

### 6.1 `correction.md` 新增规则（三方同步）

在 `macos/Resources/correction.md`、`windows/Resources/correction.md` 与 `client-server/server/voice_typer_server/prompts/correction.md` 的 `## 总原则` 末尾（紧跟 `<glossary>` 规则第 9 行）新增 1 条：

```markdown
- 若用户消息含 `<asr_hints>`，其中列出了识别引擎在局部拿不准时的声学备选：仅当备选结合上下文明显比原文更合理时才采纳，否则忽略；不要盲从不通顺的备选，也不要输出该标签。
```

### 6.2 `userMessage` 拼接格式

- 签名扩展：
  - macOS：`LLMCorrector.userMessage(_ text: String, glossary: [String] = [], hints: [AcousticHint] = []) -> String`
  - Windows：`LlmCorrector.UserMessage(string text, IReadOnlyList<string>? glossary, IReadOnlyList<AcousticHint>? hints = null)`
- 拼接顺序：`<glossary>`（可选） → `<asr_hints>`（可选） → `<asr_text>`（必选）。
- 格式示例（存在有效 hints 时）：
  ```xml
  <glossary>VoiceTyper、SenseVoice</glossary>
  <asr_hints>
  「密等」可能是：幂等 / 密度
  「将近」可能是：降级
  </asr_hints>
  <asr_text>
  这个接口需要保证密等性，超时之后自动将近处理
  </asr_text>
  ```
- **空 hints 不变量**：当 `hints.isEmpty` 时，输出与现有实现**逐字节完全一致**。
- **`test(_:)` 不变**：设置页「测试校对」继续传 `glossary: [], hints: []`。

### 6.3 `fewShotMessages` 新增 1 组“正反合一”示例

为了同时教会轻量模型「采纳合理备选」和「坚决拒绝噪声备选」，且尽可能节省固定前缀 token（仅增 ~80 tokens，且命中 LLM Prefix Cache），在现有 8 组 few-shot 末尾追加 1 组（第 9 组，macOS 与 Windows 同步）：

- **user**：
  ```xml
  <asr_hints>
  「密等」可能是：幂等 / 密度
  「处理」可能是：出力 / 储理
  </asr_hints>
  <asr_text>
  这个接口需要保证密等性，超时之后自动降级处理
  </asr_text>
  ```
- **assistant**：
  ```text
  这个接口需要保证幂等性，超时之后自动降级处理。
  ```

该示例同时示范 4 条行为约束：① `密等` 结合上下文从备选中采纳 `幂等`；② `处理` 的两个噪声备选被忽略、保留原文；③ 句末标点正常补全；④ 输出纯文本，不携带任何标签或解释。

> Step 0 评测时同步对比「加这 1 组示例」与「不加示例（仅靠 system prompt 规则）」在多款快模型上的效果差异，以实测数据定稿。

### 6.4 `parseOutcome` 输出安全防御

在现有 `<asr_text>` 首尾剥离逻辑之前增加对 `<asr_hints>` / `<glossary>` 意外回显的防御：

1. 若响应包含成对的 `<asr_hints>...</asr_hints>` 或 `<glossary>...</glossary>` 块，先剔除这些标签块再继续走原有的 `<asr_text>` 剥离与判空逻辑；
2. 若剥离后仍残留未闭合的 `<asr_hints`、`</asr_hints>`、`<glossary` 等内部标签片段，说明模型输出已严重跑偏，**直接按 `.fellBack(original)` 回落到 1-best 原文**（`fallbackReason = .emptyResult`），绝不将内部协议标签写入用户的光标位置。

---

## 7. 可观测性与隐私（`HINT-7`）

- `ASRSessionTimings` 新增 `asrHintCount: Int = 0`（Windows `AsrSessionTimings.AsrHintCount`）；
- `DictationMetrics.summaryLine()` 末尾追加 1 个字段（沿用 R8 / VOC-10 末尾追加约定，不改变既有字段顺序）：
  - `asr_hints=<数量>`（当 `llmResult == nil || llmResult == .off` 时输出 `-`，启用了校对时输出非负整数如 `0`、`2`、`5`）；
- `DecodeStats.decodeNanos` / `DecodeTicks` 包含 `AcousticHintExtractor` 耗时，便于直接从线上 `decode_ms` 观察提取开销；
- **隐私红线**：`DictationMetrics` 不新增 `String` 存储属性（继续通过反射测试）；任何级别日志（含 `.debug`）均**不得**记录 `AcousticHint.surface` 或 `alternatives`。

---

## 8. Step 0：离线探索验证方案与 Go / No-Go 门禁（`HINT-0`）

> **本节是进入生产代码实施的前置门禁。** 团队不凭论文结论或合成 TTS 猜测效果，必须先用真实录音与真实云端模型拿到量化数据。

### 8.1 离线评测工具（`macos/scripts/eval_asr_hints.py`）

在 `feat/asr-hints` 分支上先落地一个轻量独立 Python 评测脚本（不进入 App bundle，不修改 Swift/C# 生产代码）：

- 依赖：本机已有的 `onnxruntime`、`numpy`、`pyyaml`（或标准库）与已下载的 SenseVoice-Small ONNX 模型（自动按 `ModelLocator` 规则寻找，支持 `--model-dir` 指定）。
- 语料输入（**语料不入库**）：`--eval-dir /path/to/dataset`，目录结构：
  - `hard/*.wav` + 同名 `*.txt`（真值参考文本）—— 易错/困难口述集
  - `clean/*.wav` + 同名 `*.txt`（真值参考文本）—— 干净防误改集
  - 可选 `vocabulary.yaml`（验证与词表的交互）
- 两种工作模式：
  1. **`--mode oracle`**（离线、零 API 费用）：输出 SenseVoice 1-best CER，并扫 $\tau \in \{1.5, 2.0, 2.5, 3.0, 3.5, 4.0\}$，分别统计 **B1（帧级低置信度）** 与 **B2（束搜索 Top-5 Diff）** 的：
     - 1-best 错字位置上，正确字落入候选项的 **Oracle 召回率**；
     - 平均每句产生的 hint 处数与备选词数；
     - `clean/` 集上的假阳性提示数与误触高发 token（便于检查是否需要过滤助词）。
  2. **`--mode llm`**（调用 OpenAI 兼容端点，`temperature=0`）：支持指定多个模型依次跑：
     - **Baseline**：现行 `correction.md` + 8 组 few-shot + 纯 `<asr_text>`；
     - **Exp-B1**：`<asr_hints>`（帧级提取，取最优 $\tau$）；
     - **Exp-B2**：`<asr_hints>`（束搜索 Top-5 Diff 压缩版）；
     - **Exp-A**（可选对照）：整句 Top-3 N-best，用于实证对比 token 增量与误改率。
     - 输出逐句 diff 报告（仅落盘到用户指定的本地临时目录，不入库）及汇总指标表。

### 8.2 最小有效真实语料集建议

**严禁使用 TTS 合成音频代替真实人声**（`DESIGN.md` §11.7 实测 TTS 的次优 logit 普遍落后 4–12，与真实口述完全不同，**S**）。建议采集 **40–50 条**真实麦克风口述录音（16kHz wav）：

| 分组 | 建议条数 | 覆盖内容 | 评测目的 |
| --- | --- | --- | --- |
| `hard/`（易错集） | 25–30 条 | 从日常真实出错的听写里收集：近音偏移、快语速轻声、专业技术术语、中英混排、语义两可同音句（建议覆盖短句 3–6s 与长句 15–30s） | 测 Oracle 召回率、剩余错误消减率 |
| `clean/`（防误改集） | 15–20 条 | 日常口述中 SenseVoice 1-best 已经全对或接近全对的句子 | 测假阳性提示数与**误改率（把对的改错）** |

### 8.3 评测模型矩阵

至少覆盖 **2–3 类**典型用户实际配置的轻量/常用模型（`temperature = 0`）：
1. 轻量极速云端模型（如 `gpt-4o-mini` / `qwen-flash` / `doubao-1.5-lite` 等）；
2. 中等通用模型（如 `deepseek-chat` / `qwen-plus` / `gpt-4.1-mini`）；
3. （可选）本地小模型（如 Ollama / MLX 下的 1.5B–7B 模型，验证小模型是否出现标签回显或过度改写）。

### 8.4 Go / No-Go 决策门禁

| 指标 | Go 门槛（须全部满足） | 说明 |
| --- | --- | --- |
| **困难集剩余错误消减率** | **≥ 15%**（理想 ≥ 25%） | 在 Baseline LLM 仍未修对的错字里，加入 `<asr_hints>` 后新修对的比例 |
| **净收益比（修对 : 误改）** | **≥ 3 : 1** | 新修对字数显著高于新改错字数（防止正负抵消） |
| **干净集误改率增量** | **≤ 0.3%**（近乎 0 误改） | 绝不以破坏清晰句子的稳定性为代价 |
| **格式稳定性** | **标签回显 / 格式破坏 = 0 次** | 在所有测试模型上零泄露 |
| **Token 增量** | 平均每句 **≤ 50 input tokens** | 保持低成本、低 TTFT 增量 |

#### 决策分支

- **路径 1（B1 达标）**：冻结校准后的 $\tau$，进入 §10 阶段 1（macOS）与阶段 2（Windows）正式实施。
- **路径 2（B1 不达标，但 B2 束搜索 Diff 达标）**：说明错因主要来自吞音漏字或英文 BPE 变长拆分；保持 `<asr_hints>` 协议、Prompt 与硬上限（5 处 × 1–3 备选）完全不变，将 §4.1–§4.4 的底层候选源替换为“复用 `CTCPrefixBeamSearch` Top-5 → Token Diff 压缩”，修订本规格后实施。
- **路径 3（B1 / B2 均未达标）**：说明 SenseVoice-Small CTC 的错误帧本身 Oracle 召回偏低，或轻量模型无法稳定利用局部备选；**将实测报告回写本规格与 `DESIGN.md` 后终止实施，不合并生产代码**，避免长期维护零净收益的复杂度。

---

## 9. 自动化测试、共享夹具与手工验收（`HINT-8`）

*（仅在 Step 0 通过门禁、进入代码实施阶段时执行）*

### 9.1 单元测试

| 测试文件（macOS / Windows） | 覆盖点 |
| --- | --- |
| `AcousticHintExtractorTests` | 读取共享夹具 `asr_hints_cases.json`；验证贪心核心帧选择、强制对齐 + 热词保护区间、标点/blank/填充词/大小写过滤、连续汉字合并、单字锚点扩展与重复词消歧、英文 BPE 词级组装、5 处 / 3 备选硬上限、原文顺序重排、NaN 安全回退为空 |
| `VocabularyReplacerTests` | `reconcileHints`：已被替换的片段丢弃、精确 term 保护、备选内 alias 规整与去重、含 `<`/`>`/换行防御 |
| `LLMCorrectorTests` | 带 hints 时 system 与 few-shot 不变、末条消息按 `<glossary>` → `<asr_hints>` → `<asr_text>` 顺序拼装；空 hints 时与现状逐字节相同；响应回显 `<asr_hints>...</asr_hints>` 时剥离，残缺标签时安全回落原文 |
| `LocalASRSessionTests` | 未开启 LLM 校对不调用 `recognizeWithHints`；预览不调用 `recognizeWithHints`；开启校对时 `finalize` 收集 hints、经替换过滤后透传给 `LLMCorrector` 并记录 `timings.asrHintCount` |
| `DictationMetricsTests` | `summaryLine()` 末尾输出 `asr_hints=`；反射测试继续确认无 `String` 存储属性 |

### 9.2 共享夹具（`macos/Tests/VoiceTyperTests/Fixtures/asr_hints_cases.json`）

两平台共用同一份夹具（Windows 通过 `VoiceTyper.Tests.csproj` Link 引用，沿用 `ctc_hotword_cases.json` 约定），至少包含以下 11 类用例：

1. 全句高置信度（所有帧 $\Delta > \tau$）→ 空 hints；
2. 单个汉字低置信度 → 自动带 1 字相邻上下文锚点；
3. 句中重复单字 → 自动向另一侧扩展到 3 字消歧；
4. 连续 2–3 个低置信度汉字 → 合并为词级联合备选，按总分差排序；
5. 超过 3 个连续低置信度汉字 → 切分且不发生组合爆炸；
6. 英文 BPE 子词低置信度 → 组装为完整英文词备选，大小写等价变体去重；
7. 标点、blank、`<|…|>`、纯填充词、`▁` 变体 → 全部过滤；
8. 全句超过 5 处低置信度 → 按最小 `bestMargin` 保留前 5 处，最终按原文位置升序输出；
9. 单处超过 3 个门限内备选 → 截断保留前 3 个（同分按 token id 升序）；
10. 热词束搜索胜出路径 → 热词命中区间受保护不产出 hints，句中其余非热词低置信度位置正常产出 hints；
11. 贪心多帧持续发射 → 选非 blank 峰值核心帧而非边缘过渡帧。

### 9.3 金标准与性能回归

- 金标准回归：`speech_zh_en_mixed.wav` 下 `recognize()` 输出与既有参考逐字不变；
- 性能测试：2000 帧（120s）× 25055 合成 logits 下测 `AcousticHintExtractor` 耗时，确认相对推理耗时可忽略（Debug 宽松上限断言，真机 Release 从 `decode_ms` 采数）。

### 9.4 手工验证步骤（真机）

1. **关闭智能校对**：听写 10 句日常与易错句，确认度量日志 `asr_hints=-`，识别结果与 `decode_ms` 无退化。
2. **开启智能校对（清晰句）**：听写 10 句清晰日常话，确认多数句子 `asr_hints=0`（零额外 token），无误改、无标签泄露。
3. **开启智能校对（易错句）**：针对 Step 0 选出的 10 句易错口述对照开启/关闭 `<asr_hints>`（或对照升级前后），确认近音错词修正率提升、上屏延迟无明显变慢。
4. **开启智能校对 + 自定义词汇**：口述含词表 alias 与 term 的句子，确认词表写法不被 `<asr_hints>` 改坏。
5. **异常/弱网兜底**：断网或超时下听写，确认仍回落 1-best 原文（带词汇替换），无异常字符上屏。

---

## 10. 实施分期

| 阶段 | 内容 | 交付与验收标准 |
| --- | --- | --- |
| **阶段 0 · 离线探索验证（当前）** | `HINT-0`：编写 `macos/scripts/eval_asr_hints.py`，采集 40–50 句真实录音，完成 0A Oracle 诊断与 0B 多模型 A/B/C 对比 | 产出量化评测报告，完成 §8.4 Go / No-Go 评审并确定 $\tau$（及 B1 / B2 选型） |
| **阶段 1 · macOS 核心与 Prompt** | `HINT-1`–`HINT-8`：Swift `AcousticHintExtractor`、`SenseVoiceEngine`、`RecognitionBuffer`、`LocalASRSession`、`LLMCorrector`、三份 `correction.md`、度量与共享夹具 `asr_hints_cases.json`；同步执行 `ruby scripts/generate_xcodeproj.rb` | `xcodebuild test` 全绿；真机跑通 §9.4 手工步骤并记录实测 `decode_ms` |
| **阶段 2 · Windows 同步** | C# 移植 `AcousticHintExtractor.cs`、对齐 `SenseVoiceEngine.cs` / `RecognitionBuffer.cs` / `LocalAsrSession.cs` / `LlmCorrector.cs` / `DictationMetrics.cs`，链接同一份 `asr_hints_cases.json` | `dotnet test` 与共享夹具全绿；按 `AGENTS.md` 区分「代码审查通过」与「真机验证通过」 |

---

## 11. 已否决方案

| 方案 | 否决理由 |
| --- | --- |
| 直接发送整句 Top-3 N-best | 纯 CTC 长句易被标点/语气词占满槽位；输入 token ×3；小模型在 3 句长文间 diff 易缝合噪声导致误改。仅在 Step 0 中作为对照组实证比较 |
| 裸单字提示（不带上下文锚点） | 句中出现重复字时无法定位，极易诱导 LLM 误改句中其他位置的同名正确字 |
| 将 `<blank>` 作为备选（提示“可能是多识别的字”） | 容易诱导轻量模型随意删字，破坏 `correction.md` “只允许删除三类内容”的保守原则 |
| 完整 Softmax 概率 + 熵作为门限 | 每帧需对 25055 维做 `exp()` 与求和；同一帧内任意两候选的后验概率比严格由 logit 差 $\Delta$ 决定，直接用 $\Delta$ 在数学上等价且与热词门限 `G` 同量纲、免除全词表 `exp()` |
| 在录音预览（`preview()`）阶段提取 hints | 预览不走 LLM 校对，提取结果无消费者，纯属浪费 CPU |
| 新增 `config.yaml` 字段与设置页开关 | 普通用户无法理解“声学低置信度备选”的含义与调参意义；应作为智能校对的透明内部增强，若 Step 0 证明其不够稳定则不上线，而非靠加开关转嫁给用户 |

---

## 12. 文档同步清单

通过 Step 0 门禁并完成代码实施时，随代码一并更新：

- `macos/DESIGN.md`：新增小节记录 Step 0 实测数据、实施结果、实测 `decode_ms` 与待真机验证项；§5.2 / §5.4 补充 `<asr_hints>` 数据流。
- `windows/DESIGN.md` & `windows/docs/REVIEW_HISTORY.md`：记录 Windows 同步状态与真机验证清单。
- `macos/DEVELOPMENT.md` & `windows/DEVELOPMENT.md`：补充度量日志新字段 `asr_hints` 与离线评测脚本用法。
- `macos/CHANGELOG.md` & `windows/CHANGELOG.md`：若效果达标上线，用用户语言记一条智能校对准确率改进（不暴露内部标签与算法细节）。
- `macos/Tests/VoiceTyperTests/Fixtures/README.md`：登记 `asr_hints_cases.json`。
