# Fixtures

本目录下的文件用于 `FbankParityTests` 与 `EndToEndRecognitionTests` 的金标准比对，以及与 Windows 共享的规则用例
（`text_format_cases.json`、`vocabulary_cases.json`、`ctc_hotword_cases.json`）。

## speech_zh_en_mixed.wav

- 来源：合成语音（TTS），非真实用户录音，内容为中英混排的测试句，
  文本见同目录 `speech_zh_en_mixed.reference.txt`。不含任何真实用户语音或第三方版权素材。
- 采样率：16kHz，单声道。
- sha256: `e3f176a582cdff3ac30e3b1fb4dd4abc9b6737060533351589f51d9cfdd56b96`
- `speech_zh_en_mixed.reference.txt` 由 `scripts/dump_reference_fixtures.py` 用
  `client-server/server/` 的 Python 识别链路对本文件跑出的参考识别文本生成，
  Swift 侧 `EndToEndRecognitionTests` 据此比对。验收标准是**编辑距离 ≤ 2** 而非逐字相等：
  Python 与 macOS 用的是两套独立编译的 ONNX Runtime 二进制，个别模棱两可的 token 可能因
  浮点求和顺序不同而翻转（详见 `macos/DESIGN.md` §8 的实测结论）。

## fbank_input.f32 / fbank_reference.f32 / lfrcmvn_reference.f32 / fbank_parity_shapes.json

由 `scripts/dump_reference_fixtures.py` 中的 `dump_fbank_parity()` 生成：
固定随机种子（42）合成的 0.63s 正弦波 + 噪声信号（61 fbank 帧、11 个 LFR 帧），
经同一套 Python fbank/LFR+CMVN 实现算出的参考特征，供 `FbankParityTests` 逐帧比对
（阈值 1e-3）。时长特意选在 61 帧而不是整数倍：`applyLFR`（m=7, n=6）的最后一个 LFR 帧
（第 11 帧）原始帧不够 7 个，必须走尾帧补齐分支——61 帧以下的任何整数倍时长都测不到
这条分支（R4-09）。

全零静音输入触发的对数能量下限分支不在这份夹具里：`FbankParityTests.testAllZeroInputHitsDocumentedLogFloor`
直接在 Swift 里用一帧全零样本对比一个固定的浮点常量（Python 实测得出、等价于
`log(Float.ulpOfOne)`），不需要 Python 环境或模型即可运行。

## 重新生成

需要 `client-server/server/` 的 Python 环境（funasr-onnx / onnxruntime / modelscope，
已下载 `iic/SenseVoiceSmall-onnx` 模型）：

```bash
python3 macos/scripts/dump_reference_fixtures.py
```

`fbank_input.f32` / `fbank_reference.f32` / `lfrcmvn_reference.f32` / `fbank_parity_shapes.json`
会被脚本覆盖重写；`speech_zh_en_mixed.wav` 不会（脚本只读它），需要另行准备合成语音替换。

## text_format_cases.json

R8-F2「中英文之间统一加空格」的共享用例（`OutputFormatterTests` 逐条读取）。**不是**识别金标准：
它约束的是 `TextPostprocessor` 之后的 `OutputFormatter`，规则见 `macos/docs/UX_R8_SPEC.md` R8-F2。
Windows 侧同规则的 C# 实现应在 `VoiceTyper.Tests.csproj` 里 `Link` 同一份文件，两平台规则只能一起改。
既有用例的期望值不得删改；新增用例须同时在两个平台通过。

## vocabulary_cases.json

自定义词汇替换层（`VocabularyReplacer`，VOC-7）的共享用例（`VocabularyReplacerTests` 与 Windows
`VocabularyTests` 逐组读取）。每组一份词汇表与若干 `input → expected`；`formatted` 若给出，是
`OutputFormatter.formatForOutput` 的完整输出（替换之后再加中英文空格）。测试还会对每条用例与随机拼接
做幂等检查。规则见 `macos/docs/VOCABULARY_SPEC.md` §6。

## ctc_hotword_cases.json

带热词偏置的 CTC 前缀束搜索（`CTCPrefixBeamSearch`，VOC-5）的共享用例（`CTCPrefixBeamSearchTests` 与 Windows
`HotwordDecodingTests` 逐条读取）：合成小词表、逐帧 logits（未列出的 token 取 `floor`）、热词 token 序列与档位，
期望最优路径的 token 序列与热词命中次数。覆盖同音替换、声学门限、部分匹配不泄漏、重复 token 需隔 blank、
开头标签帧、跳帧、同分可复现与嵌套热词。规则见 `macos/docs/VOCABULARY_SPEC.md` §5。期望值由规格里的算法
推导，并用独立的 Python 参考实现逐条核对过。

以上两份与 `text_format_cases.json` 同一约定：规则变更须先改规格；既有用例的期望值不得删改；新增用例须同时在
两个平台通过。Windows 的 `VoiceTyper.Tests.csproj` 以 `Link` 引用同一份文件。
