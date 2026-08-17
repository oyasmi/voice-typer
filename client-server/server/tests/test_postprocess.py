"""SenseVoice 后处理正则 + 结果提取的兼容分支。

纯字符串函数，不需要 ONNX 模型：_postprocess 是 @staticmethod，
_extract_preds_text / _extract_punc_text 是模块级函数。
"""
import pytest

from voice_typer_server.recognizer import (
    SenseVoiceRecognizer,
    _extract_preds_text,
    _extract_punc_text,
)


# -- _postprocess ------------------------------------------------------------

def test_postprocess_strips_rich_tags_and_sentencepiece_space():
    raw = "<|zh|><|NEUTRAL|><|Speech|><|withitn|>▁你好世界"
    assert SenseVoiceRecognizer._postprocess(raw) == "你好世界"


def test_postprocess_collapses_internal_whitespace():
    raw = "▁你好   世界"
    assert SenseVoiceRecognizer._postprocess(raw) == "你好 世界"


def test_postprocess_removes_space_between_cjk_and_latin():
    # SentencePiece 词首标记在中英交界处留下的空格要被吃掉
    assert SenseVoiceRecognizer._postprocess("▁用▁swift▁写") == "用swift写"


def test_postprocess_keeps_space_within_pure_english():
    assert SenseVoiceRecognizer._postprocess("▁hello▁world") == "hello world"


def test_postprocess_discards_punctuation_only_result():
    # 静音附近 SenseVoice 常吐一个孤立句号，没有上屏价值
    assert SenseVoiceRecognizer._postprocess("。") == ""
    assert SenseVoiceRecognizer._postprocess("  ") == ""


def test_postprocess_keeps_result_with_digits_only():
    assert SenseVoiceRecognizer._postprocess("64") == "64"


# -- _extract_preds_text ------------------------------------------------------

def test_extract_preds_text_from_dict_with_str():
    assert _extract_preds_text([{"preds": "你好"}]) == "你好"


def test_extract_preds_text_from_bare_str():
    assert _extract_preds_text(["你好"]) == "你好"


def test_extract_preds_text_from_dict_with_list():
    assert _extract_preds_text([{"preds": ["你好"]}]) == "你好"


def test_extract_preds_text_empty_result():
    assert _extract_preds_text([]) == ""
    assert _extract_preds_text(None) == ""


def test_extract_preds_text_empty_preds_list():
    assert _extract_preds_text([{"preds": []}]) == ""


# -- _extract_punc_text --------------------------------------------------------

def test_extract_punc_text_from_bare_str():
    assert _extract_punc_text("你好。") == "你好。"


def test_extract_punc_text_from_list_of_str():
    assert _extract_punc_text(["你好。"]) == "你好。"


def test_extract_punc_text_from_nested_list():
    assert _extract_punc_text([["你好。"]]) == "你好。"


def test_extract_punc_text_empty_result():
    assert _extract_punc_text(None) == ""
    assert _extract_punc_text([]) == ""
    assert _extract_punc_text("") == ""
