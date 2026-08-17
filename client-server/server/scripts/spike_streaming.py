#!/usr/bin/env python3
"""
流式 ASR spike 脚本

验证目标：
1. funasr-onnx 的 paraformer_online_bin.Paraformer 可以加载 streaming 模型
2. 按 600ms chunk 切片喂入，收到增量文本
3. is_final=True 时正确 flush 尾音

用法：
    python3 spike_streaming.py [wav_file]

wav_file 如果不指定，会生成一段 4s 静音用于基本流程验证。
模型首次运行会自动下载到 ~/.cache/modelscope。

模型：damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-onnx
"""

import importlib
import sys
import time
import types
from pathlib import Path

import numpy as np

# -------------------------------------------------------------------
# 配置
# -------------------------------------------------------------------
STREAMING_MODEL = "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-onnx"
CHUNK_SIZE = [0, 10, 5]       # [left, current, right]，单位：60ms 帧
SAMPLE_RATE = 16000
CHUNK_SAMPLES = 9600          # 600ms @ 16kHz


def load_online_class():
    """绕过 funasr_onnx 包级 import 里的 torch 依赖，直接加载 online 模块。"""
    pkg = "funasr_onnx"
    if pkg not in sys.modules:
        spec = importlib.machinery.PathFinder.find_spec(pkg, sys.path)
        if spec is None or spec.submodule_search_locations is None:
            raise ImportError("未找到 funasr_onnx 包，请先 pip install funasr-onnx")
        package = types.ModuleType(pkg)
        package.__path__ = list(spec.submodule_search_locations)
        package.__spec__ = spec
        sys.modules[pkg] = package

    online_mod = importlib.import_module("funasr_onnx.paraformer_online_bin")
    return online_mod.Paraformer


def resolve_model(model_name: str) -> str:
    model_dir = Path(model_name).expanduser()
    if model_dir.exists():
        return str(model_dir)
    # Try modelscope cache path
    cache_path = Path.home() / ".cache/modelscope/hub/models" / model_name
    if cache_path.exists():
        return str(cache_path)
    # Download
    from modelscope.hub.snapshot_download import snapshot_download
    print(f"下载模型: {model_name}")
    return snapshot_download(model_name)


def load_wav(path: str) -> np.ndarray:
    import wave
    with wave.open(path, "rb") as f:
        assert f.getnchannels() == 1, "需要单声道"
        assert f.getframerate() == SAMPLE_RATE, f"需要 {SAMPLE_RATE}Hz"
        raw = f.readframes(f.getnframes())
    # wav 通常是 int16，转 float32
    audio = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    return audio


def run_spike(audio: np.ndarray, model):
    chunks = [
        audio[i: i + CHUNK_SAMPLES]
        for i in range(0, len(audio), CHUNK_SAMPLES)
    ]

    cache = {}
    fragments = []
    print(f"\n共 {len(chunks)} 个 chunk，每 chunk {CHUNK_SAMPLES} samples ({CHUNK_SAMPLES/SAMPLE_RATE*1000:.0f}ms)")
    print("-" * 50)

    t0 = time.time()
    for idx, chunk in enumerate(chunks):
        is_final = (idx == len(chunks) - 1)
        t_chunk = time.time()

        result = model(chunk, param_dict={"is_final": is_final, "cache": cache})

        elapsed_ms = (time.time() - t_chunk) * 1000
        fragment = ""
        if result:
            preds = result[0].get("preds", "")
            if isinstance(preds, (list, tuple)):
                fragment = preds[0] if preds else ""
            else:
                fragment = str(preds) if preds else ""
        if fragment:
            fragments.append(fragment)

        label = "[FINAL]" if is_final else f"[{idx:02d}]  "
        print(f"{label}  {elapsed_ms:5.1f}ms  → '{fragment}'")

    total = time.time() - t0
    full_text = "".join(fragments)
    print("-" * 50)
    print(f"完整输出: 「{full_text}」")
    print(f"总耗时:   {total*1000:.0f}ms  (音频时长 {len(audio)/SAMPLE_RATE:.1f}s)")
    return full_text


def main():
    # 加载模型
    print("加载 Paraformer Online (streaming) 类...")
    ParaformerOnline = load_online_class()

    model_dir = resolve_model(STREAMING_MODEL)
    # 检测量化
    quantize = not Path(model_dir, "model.onnx").exists()
    if quantize:
        print(f"使用量化模型 (model_quant.onnx)")

    print(f"初始化模型: {model_dir}")
    t0 = time.time()
    model = ParaformerOnline(
        model_dir=model_dir,
        batch_size=1,
        chunk_size=CHUNK_SIZE,
        device_id="-1",
        quantize=quantize,
        intra_op_num_threads=4,
    )
    print(f"模型加载完成，耗时 {time.time()-t0:.1f}s")

    # 加载音频
    if len(sys.argv) > 1:
        wav_path = sys.argv[1]
        print(f"\n读取音频: {wav_path}")
        audio = load_wav(wav_path)
    else:
        print("\n未指定音频文件，生成 4s 测试静音...")
        audio = np.zeros(SAMPLE_RATE * 4, dtype=np.float32)

    # --- spike 1: 正常流式喂入 ---
    print("\n=== Spike 1: 正常流式推理 ===")
    run_spike(audio, model)

    # --- spike 2: 短音频 (<600ms) ---
    print("\n=== Spike 2: 短音频（仅 300ms）===")
    short_audio = audio[:SAMPLE_RATE // 2]
    run_spike(short_audio, model)

    # --- spike 3: cache 跨会话隔离（两次调用用独立 cache）---
    print("\n=== Spike 3: 两次独立会话 cache 隔离 ===")
    for session_id in [1, 2]:
        cache = {}
        chunks = [audio[i: i + CHUNK_SAMPLES] for i in range(0, min(len(audio), CHUNK_SAMPLES * 3), CHUNK_SAMPLES)]
        for idx, chunk in enumerate(chunks):
            is_final = (idx == len(chunks) - 1)
            result = model(chunk, param_dict={"is_final": is_final, "cache": cache})
            frag = ""
            if result:
                preds = result[0].get("preds", "")
                frag = preds[0] if isinstance(preds, (list, tuple)) and preds else str(preds or "")
            print(f"  session={session_id} chunk={idx} is_final={is_final} → '{frag}'")

    print("\nSpike 完成 ✓")


if __name__ == "__main__":
    main()
