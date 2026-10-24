# Fun-ASR-Nano 模型选型调研

- 调研与决策日期：2026-10-23。
- 适用范围：`macos/`、`windows/` 的进程内本地桌面应用；不改变旧客户端—服务端版本的定位。
- 状态：**暂缓引入 Fun-ASR-Nano，继续仅支持 SenseVoice-Small。**
- 证据范围：官方模型卡、技术报告、部署项目文档，以及仓库已有实现与性能记录。本轮没有运行模型对照测试。

## 1. 决策与理由

目前没有足够证据证明 Fun-ASR-Nano 能在 VoiceTyper 的主要听写场景中，带来足以抵偿资源占用、延迟和
迁移成本的准确率提升。因此搁置替换或新增模型的工作，不增加可选模型、下载入口或相关配置，也不启动
实现或性能验证任务。

SenseVoice-Small 发布较早，并不直接意味着它已不适合桌面听写。它的非自回归架构仍然符合本项目对快速
预览、松键后及时插入和后台常驻的要求。Nano 在方言、口音、中英混说和复杂声学场景中值得关注，但不能
将这些潜在收益写成已经在本项目验证的结论。

本次决策的主要依据：

1. Nano 的常见本地量化部署需要约 1 GB 模型文件，约为当前 SenseVoice int8 权重体积的四倍。
2. Nano 已能在 CPU 上实用运行，但公开耗时不足以证明能维持当前桌面预览和终稿的响应体验。
3. 官方 Nano 评测表没有直接与 SenseVoice-Small 对照；另一组普通话评测中，Nano 的字错率还略高于
   SenseVoice。现阶段不能认定其在常规普通话听写上有显著收益。
4. 迁移需要适配自回归解码、tokenizer、缓存、取消、预览和长音频处理，并同步考虑 macOS 与 Windows。

本文保留供日后查阅，不构成待实施规格或版本承诺。重新评估的条件见第 7 节。

## 2. 模型定位与架构差异

| 项目 | SenseVoice-Small | Fun-ASR-Nano-2512 |
| --- | --- | --- |
| 参数量 | 约 234M | 约 800M，约为前者的 3.4 倍 |
| 主要架构 | 语音编码器 + CTC，非自回归 | 语音编码器 + 适配器 + Qwen3-0.6B，自回归生成 |
| 主要取舍 | 推理快速，解码与部署相对简单 | 增强语言上下文建模，增加解码与资源成本 |
| 语言覆盖 | 普通话、粤语、英语、日语、韩语 | 中英日；中文包含更多方言和地方口音 |
| 本地部署 | 本项目已有 Swift / C# + ONNX Runtime 实现 | 已有 ONNX、sherpa-onnx、GGUF 等部署路线 |

Nano 可以视为同一团队的新一代 ASR 产品，但不是直接替换 SenseVoice 权重的兼容升级。
官方原生运行时描述的 Nano 架构为 SenseVoice SAN-M 编码器、适配器和 Qwen3 解码器的组合；名称中的
“Nano”不表示它比 SenseVoice-Small 更小或更快。[S1][S2]

必须区分以下三个对象：

- **Fun-ASR-Nano-2512**：本次重点评估的约 0.8B 检查点，支持中英日，以及中文的多种方言、口音。
- **Fun-ASR-MLT-Nano-2512**：另一个多语言检查点，官方列出 31 种语言；不能把它的覆盖范围直接算给基础 Nano。
- **Fun-ASR**：评测中还包含约 7.7B 的模型，其准确率与流式能力不能直接当成 0.8B Nano 的部署能力。

基础 Nano 不能直接覆盖当前产品的韩语能力。需要多语言版本时，应对该检查点单独评估。[S1]

## 3. 模型体积与内存

下表是必需模型文件的近似合计，不是压缩下载包体积，也不是运行内存。不同导出会拆分或复制权重，不能
仅按参数量推算实际分发大小。

| 配置 | 模型文件体积 | 依据 |
| --- | --- | --- |
| 当前 SenseVoice int8 ONNX | 权重 241,216,270 字节，约 241 MB；另有少量配置与词表 | 当前下载器的固定文件清单 |
| Nano：sherpa-onnx int8 | 约 1 GB，含编码器、LLM、embedding 和 tokenizer | 编码器约 227 MiB、LLM 约 573 MiB、embedding 约 149 MiB，另加词表 |
| Nano：GGUF，F16 编码器 + Q4 LLM | 约 954 MB，另加少量辅助文件 | 约 470 MB 编码器 + 484 MB LLM |
| Nano：GGUF，F16 编码器 + Q8 LLM | 约 1.28 GB，另加少量辅助文件 | 约 470 MB 编码器 + 805 MB LLM |

来源：[当前下载器](macos/Sources/VoiceTyper/ASR/ModelDownloader.swift)、[S3][S4]。
MB 与 MiB 采用来源标注，均作近似比较，不应据此生成精确下载进度或容量预算。

Nano 的量化部署通常仍需要当前约四倍或更多的模型文件，增加首次下载、存储和模型加载成本。不能只计算
语音编码器，也不能把 LLM 的量化文件大小当成整个识别系统的大小。

运行内存还包括推理缓冲、KV cache、执行器开销等，并受内存映射和分配策略影响。仓库历史记录中，
SenseVoice 在 M4 上禁用 ORT prepacking 后约占 510 MB 常驻内存；该记录不是本轮复测，也不等于所有
平台上完整应用的内存预算。没有找到与本项目条件一致的 Nano 常驻与峰值内存对照，不给出桌面端实测结论。
详见 [macOS 设计文档 §4.3](macos/DESIGN.md#43-实测性能与内存m4)。

## 4. 推理耗时与桌面预览

### 4.1 当前 SenseVoice 基线

仓库已有 M4 性能记录如下。本轮没有复测；它们是识别阶段的耗时，不是从松键到文本插入的端到端耗时。

| 音频时长 | 识别耗时 |
| --- | --- |
| 1 秒 | 21 ms |
| 5 秒 | 61 ms |
| 15 秒 | 166 ms |
| 30 秒 | 262～397 ms |

模型加载约 0.85～0.98 秒。来源同上；不能将 M4 数值外推为 Windows 真机结果。

### 4.2 Nano 的公开 CPU 数据

RTF = 处理耗时 / 音频时长，越低越快；小于 1 仅表示处理速度快于音频播放速度。

| 实现 | 公开运行条件 | 公开结果及限制 |
| --- | --- | --- |
| DakeQQ ONNX | Ubuntu 24.04，i7-1165G7 CPU，Q4，8 秒音频块 | RTF 0.11，对应约 880 ms；不是本项目或 Windows 实测 |
| sherpa-onnx int8 | CPU，两线程；页面未明确 CPU 型号 | 一条 7.012 秒示例耗时 1.154 秒，RTF 0.165；不是完整性能评测 |

来源：[S3][S5]。硬件、输入、线程数和计时边界不一致，不能据此计算 Nano 相对本项目 SenseVoice 慢多少倍。
也没有据此推算 M4 或 Windows arm64 的性能。

### 4.3 对 VoiceTyper 的影响

当前 macOS 实现对最近最多 15 秒音频反复识别来生成完整预览，松键后再对完整录音识别。预览调度至少
积累 300ms 新音频，且会按上一次推理耗时退让；不是始终固定每 600ms 重跑。参考
[RecognitionBuffer](macos/Sources/VoiceTyper/ASR/RecognitionBuffer.swift) 与
[LocalASRSession](macos/Sources/VoiceTyper/ASR/LocalASRSession.swift)。

如果窗口识别变成秒级，预览刷新和终稿排队都可能变慢。自回归解码还受输出 token 数影响，相同时长的
音频不保证相同耗时。因此“快于实时”、服务器批量吞吐或 GPU 短样本延迟，都不足以证明能满足本项目体验。

sherpa 的 Nano 接口属于 `OfflineRecognizer`。工具包支持其他流式模型，并不意味着 Nano 已提供可直接
接入的增量识别接口。[S7] 当前 SenseVoice 本身也不是原生流式模型；比较时应对照实际预览语义与端到端
延迟，而不是只比较“流式”标签。

## 5. 识别准确率与证据限制

官方 Nano 模型卡公布了 AISHELL-1 1.80%、Fleurs 中文 2.56%、WenetSpeech Meeting 6.60% 等错误率结果，
但表中没有 SenseVoice-Small 的直接对照。部分行业评测使用内部数据，不能直接复现，也不能换算成当前
产品准确率的提升。[S1]

另一组由官方原生运行时仓库公布的评测，使用 184 条有人工参考文本的普通话录音、内置 FSMN-VAD，以及
统一中文归一化方式，给出的完整数据集 micro-CER 为：

| 模型 | 中文字错率 CER，越低越好 |
| --- | --- |
| SenseVoice-Small | 8.01% |
| Fun-ASR-Nano | 8.30% |

来源：[S6]。该文档还保留了其他配置的结果，不能混用其参考模型、量化运行时和分段方案的数值。这组数据
规模有限、录音偏长，也不代表本项目的短句听写分布；小幅差异不能解释为统计显著优势。它能说明的是：
**Nano 更新、更大，并不保证在普通话任务上比 SenseVoice 更准确。**

值得日后验证的潜在收益集中在方言、地方口音、中英混说、噪声和专业词汇。当前没有本项目对照数据，
不能据此宣称 Nano 在这些场景中已优于当前实现。

用户实际需要的是忠实、可直接使用的文本。重新评估时，除 CER / WER 外，还需检查数字、金额、日期、
否定词、人名、英文术语、标点与 ITN，以及静音幻觉、重复、漏句和擅自改写。统一归一化后的错误率可能
掩盖书写格式差异，不能单独作为换模型的理由。

## 6. 部署路线与迁移成本

| 路线 | 已确认的信息 | 对本项目的判断 |
| --- | --- | --- |
| ModaLeap ONNX | 第三方 CUDA 导出；作者明确说明实际面向 GPU，FP16 LLM 在 CPU 上不适合实时使用 | 不能用搜索摘要中的“CPU support”作为桌面 CPU 性能证据 |
| sherpa-onnx | 有 CPU int8 模型与原生 C API，Nano 属于离线识别接口 | 日后可作为跨平台验证候选；仍需核对具体平台产物与推理表现 |
| DakeQQ ONNX | 有 Q4 CPU 耗时，提供 CTC 快速头与自回归解码路线 | 可作为量化与延迟验证候选，公开数字不能直接套用 |
| Wasser1462 / csukuangfj | 提供 ONNX 导出、推理参考；sherpa 文档链接到相关导出脚本 | 适合追溯模型契约，Python 服务示例不应变成本项目部署结构 |
| 官方 GGUF 原生运行时 | C++ 本地运行，支持量化模型和 VAD | 本地部署可行，但需要引入或封装不同运行时 |

来源：[S2][S3][S5][S7][S8][S9][S10]。ModaLeap 报告的 RTX 4090 短片段耗时和约 2.2 GB 显存占用属于
该实现的 GPU 条件，且作者没有正式 WER 评测；不能据此认定 CPU、Apple Silicon 或本项目性能。[S8]

ONNX 导出已存在，因此“没有 ONNX”不是搁置理由。真正的成本是：

- 当前 CTC logits 契约不能直接替代 LLM 的 embedding、tokenizer、逐步生成和 KV cache 管理。
- 需要重新处理预览刷新、终稿生成、取消、输出长度限制及长录音分段，验证完整预览可回溯修正的语义。
- 现有 CTC 热词偏置不能直接当作 LLM 提示词热词使用；需分别验证偏置效果和误插入风险。
- 两个平台的模型下载、固定 SHA256、加载卸载、内存预算和错误处理需要同步评估。
- 后续如修改特征处理或模型契约，仍须遵守跨平台评估要求，继续共用
  [金标准夹具](macos/Tests/VoiceTyperTests/Fixtures/README.md)。这些夹具包含当前实现的一致性参考，不能
  直接替代评估新模型准确率所需的人工转写语料。

可在进程内通过原生库实现全离线推理，不需要因此恢复独立 Python 服务或传输音频。Nano 内置的本地
LLM 解码器与产品现有的可选联网 LLM 纠错是两个不同环节。

## 7. 日后重新评估的条件

仅在出现明确需求或新证据时再决定是否重启，例如：

- 用户反馈中，方言、口音、混说或专业词错误形成可复现的主要问题，现有词汇功能仍无法有效改善。
- 出现针对 Apple Silicon、Windows x64 / arm64 的可复现低延迟结果，或完整模型体积明显缩小。
- 有与 SenseVoice 在同一语料、同一硬件及一致计时边界下的对照，显示具有实际意义的准确率收益。

重启后应先做独立验证，不先增加用户设置或更换默认模型。候选可包含当前 SenseVoice int8、Nano sherpa
int8 与 Nano DakeQQ Q4，固定模型和运行时版本、文件哈希与生成参数。

验证应覆盖真实听写短句，以及带停顿的 30～120 秒录音；分别记录：

1. 中文 CER、英文 WER、关键实体正确率、标点与数字格式，以及重复、漏句和幻觉。
2. 首次预览、预览刷新间隔、松键到插入的 P50 / P95，并拆分排队与推理耗时。
3. 冷加载、常驻与峰值内存、持续录音 CPU 占用和功耗。
4. Apple Silicon、Windows x64 和 Windows arm64 的真机结果；代码审查、Linux 测量与平台外推须分别标注。

正式验证前应约定最低收益和可接受延迟，例如目标场景错误率相对下降 15%～20%；这只是可讨论的产品
门槛示例，不是现有测量结果或已经批准的验收标准。验证结果足以支持引入时，再单独决定产品定位。

## 8. 来源与后续查阅

以下网页查阅于 2026-10-23。外部 `main` 分支、模型卡与导出包会变化；本次没有下载、固定或验证 Nano
产物。重启评估时需重新核对具体 revision、许可证、量化配置与平台支持，不能把本文当作最新兼容性清单。

- **[S1]** [FunAudioLLM/Fun-ASR-Nano-2512 模型卡](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-2512)：模型规模、语言范围与准确率表。
- **[S2]** [FunASR 原生 llama.cpp / GGUF 运行时](https://github.com/modelscope/FunASR/tree/main/runtime/llama.cpp)：模型架构与本地运行路线。
- **[S3]** [sherpa-onnx Nano 预训练模型文档](https://k2-fsa.github.io/sherpa/onnx/funasr-nano/pretrained.html)：int8 文件清单、CPU 配置与示例耗时。
- **[S4]** [FunAudioLLM/Fun-ASR-Nano-GGUF 模型卡](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF)：编码器与量化 LLM 的配套体积。
- **[S5]** [DakeQQ/Automatic-Speech-Recognition-ASR-ONNX](https://github.com/DakeQQ/Automatic-Speech-Recognition-ASR-ONNX)：ONNX 路线、CTC / AR 支持与 CPU RTF。
- **[S6]** [FunASR 原生运行时 BENCHMARKS.md](https://github.com/modelscope/FunASR/blob/main/runtime/llama.cpp/BENCHMARKS.md)：184 条普通话录音的 CER 与分段、归一化方法。
- **[S7]** [sherpa-onnx C API](https://github.com/k2-fsa/sherpa-onnx/blob/master/sherpa-onnx/c-api/c-api.h)：Nano 的离线模型配置与原生接口。
- **[S8]** [ModaLeap/fun-asr-nano-onnx 模型卡](https://huggingface.co/ModaLeap/fun-asr-nano-onnx)：CUDA 导出、CPU 使用限制及评测范围。
- **[S9]** [Wasser1462/FunASR-nano-onnx](https://github.com/Wasser1462/FunASR-nano-onnx)：ONNX 导出与推理参考。
- **[S10]** [csukuangfj/FunASR-nano-onnx](https://github.com/csukuangfj/FunASR-nano-onnx)：相关导出分支。
- **[S11]** [Fun-ASR Technical Report，v4](https://arxiv.org/html/2509.12508v4)：模型家族的训练与评测背景；需区分家族能力与具体 Nano 部署能力。
- **[S12]** [SenseVoice 技术报告](https://arxiv.org/html/2407.04051v2)：SenseVoice-Small 的规模、语言与非自回归设计。

仓库内的实现与历史证据入口：

- [macOS 设计文档](macos/DESIGN.md)、[Windows 设计文档](windows/DESIGN.md)。
- [macOS SenseVoice 引擎](macos/Sources/VoiceTyper/ASR/SenseVoiceEngine.swift)、[Windows SenseVoice 引擎](windows/Asr/SenseVoiceEngine.cs)。
- [macOS 会话](macos/Sources/VoiceTyper/ASR/LocalASRSession.swift)、[共享金标准夹具说明](macos/Tests/VoiceTyperTests/Fixtures/README.md)。
