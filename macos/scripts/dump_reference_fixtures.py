#!/usr/bin/env python3
"""生成 macos/Tests/VoiceTyperTests/Fixtures 下的金标准测试数据。

用 client-server/server/ 的实际 Python 识别链路（WavFrontend + SenseVoiceRecognizer）在同一份
输入音频上跑出参考结果，Swift 侧测试据此比对：
- FbankParityTests: fbank / LFR+CMVN 特征逐帧比对（阈值 1e-3）
- EndToEndRecognitionTests: 完整识别文本逐字比对

依赖 client-server/server/ 的 Python 环境（funasr-onnx / onnxruntime / modelscope，已下载
iic/SenseVoiceSmall-onnx 模型）。用法：

    python3 macos/scripts/dump_reference_fixtures.py
"""
import json
import sys
from pathlib import Path

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "client-server" / "server"))

FIXTURES_DIR = Path(__file__).resolve().parent.parent / "Tests" / "VoiceTyperTests" / "Fixtures"


def dump_fbank_parity():
    from voice_typer_server.recognizer import _bypass_load

    rng = np.random.default_rng(42)
    t = np.arange(int(0.62 * 16000)) / 16000
    sig = (
        0.3 * np.sin(2 * np.pi * 220 * t)
        + 0.15 * np.sin(2 * np.pi * 880 * t)
        + 0.02 * rng.standard_normal(len(t))
    ).astype(np.float32)
    sig.tofile(FIXTURES_DIR / "fbank_input.f32")

    utils_mod = _bypass_load("funasr_onnx.utils.utils")
    frontend_mod = _bypass_load("funasr_onnx.utils.frontend")

    model_dir = Path.home() / ".cache/modelscope/hub/models/iic/SenseVoiceSmall-onnx"
    config = utils_mod.read_yaml(str(model_dir / "config.yaml"))
    frontend_conf = dict(config["frontend_conf"])
    frontend_conf["cmvn_file"] = str(model_dir / "am.mvn")
    frontend_conf["dither"] = 0.0
    frontend = frontend_mod.WavFrontend(**frontend_conf)

    feat, _ = frontend.fbank(sig)
    feat.astype(np.float32).tofile(FIXTURES_DIR / "fbank_reference.f32")

    feat2, _ = frontend.lfr_cmvn(feat)
    feat2.astype(np.float32).tofile(FIXTURES_DIR / "lfrcmvn_reference.f32")

    shapes = {
        "n_samples": len(sig),
        "fbank_frames": int(feat.shape[0]),
        "fbank_dim": int(feat.shape[1]),
        "lfr_frames": int(feat2.shape[0]),
        "lfr_dim": int(feat2.shape[1]),
    }
    (FIXTURES_DIR / "fbank_parity_shapes.json").write_text(json.dumps(shapes, indent=2))
    print("fbank parity fixtures:", shapes)


def dump_end_to_end_reference():
    from voice_typer_server.recognizer import SenseVoiceRecognizer

    wav_path = FIXTURES_DIR / "speech_zh_en_mixed.wav"
    sig, samplerate = sf.read(str(wav_path), dtype="float32")
    assert samplerate == 16000, f"期望 16kHz，实际 {samplerate}"

    recognizer = SenseVoiceRecognizer(intra_op_num_threads=4)
    recognizer.initialize()
    text = recognizer.recognize(sig)
    (FIXTURES_DIR / "speech_zh_en_mixed.reference.txt").write_text(text, encoding="utf-8")
    print("end-to-end reference text:", repr(text))


if __name__ == "__main__":
    FIXTURES_DIR.mkdir(parents=True, exist_ok=True)
    dump_fbank_parity()
    dump_end_to_end_reference()
