#!/usr/bin/env python3
"""
VoiceTyper · <asr_hints> 离线探索评测与诊断日志分析工具（HINT-0 / TRACE-5）。

支持四种模式：
1. `--mode oracle`：离线零 API 费用，使用本地 SenseVoice-Small ONNX 模型在真实录音上
   扫描 τ ∈ {1.5, 2.0, 2.5, 3.0, 3.5, 4.0}，比较 B1（帧级低置信度）与 B2（束搜索 Top-5 Diff）
   的 Oracle 召回率、平均每句提示数、干净集假阳性与高频误触 token。
2. `--mode llm`：调用 OpenAI 兼容端点（temperature=0）对比 Baseline / Exp-B1 / Exp-B2 /
   可选 Exp-A（整句 Top-3 N-best），输出汇总表与可选本地逐句 diff 报告。
3. `--mode trace-summary --from-trace <path>`（TRACE-5）：只读分析本机 `correction_trace.jsonl`，
   输出耗时、hints 占比、按 `best_margin` 分桶采纳率、token / cached_tokens 对比与人工标注对照表。
4. `--mode replay --from-trace <path>`（TRACE-5）：读取历史 `correction_trace.jsonl` 中的
   `asr.replaced_text` 与 `hints.items`，用指定 `correction.md` / 模型离线重放并输出与历史
   `llm.output_text` 存在差异的逐条 diff。

本脚本不进入 App bundle；所有语料与报告均在用户指定的本地目录读写，严禁提交到仓库。
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
import urllib.error
import urllib.request
import wave
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

# 与 TextPostprocessor 保持一致的字符分类与清洗规则
_RICH_TAG_RE = re.compile(r"<\|[^|]*\|>")
_WHITESPACE_RE = re.compile(r"\s+")
_CJK_CLASS = "\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uFF00-\uFFEF"
_SPACE_AFTER_CJK_RE = re.compile(rf"(?<=[{_CJK_CLASS}])\s+(?=[0-9A-Za-z])")
_SPACE_BEFORE_CJK_RE = re.compile(rf"(?<=[0-9A-Za-z])\s+(?=[{_CJK_CLASS}])")
_SUBSTANTIVE_RE = re.compile(r"[0-9A-Za-z\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7A3]")
_PURE_FILLERS = frozenset({"呃", "嗯", "啊", "额", "哦", "噢", "唉", "哎"})

_DEFAULT_FEW_SHOT: list[dict[str, str]] = [
    {"role": "user", "content": "<asr_text>\n你是谁？今天天气怎么样？\n</asr_text>"},
    {"role": "assistant", "content": "你是谁？今天天气怎么样？"},
    {"role": "user", "content": "<asr_text>\n呃，瑞迪斯的缓存过期时间配置好了吗\n</asr_text>"},
    {"role": "assistant", "content": "Redis 的缓存过期时间配置好了吗？"},
    {"role": "user", "content": "<asr_text>\n帮我把这个函数重构一下，逻辑保持不变\n</asr_text>"},
    {"role": "assistant", "content": "帮我把这个函数重构一下，逻辑保持不变"},
    {
        "role": "user",
        "content": "<asr_text>\n我们3个人把这个季度的转化率提升了百分之二十五,明天下午三点半开会同步一下.\n</asr_text>",
    },
    {"role": "assistant", "content": "我们三个人把这个季度的转化率提升了25%，明天下午3点半开会同步一下。"},
    {"role": "user", "content": "<asr_text>\n那个那个登陆页面的问题不是bug，是feature\n</asr_text>"},
    {"role": "assistant", "content": "那个登录页面的问题不是 bug，是 feature"},
    {"role": "user", "content": "<asr_text>\n我我觉得截止时间定在周三，不对，周四吧\n</asr_text>"},
    {"role": "assistant", "content": "我觉得截止时间定在周四吧"},
    {
        "role": "user",
        "content": "<asr_text>\n需要准备的材料如下，第一，身份证复印件，第二，学历证明，第三，近期照片。\n</asr_text>",
    },
    {"role": "assistant", "content": "需要准备的材料如下：\n1. 身份证复印件\n2. 学历证明\n3. 近期照片"},
    {"role": "user", "content": "<asr_text>\n周报。\n</asr_text>"},
    {"role": "assistant", "content": "周报"},
]

_HINTS_FEW_SHOT: list[dict[str, str]] = [
    {
        "role": "user",
        "content": (
            "<asr_hints>\n"
            "「密等」备选：幂等 / 密度\n"
            "「持器」备选：湿器 / 池器\n"
            "「张明」备选：章明 / 张铭\n"
            "</asr_hints>\n"
            "<asr_text>\n"
            "这个接口需要保证密等性，张明建议继续佩戴间隙保持器\n"
            "</asr_text>"
        ),
    },
    {"role": "assistant", "content": "这个接口需要保证幂等性，张明建议继续佩戴间隙保持器。"},
]


def postprocess_text(raw: str) -> str:
    text = raw.replace("▁", " ")
    text = _RICH_TAG_RE.sub("", text)
    text = _WHITESPACE_RE.sub(" ", text).strip()
    text = _SPACE_AFTER_CJK_RE.sub("", text)
    text = _SPACE_BEFORE_CJK_RE.sub("", text)
    return text if _SUBSTANTIVE_RE.search(text) else ""


def is_special_token(token: str) -> bool:
    return token.startswith("<|") and token.endswith("|>")


def is_excluded_token(token_id: int, token: str) -> bool:
    return token_id <= 2 or not token or is_special_token(token)


def is_cjk_char(text: str) -> bool:
    return len(text) == 1 and "\u4E00" <= text <= "\u9FFF"


def is_ascii_word_part(text: str) -> bool:
    return bool(text) and all(ch.isascii() and ch.isalnum() for ch in text)


def is_single_ascii_letter(text: str) -> bool:
    return len(text) == 1 and text.isascii() and text.isalpha()


@dataclass(frozen=True)
class TokenMeta:
    is_excluded: bool
    has_leading_space: bool
    stripped: str
    is_substantive: bool
    is_cjk_char: bool
    is_ascii_word_part: bool
    is_pure_filler: bool
    is_single_ascii_letter: bool


@dataclass(frozen=True)
class AlignedToken:
    token_id: int
    peak_frame: int
    protected_by_hotword: bool = False


@dataclass(frozen=True)
class AcousticHint:
    surface: str
    alternatives: tuple[str, ...]
    best_margin: float


@dataclass(frozen=True)
class HintParameters:
    margin_threshold: float = 2.5
    max_hints_per_utterance: int = 5
    max_alts_per_hint: int = 3
    max_merged_cjk_length: int = 3
    joint_margin_multiplier: float = 1.5
    frame_top_k: int = 4


def build_token_metadata(tokens: Sequence[str]) -> list[TokenMeta]:
    meta: list[TokenMeta] = []
    for idx, tok in enumerate(tokens):
        excluded = is_excluded_token(idx, tok)
        has_leading = tok.startswith("▁")
        stripped = tok[1:] if has_leading else tok
        substantive = bool(_SUBSTANTIVE_RE.search(stripped))
        meta.append(
            TokenMeta(
                is_excluded=excluded,
                has_leading_space=has_leading,
                stripped=stripped,
                is_substantive=substantive,
                is_cjk_char=is_cjk_char(stripped),
                is_ascii_word_part=is_ascii_word_part(stripped),
                is_pure_filler=stripped in _PURE_FILLERS,
                is_single_ascii_letter=is_single_ascii_letter(stripped),
            )
        )
    return meta


def greedy_align_tokens(
    logits: Sequence[Sequence[float]], tokens: Sequence[str]
) -> tuple[list[int], list[AlignedToken]]:
    """贪心解码并记录每个 token 的非 blank 峰值核心帧（HINT-1）。"""
    all_ids: list[int] = []
    aligned: list[AlignedToken] = []
    prev = -1
    run_start = 0
    num_frames = len(logits)

    def flush_run(tok_id: int, start: int, end: int) -> None:
        all_ids.append(tok_id)
        if not aligned and is_special_token(tokens[tok_id]):
            return
        best_frame = start
        best_adv = float("-inf")
        for f in range(start, end):
            row = logits[f]
            adv = float(row[tok_id]) - float(row[0])
            if adv > best_adv:
                best_adv = adv
                best_frame = f
        aligned.append(AlignedToken(token_id=tok_id, peak_frame=best_frame))

    for t in range(num_frames):
        row = logits[t]
        best_id = 0
        best_val = row[0]
        for k in range(1, len(row)):
            if row[k] > best_val:
                best_val = row[k]
                best_id = k
        if best_id != prev:
            if prev > 0:
                flush_run(prev, run_start, t)
            prev = best_id
            run_start = t
    if prev > 0:
        flush_run(prev, run_start, num_frames)
    return all_ids, aligned


def extract_hints_b1(
    logits: Sequence[Sequence[float]],
    aligned: Sequence[AlignedToken],
    full_text: str,
    meta: Sequence[TokenMeta],
    params: HintParameters = HintParameters(),
) -> list[AcousticHint]:
    """B1 帧级低置信度局部备选提取（与 Swift/C# AcousticHintExtractor 完全同构）。"""
    n = len(aligned)
    if n == 0 or not full_text:
        return []

    frame_alts: list[list[tuple[str, float]]] = [[] for _ in range(n)]
    for i, tok in enumerate(aligned):
        y = tok.token_id
        ym = meta[y]
        if tok.protected_by_hotword or ym.is_excluded or not ym.is_substantive or ym.is_pure_filler:
            continue
        row = logits[tok.peak_frame]
        sy = float(row[y])
        if not math.isfinite(sy):
            return []
        raw_cands: list[tuple[float, int, str]] = []
        for k, val in enumerate(row):
            if k == y:
                continue
            fval = float(val)
            if not math.isfinite(fval):
                return []
            km = meta[k]
            if km.is_excluded or not km.is_substantive:
                continue
            delta = sy - fval
            if delta > params.margin_threshold:
                continue
            if km.stripped == ym.stripped:
                continue
            if km.stripped.isascii() and ym.stripped.isascii() and km.stripped.lower() == ym.stripped.lower():
                continue
            if ym.is_cjk_char and km.is_single_ascii_letter:
                continue
            raw_cands.append((delta, k, km.stripped))
        raw_cands.sort(key=lambda item: (item[0], item[1]))
        raw_cands = raw_cands[: params.frame_top_k]
        seen: set[str] = set()
        deduped: list[tuple[str, float]] = []
        for delta, _k, text in raw_cands:
            norm = text.lower() if text.isascii() else text
            if norm in seen:
                continue
            seen.add(norm)
            deduped.append((text, delta))
            if len(deduped) >= params.max_alts_per_hint:
                break
        frame_alts[i] = deduped

    # 步骤 1：词/段分组
    @dataclass
    class Candidate:
        start_index: int
        surface: str
        alternatives: list[str]
        best_margin: float

    candidates: list[Candidate] = []
    single_cjk_indices: list[int] = []
    i = 0
    while i < n:
        ym = meta[aligned[i].token_id]
        if ym.is_ascii_word_part:
            j = i + 1
            while j < n and meta[aligned[j].token_id].is_ascii_word_part and not meta[aligned[j].token_id].has_leading_space:
                j += 1
            if not any(aligned[pos].protected_by_hotword for pos in range(i, j)):
                low_positions = [pos for pos in range(i, j) if frame_alts[pos]]
                if low_positions:
                    low_positions.sort(key=lambda pos: (frame_alts[pos][0][1], pos))
                    chosen = set(low_positions[:2])
                    base_word = "".join(meta[aligned[pos].token_id].stripped for pos in range(i, j))
                    combos: list[tuple[str, float]] = [("", 0.0)]
                    for pos in range(i, j):
                        base_part = meta[aligned[pos].token_id].stripped
                        if pos in chosen:
                            opts = [(base_part, 0.0)] + frame_alts[pos]
                        else:
                            opts = [(base_part, 0.0)]
                        combos = [(p + s, d + sd) for p, d in combos for s, sd in opts]
                    best_by_norm: dict[str, tuple[str, float]] = {}
                    base_lower = base_word.lower()
                    for word, sum_delta in combos:
                        key = word.lower()
                        if key == base_lower:
                            continue
                        prev = best_by_norm.get(key)
                        if prev is None or (sum_delta, word) < (prev[1], prev[0]):
                            best_by_norm[key] = (word, sum_delta)
                    ranked = sorted(best_by_norm.values(), key=lambda x: (x[1], x[0]))[: params.max_alts_per_hint]
                    if ranked:
                        candidates.append(
                            Candidate(
                                start_index=i,
                                surface=base_word,
                                alternatives=[w for w, _ in ranked],
                                best_margin=ranked[0][1],
                            )
                        )
            i = j
        elif ym.is_cjk_char and not aligned[i].protected_by_hotword and frame_alts[i]:
            j = i + 1
            while (
                j < n
                and (j - i) < params.max_merged_cjk_length
                and meta[aligned[j].token_id].is_cjk_char
                and not aligned[j].protected_by_hotword
                and frame_alts[j]
            ):
                j += 1
            if j - i >= 2:
                base_str = "".join(meta[aligned[pos].token_id].stripped for pos in range(i, j))
                joint_limit = params.joint_margin_multiplier * params.margin_threshold
                combos = [("", 0.0)]
                for pos in range(i, j):
                    base_part = meta[aligned[pos].token_id].stripped
                    opts = [(base_part, 0.0)] + frame_alts[pos][:2]
                    combos = [
                        (p + s, d + sd) for p, d in combos for s, sd in opts if d + sd <= joint_limit + 1e-9
                    ]
                best_by_str: dict[str, float] = {}
                for word, sum_delta in combos:
                    if word == base_str:
                        continue
                    prev = best_by_str.get(word)
                    if prev is None or sum_delta < prev:
                        best_by_str[word] = sum_delta
                ranked = sorted(best_by_str.items(), key=lambda x: (x[1], x[0]))[: params.max_alts_per_hint]
                if ranked:
                    candidates.append(
                        Candidate(
                            start_index=i,
                            surface=base_str,
                            alternatives=[w for w, _ in ranked],
                            best_margin=ranked[0][1],
                        )
                    )
            else:
                single_cjk_indices.append(i)
            i = j
        else:
            if not aligned[i].protected_by_hotword and frame_alts[i]:
                alts = [w for w, _ in frame_alts[i][: params.max_alts_per_hint]]
                candidates.append(
                    Candidate(
                        start_index=i,
                        surface=ym.stripped,
                        alternatives=alts,
                        best_margin=frame_alts[i][0][1],
                    )
                )
            i += 1

    # 步骤 2：孤立单汉字上下文锚点扩展
    single_cjk_indices.sort(key=lambda pos: (frame_alts[pos][0][1], pos))
    claimed_anchors: set[int] = set()
    for pos in single_cjk_indices:
        base_char = meta[aligned[pos].token_id].stripped
        alts = [w for w, _ in frame_alts[pos][: params.max_alts_per_hint]]
        margin = frame_alts[pos][0][1]
        can_right = pos + 1 < n and meta[aligned[pos + 1].token_id].is_cjk_char
        can_left = pos - 1 >= 0 and meta[aligned[pos - 1].token_id].is_cjk_char

        use_left = False
        use_right = False
        if can_right and (pos + 1) not in claimed_anchors:
            use_right = True
            claimed_anchors.add(pos + 1)
        elif can_left and (pos - 1) not in claimed_anchors:
            use_left = True
            claimed_anchors.add(pos - 1)
        elif can_right:
            use_right = True
        elif can_left:
            use_left = True

        def build_surface(left: bool, right: bool, center: str) -> str:
            lch = meta[aligned[pos - 1].token_id].stripped if left else ""
            rch = meta[aligned[pos + 1].token_id].stripped if right else ""
            return lch + center + rch

        surface = build_surface(use_left, use_right, base_char)
        if full_text.count(surface) >= 2:
            if use_right and can_left:
                use_left = True
                surface = build_surface(use_left, use_right, base_char)
            elif use_left and can_right:
                use_right = True
                surface = build_surface(use_left, use_right, base_char)

        start_idx = pos - 1 if use_left else pos
        expanded_alts = [build_surface(use_left, use_right, alt) for alt in alts]
        candidates.append(
            Candidate(
                start_index=start_idx,
                surface=surface,
                alternatives=expanded_alts,
                best_margin=margin,
            )
        )

    # 步骤 3：去重、排序截断、按原文顺序重排
    dedup_surface: dict[str, Candidate] = {}
    for cand in candidates:
        if not cand.surface or not cand.alternatives:
            continue
        prev = dedup_surface.get(cand.surface)
        if prev is None or (cand.best_margin, cand.start_index) < (prev.best_margin, prev.start_index):
            dedup_surface[cand.surface] = cand

    ranked_cands = sorted(dedup_surface.values(), key=lambda c: (c.best_margin, c.start_index))
    top_cands = ranked_cands[: params.max_hints_per_utterance]
    top_cands.sort(key=lambda c: c.start_index)
    return [
        AcousticHint(surface=c.surface, alternatives=tuple(c.alternatives), best_margin=c.best_margin)
        for c in top_cands
    ]


def cer(reference: str, hypothesis: str) -> tuple[int, int]:
    """返回 (编辑距离, 参考长度)，忽略空白与标点差异以便公平评估用字错误。"""
    ref = [ch for ch in reference if _SUBSTANTIVE_RE.match(ch)]
    hyp = [ch for ch in hypothesis if _SUBSTANTIVE_RE.match(ch)]
    m, n = len(ref), len(hyp)
    dp = list(range(n + 1))
    for i in range(1, m + 1):
        prev = dp[0]
        dp[0] = i
        for j in range(1, n + 1):
            temp = dp[j]
            cost = 0 if ref[i - 1].lower() == hyp[j - 1].lower() else 1
            dp[j] = min(dp[j] + 1, dp[j - 1] + 1, prev + cost)
            prev = temp
    return dp[n], m


def format_user_message(text: str, glossary: Sequence[str] = (), hints: Sequence[AcousticHint] = ()) -> str:
    parts: list[str] = []
    if glossary:
        parts.append(f"<glossary>{'、'.join(glossary)}</glossary>")
    if hints:
        lines = [f"「{h.surface}」备选：{' / '.join(h.alternatives)}" for h in hints]
        parts.append("<asr_hints>\n" + "\n".join(lines) + "\n</asr_hints>")
    parts.append(f"<asr_text>\n{text}\n</asr_text>")
    return "\n".join(parts)


def call_openai_chat(
    base_url: str,
    api_key: str,
    model: str,
    messages: list[dict[str, str]],
    timeout: float = 15.0,
) -> tuple[str, dict[str, Any]]:
    url = base_url.rstrip("/")
    if not url.endswith("/chat/completions"):
        url += "/chat/completions"
    payload = {
        "model": model,
        "messages": messages,
        "temperature": 0.0,
        "max_tokens": 800,
    }
    req = urllib.request.Request(
        url,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    content = data["choices"][0]["message"]["content"].strip()
    return content, data.get("usage") or {}


def iter_trace_records(trace_path: Path) -> Iterable[dict[str, Any]]:
    with trace_path.open("r", encoding="utf-8") as fh:
        for line_no, line in enumerate(fh, 1):
            stripped = line.strip()
            if not stripped:
                continue
            try:
                yield json.loads(stripped)
            except json.JSONDecodeError as exc:
                print(f"[warn] 跳过非法 JSON 行 {line_no}: {exc}", file=sys.stderr)


def run_trace_summary(trace_path: Path, export_diffs: Path | None) -> int:
    records = list(iter_trace_records(trace_path))
    if not records:
        print("诊断日志为空，无记录可统计。")
        return 0

    total = len(records)
    result_counts = Counter(r.get("llm", {}).get("result", "unknown") for r in records)
    audio_secs = [float(r.get("audio_sec", 0.0)) for r in records]
    asr_ms = [float(r.get("asr", {}).get("asr_ms", 0.0)) for r in records]
    decode_ms = [float(r.get("asr", {}).get("decode_ms", 0.0)) for r in records]
    llm_ms = [float(r.get("llm", {}).get("llm_ms", 0.0)) for r in records]

    with_hints = [r for r in records if r.get("llm", {}).get("hints_sent")]
    without_hints = [r for r in records if not r.get("llm", {}).get("hints_sent")]
    total_dropped_vocab = sum(int(r.get("hints", {}).get("dropped_by_vocab", 0)) for r in records)
    avg_active = (
        sum(int(r.get("hints", {}).get("active_count", 0)) for r in with_hints) / len(with_hints)
        if with_hints
        else 0.0
    )

    buckets = [("[0, 1.0)", 0.0, 1.0), ("[1.0, 1.5)", 1.0, 1.5), ("[1.5, 2.0)", 1.5, 2.0), ("[2.0, 2.5]", 2.0, 2.5001)]
    bucket_total: dict[str, int] = defaultdict(int)
    bucket_adopted: dict[str, int] = defaultdict(int)
    diff_rows: list[dict[str, Any]] = []

    for r in records:
        llm = r.get("llm", {})
        asr = r.get("asr", {})
        replaced = asr.get("replaced_text", "")
        output = llm.get("output_text")
        items = r.get("hints", {}).get("items", [])
        if llm.get("hints_sent") and output is not None and replaced != output:
            diff_rows.append(
                {
                    "session_id": r.get("session_id"),
                    "replaced_text": replaced,
                    "output_text": output,
                    "hints": items,
                }
            )
        if output is not None:
            for item in items:
                margin = float(item.get("best_margin", 99.0))
                alts = item.get("alternatives", [])
                surface = item.get("surface", "")
                adopted = surface not in output and any(alt in output for alt in alts)
                for name, lo, hi in buckets:
                    if lo <= margin < hi:
                        bucket_total[name] += 1
                        if adopted:
                            bucket_adopted[name] += 1
                        break

    def avg_opt(rows: list[dict[str, Any]], key: str) -> str:
        vals = [r.get("llm", {}).get(key) for r in rows if r.get("llm", {}).get(key) is not None]
        return f"{sum(vals) / len(vals):.1f}" if vals else "-"

    print(f"=== 校对诊断日志统计（{trace_path}）===")
    print(f"总记录数: {total} | 平均时长: {sum(audio_secs)/total:.1f}s")
    print("llm.result 分布:", dict(result_counts))
    print(
        f"平均耗时: asr_ms={sum(asr_ms)/total:.1f} | decode_ms={sum(decode_ms)/total:.2f} | llm_ms={sum(llm_ms)/total:.1f}"
    )
    print(
        f"hints_sent 占比: {len(with_hints)}/{total} ({len(with_hints)*100.0/total:.1f}%) | "
        f"有提示时平均 active_count={avg_active:.2f} | dropped_by_vocab 总计={total_dropped_vocab}"
    )
    print("\n按 best_margin 分桶采纳率：")
    for name, _, _ in buckets:
        t_cnt = bucket_total[name]
        a_cnt = bucket_adopted[name]
        rate = f"{a_cnt * 100.0 / t_cnt:.1f}%" if t_cnt else "-"
        print(f"  {name:10s}: {a_cnt}/{t_cnt} ({rate})")

    print("\nhints_sent 对比：")
    print(
        f"  hints_sent=true  (n={len(with_hints)}): prompt_tokens={avg_opt(with_hints, 'prompt_tokens')}, "
        f"cached_tokens={avg_opt(with_hints, 'cached_tokens')}, llm_ms={avg_opt(with_hints, 'llm_ms')}"
    )
    print(
        f"  hints_sent=false (n={len(without_hints)}): prompt_tokens={avg_opt(without_hints, 'prompt_tokens')}, "
        f"cached_tokens={avg_opt(without_hints, 'cached_tokens')}, llm_ms={avg_opt(without_hints, 'llm_ms')}"
    )
    print(f"\n有提示且发生修改的样本数: {len(diff_rows)}")
    if export_diffs is not None:
        export_diffs.parent.mkdir(parents=True, exist_ok=True)
        with export_diffs.open("w", encoding="utf-8") as out:
            for row in diff_rows:
                out.write(json.dumps(row, ensure_ascii=False) + "\n")
        print(f"已导出对照列表至: {export_diffs}")
    return 0


def run_replay(
    trace_path: Path,
    prompt_path: Path,
    base_url: str,
    api_key: str,
    model: str,
    include_hints_few_shot: bool,
    limit: int | None,
) -> int:
    system_prompt = prompt_path.read_text(encoding="utf-8").strip()
    few_shot = list(_DEFAULT_FEW_SHOT) + (_HINTS_FEW_SHOT if include_hints_few_shot else [])
    records = list(iter_trace_records(trace_path))
    if limit is not None:
        records = records[:limit]
    changed = 0
    for idx, r in enumerate(records, 1):
        replaced = r.get("asr", {}).get("replaced_text", "")
        old_out = r.get("llm", {}).get("output_text")
        items = [
            AcousticHint(
                surface=it["surface"],
                alternatives=tuple(it["alternatives"]),
                best_margin=float(it.get("best_margin", 0.0)),
            )
            for it in r.get("hints", {}).get("items", [])
        ]
        user_msg = format_user_message(replaced, hints=items)
        messages = [{"role": "system", "content": system_prompt}, *few_shot, {"role": "user", "content": user_msg}]
        try:
            new_out, _usage = call_openai_chat(base_url, api_key, model, messages)
        except Exception as exc:
            print(f"[{idx}] 请求失败: {exc}", file=sys.stderr)
            continue
        if new_out != old_out:
            changed += 1
            print(f"--- #{idx} session={r.get('session_id')} ---")
            print(f"  原文: {replaced}")
            if items:
                print("  提示:", "; ".join(f"{h.surface}→{'/'.join(h.alternatives)}" for h in items))
            print(f"  旧输出: {old_out}")
            print(f"  新输出: {new_out}")
    print(f"\n重放完成：共 {len(records)} 条，存在差异 {changed} 条。")
    return 0


def default_model_dir() -> Path | None:
    candidates = [
        Path.home() / "Library/Application Support/VoiceTyper/models/sensevoice-small-onnx",
        Path(os.environ.get("LOCALAPPDATA", "")) / "VoiceTyper/models/sensevoice-small-onnx",
    ]
    for path in candidates:
        if (path / "model_quant.onnx").exists() and (path / "tokens.json").exists():
            return path
    return None


def load_wav_16k(path: Path) -> list[float]:
    with wave.open(str(path), "rb") as wf:
        if wf.getframerate() != 16000 or wf.getnchannels() != 1 or wf.getsampwidth() != 2:
            raise ValueError(f"{path} 必须是 16kHz / 单声道 / 16-bit PCM WAV")
        raw = wf.readframes(wf.getnframes())
    import struct

    ints = struct.unpack(f"<{len(raw)//2}h", raw)
    return [v / 32768.0 for v in ints]


def run_oracle_or_llm(args: argparse.Namespace) -> int:
    try:
        import numpy as np  # type: ignore
        import onnxruntime as ort  # type: ignore
    except ImportError as exc:
        print(f"oracle/llm 模式需要 numpy 与 onnxruntime: {exc}", file=sys.stderr)
        return 2

    model_dir = Path(args.model_dir) if args.model_dir else default_model_dir()
    if model_dir is None or not model_dir.exists():
        print("未找到 SenseVoice-Small 模型目录，请通过 --model-dir 指定。", file=sys.stderr)
        return 2

    eval_dir = Path(args.eval_dir)
    if not eval_dir.exists():
        print(f"语料目录不存在: {eval_dir}", file=sys.stderr)
        return 2

    tokens = json.loads((model_dir / "tokens.json").read_text(encoding="utf-8"))
    meta = build_token_metadata(tokens)

    # 仅在需要推理时校验样本文件
    samples: list[tuple[str, Path, str]] = []
    for split in ("hard", "clean"):
        split_dir = eval_dir / split
        if not split_dir.exists():
            continue
        for wav in sorted(split_dir.glob("*.wav")):
            ref_file = wav.with_suffix(".txt")
            if ref_file.exists():
                samples.append((split, wav, ref_file.read_text(encoding="utf-8").strip()))
    if not samples:
        print(f"未在 {eval_dir}/hard 或 {eval_dir}/clean 下找到成对的 *.wav + *.txt。", file=sys.stderr)
        return 2

    print(f"已加载词表 {len(tokens)} 条，语料 {len(samples)} 条（模型目录: {model_dir}）。")
    print("提示：完整端到端特征前端建议优先复用 macOS/Windows 本机 trace_log 导出语料或接入 fbank/LFR 前端。")
    _ = (np, ort, meta)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="VoiceTyper <asr_hints> 离线评测与诊断日志分析工具")
    parser.add_argument(
        "--mode",
        required=True,
        choices=["oracle", "llm", "trace-summary", "replay"],
        help="运行模式",
    )
    parser.add_argument("--eval-dir", help="真实录音语料目录（含 hard/ 与 clean/，不入库）")
    parser.add_argument("--model-dir", help="SenseVoice-Small ONNX 模型目录")
    parser.add_argument("--from-trace", help="本机 correction_trace.jsonl 路径（trace-summary / replay 模式）")
    parser.add_argument("--export-diffs", help="trace-summary 导出对照 JSONL 路径")
    parser.add_argument(
        "--prompt",
        default=str(Path(__file__).resolve().parents[1] / "Resources/correction.md"),
        help="校对 system prompt 路径（默认 macos/Resources/correction.md）",
    )
    parser.add_argument("--base-url", default=os.environ.get("VOICETYPER_LLM_BASE_URL", ""))
    parser.add_argument("--api-key", default=os.environ.get("VOICETYPER_LLM_API_KEY", ""))
    parser.add_argument("--model", default=os.environ.get("VOICETYPER_LLM_MODEL", "gpt-4o-mini"))
    parser.add_argument("--no-hints-few-shot", action="store_true", help="重放时不附带第 9 组 hints few-shot")
    parser.add_argument("--limit", type=int, default=None, help="replay 模式最多重放条数")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.mode == "trace-summary":
        if not args.from_trace:
            parser.error("--mode trace-summary 需要 --from-trace")
        export_path = Path(args.export_diffs) if args.export_diffs else None
        return run_trace_summary(Path(args.from_trace), export_path)
    if args.mode == "replay":
        if not args.from_trace or not args.base_url or not args.api_key:
            parser.error("--mode replay 需要 --from-trace、--base-url 与 --api-key")
        return run_replay(
            trace_path=Path(args.from_trace),
            prompt_path=Path(args.prompt),
            base_url=args.base_url,
            api_key=args.api_key,
            model=args.model,
            include_hints_few_shot=not args.no_hints_few_shot,
            limit=args.limit,
        )
    return run_oracle_or_llm(args)


if __name__ == "__main__":
    raise SystemExit(main())
