# Fixtures

本目录下的文件用于 `FbankParityTests` 与 `EndToEndRecognitionTests` 的金标准比对。

## speech_zh_en_mixed.wav

- 来源：合成语音（TTS），非真实用户录音，内容为中英混排的测试句，
  文本见同目录 `speech_zh_en_mixed.reference.txt`。不含任何真实用户语音或第三方版权素材。
- 采样率：16kHz，单声道。
- sha256: `e3f176a582cdff3ac30e3b1fb4dd4abc9b6737060533351589f51d9cfdd56b96`
- `speech_zh_en_mixed.reference.txt` 由 `scripts/dump_reference_fixtures.py` 用
  `client-server/server/` 的 Python 识别链路对本文件跑出的参考识别文本生成，
  Swift 侧 `EndToEndRecognitionTests` 据此逐字比对。

## fbank_input.f32 / fbank_reference.f32 / lfrcmvn_reference.f32 / fbank_parity_shapes.json

由 `scripts/dump_reference_fixtures.py` 中的 `dump_fbank_parity()` 生成：
固定随机种子（42）合成的正弦波 + 噪声信号，经同一套 Python fbank/LFR+CMVN 实现算出的参考特征，
供 `FbankParityTests` 逐帧比对（阈值 1e-3）。

## 重新生成

需要 `client-server/server/` 的 Python 环境（funasr-onnx / onnxruntime / modelscope，
已下载 `iic/SenseVoiceSmall-onnx` 模型）：

```bash
python3 macos/scripts/dump_reference_fixtures.py
```

`fbank_input.f32` / `fbank_reference.f32` / `lfrcmvn_reference.f32` / `fbank_parity_shapes.json`
会被脚本覆盖重写；`speech_zh_en_mixed.wav` 不会（脚本只读它），需要另行准备合成语音替换。
