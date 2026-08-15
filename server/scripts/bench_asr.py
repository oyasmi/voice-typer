#!/usr/bin/env python3
"""离线识别方案对比基准：paraformer(+ct-punc) vs SenseVoice。

用法：
    # 用内置的 TTS 合成语料跑全部候选模型
    python scripts/bench_asr.py

    # 用自己录的音频（推荐——TTS 太干净，测不出真实口音/噪声下的差距）
    python scripts/bench_asr.py --audio-dir ~/my_recordings

    # 只跑部分候选
    python scripts/bench_asr.py --models sensevoice-small paraformer-zh

音频目录约定：每个 <name>.wav（16k 单声道）可以配一个同名 <name>.txt 作为参考文本；
有参考文本才会算 CER，否则只输出识别结果与耗时。
"""
import argparse
import json
import re
import subprocess
import sys
import time
import unicodedata
import wave
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from voice_typer_server.recognizer import (  # noqa: E402
    SenseVoiceRecognizer,
    SpeechRecognizer,
)

# 用 macOS say 合成的语料：贴近本项目的真实使用场景
# （中文口述编程指令，夹带英文术语、数字与标点）。
TTS_CORPUS = [
    ("num_limit",   "把一百和五十的两个限制都改成六十四兆。"),
    ("refactor",    "你来给出具体的修改方案，同时要注意确保独立出来的文件在应用启动时能够正确读取内容。"),
    ("issue",       "先放一放，我们先解决 GitHub 上那个 issue。"),
    ("swift",       "帮我把这个函数重构一下，用 Swift 的 async await 改写。"),
    ("addr",        "服务端默认监听一二七点零点零点一的六零零八端口。"),
    ("rtf",         "这个模型在 CPU 上的实时率大概是零点零一，比之前快了不少。"),
    ("websocket",   "麻烦你检查一下 WebSocket 连接断开之后的重连逻辑。"),
    ("punc",        "我们把标点模型去掉，直接用 SenseVoice 自带的标点。"),
    ("long",        "这次改动的目标是把服务端承担的识别功能合并到客户端，"
                    "从而消除前后端交互和一大批配置参数，既能加速上屏速度，"
                    "又能降低初次安装使用的配置复杂度。"),
]

# 参考文本：用户期望上屏的样子（带标点、数字用阿拉伯数字）
TTS_REFERENCE = {
    "num_limit": "把100和50的两个限制都改成64兆。",
    "refactor":  "你来给出具体的修改方案，同时要注意确保独立出来的文件在应用启动时能够正确读取内容。",
    "issue":     "先放一放，我们先解决GitHub上那个issue。",
    "swift":     "帮我把这个函数重构一下，用Swift的async await改写。",
    "addr":      "服务端默认监听127.0.0.1的6008端口。",
    "rtf":       "这个模型在CPU上的实时率大概是0.01，比之前快了不少。",
    "websocket": "麻烦你检查一下WebSocket连接断开之后的重连逻辑。",
    "punc":      "我们把标点模型去掉，直接用SenseVoice自带的标点。",
    "long":      "这次改动的目标是把服务端承担的识别功能合并到客户端，"
                 "从而消除前后端交互和一大批配置参数，既能加速上屏速度，"
                 "又能降低初次安装使用的配置复杂度。",
}

PUNCT_RE = re.compile(r"[\s，。！？、；：,.!?;:\"'“”‘’()（）]")


def synth_corpus(out_dir: Path, voice: str) -> None:
    """用 macOS say 生成 16k 单声道 wav。已存在则跳过。"""
    if sys.platform != "darwin":
        raise SystemExit("内置语料依赖 macOS 的 say 命令；请改用 --audio-dir 指定自己的录音")
    out_dir.mkdir(parents=True, exist_ok=True)
    for name, text in TTS_CORPUS:
        wav = out_dir / f"{name}.wav"
        if wav.exists():
            continue
        subprocess.run(
            ["say", "-v", voice, "-o", str(wav),
             "--data-format=LEI16@16000", "--file-format=WAVE", text],
            check=True,
        )
        (out_dir / f"{name}.txt").write_text(TTS_REFERENCE[name], encoding="utf-8")
        print(f"  合成 {wav.name}")


def read_wav16k(path: Path) -> np.ndarray:
    """读取 16k 单声道 wav 为 float32 [-1, 1]，与客户端上传格式一致。"""
    with wave.open(str(path), "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1:
            raise ValueError(f"{path.name}: 需要 16kHz 单声道，实际 "
                             f"{w.getframerate()}Hz/{w.getnchannels()}ch")
        if w.getsampwidth() != 2:
            raise ValueError(f"{path.name}: 需要 16bit PCM")
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
    return (pcm.astype(np.float32) / 32768.0).copy()


def normalize(text: str, keep_punct: bool) -> str:
    """全角转半角 + 统一大小写；keep_punct=False 时连标点空格一起去掉。"""
    text = unicodedata.normalize("NFKC", text).lower()
    return text if keep_punct else PUNCT_RE.sub("", text)


def cer(ref: str, hyp: str) -> float:
    """字符错误率（Levenshtein / len(ref)）。"""
    if not ref:
        return 0.0 if not hyp else 1.0
    prev = list(range(len(hyp) + 1))
    for i, r in enumerate(ref, 1):
        cur = [i]
        for j, h in enumerate(hyp, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (r != h)))
        prev = cur
    return prev[-1] / len(ref)


def build(name: str, threads: int):
    """按名字构造识别器。paraformer-zh 挂 ct-punc 才是当前线上的等价配置。"""
    if name.startswith("sensevoice"):
        return SenseVoiceRecognizer(model_name=name, intra_op_num_threads=threads)
    return SpeechRecognizer(model_name=name, punc_model="ct-punc",
                            intra_op_num_threads=threads)


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--audio-dir", type=Path, default=None,
                   help="16k 单声道 wav 目录（默认用内置 TTS 语料）")
    p.add_argument("--models", nargs="+",
                   default=["paraformer-zh", "sensevoice-small", "sensevoice-small-fp32"])
    p.add_argument("--repeat", type=int, default=3, help="每条音频重复推理次数，取中位数 (默认: %(default)s)")
    p.add_argument("--threads", type=int, default=4, help="ONNX intra-op 线程数 (默认: %(default)s)")
    p.add_argument("--voice", default="Tingting", help="say 的中文发音人 (默认: %(default)s)")
    p.add_argument("--json-out", type=Path, default=None, help="把明细结果写入 JSON")
    args = p.parse_args()

    audio_dir = args.audio_dir
    if audio_dir is None:
        audio_dir = Path(__file__).resolve().parent.parent / "build" / "bench_audio"
        print(f"生成 TTS 语料到 {audio_dir}")
        synth_corpus(audio_dir, args.voice)

    wavs = sorted(audio_dir.glob("*.wav"))
    if not wavs:
        raise SystemExit(f"{audio_dir} 下没有 wav 文件")

    clips = []
    for w in wavs:
        ref_file = w.with_suffix(".txt")
        clips.append({
            "name": w.stem,
            "audio": read_wav16k(w),
            "ref": ref_file.read_text(encoding="utf-8").strip() if ref_file.exists() else None,
        })
    total_sec = sum(len(c["audio"]) for c in clips) / 16000
    print(f"语料: {len(clips)} 条，合计 {total_sec:.1f}s\n")

    results = {}
    for model_name in args.models:
        print(f"===== {model_name} =====")
        t0 = time.time()
        rec = build(model_name, args.threads)
        rec.initialize()
        load_sec = time.time() - t0
        print(f"加载: {load_sec:.1f}s")

        rows = []
        for clip in clips:
            # 第一次推理含 ORT 的懒初始化开销，先热身再计时
            rec.recognize(clip["audio"])
            timings = []
            for _ in range(args.repeat):
                t = time.time()
                text = rec.recognize(clip["audio"])
                timings.append(time.time() - t)
            elapsed = float(np.median(timings))
            dur = len(clip["audio"]) / 16000

            row = {"name": clip["name"], "text": text, "elapsed": elapsed,
                   "duration": dur, "rtf": elapsed / dur}
            if clip["ref"]:
                row["cer_punct"] = cer(normalize(clip["ref"], True), normalize(text, True))
                row["cer_plain"] = cer(normalize(clip["ref"], False), normalize(text, False))
            rows.append(row)

            flag = ""
            if clip["ref"]:
                flag = f"  CER(含标点)={row['cer_punct']:.1%} CER(纯字)={row['cer_plain']:.1%}"
            print(f"  [{clip['name']:<10}] {elapsed*1000:6.0f}ms rtf={row['rtf']:.3f}{flag}")
            print(f"      {text}")

        results[model_name] = {"load_sec": load_sec, "rows": rows}
        print()

    # -- 汇总 --------------------------------------------------------------
    print("=" * 78)
    print(f"{'模型':<26}{'加载':>8}{'总耗时':>10}{'平均RTF':>10}{'CER含标点':>12}{'CER纯字':>10}")
    print("-" * 78)
    for name, r in results.items():
        rows = r["rows"]
        tot = sum(x["elapsed"] for x in rows)
        rtf = tot / total_sec
        scored = [x for x in rows if "cer_punct" in x]
        cp = f"{np.mean([x['cer_punct'] for x in scored]):.1%}" if scored else "-"
        cl = f"{np.mean([x['cer_plain'] for x in scored]):.1%}" if scored else "-"
        print(f"{name:<26}{r['load_sec']:>7.1f}s{tot:>9.2f}s{rtf:>10.3f}{cp:>12}{cl:>10}")
    print("=" * 78)

    if args.json_out:
        args.json_out.write_text(json.dumps(results, ensure_ascii=False, indent=2),
                                 encoding="utf-8")
        print(f"明细已写入 {args.json_out}")


if __name__ == "__main__":
    main()
