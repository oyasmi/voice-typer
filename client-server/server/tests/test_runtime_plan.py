"""resolve_runtime_plan：哪个参数在哪种模式下生效，钉成断言而不是靠读代码猜。

纯函数测试，不加载任何模型。args 用 SimpleNamespace 模拟 argparse 的结果，
字段需与 cli.py:build_parser() 保持一致。
"""
from types import SimpleNamespace

from voice_typer_server.app import resolve_runtime_plan


def make_args(**overrides):
    defaults = dict(
        streaming=True,
        model=None,
        offline_model=None,
        punc_model="ct-punc",
        sensevoice_language="auto",
        chunk_size="0,10,5",
        device="cpu",
        onnx_threads=4,
    )
    defaults.update(overrides)
    return SimpleNamespace(**defaults)


# -- 流式 × SenseVoice（默认组合，单模型）--------------------------------------

def test_streaming_sensevoice_is_single_model():
    plan = resolve_runtime_plan(make_args())
    assert plan.streaming is True
    assert plan.single_model is True
    assert plan.offline_model == "sensevoice-small"
    assert plan.preview_model == "sensevoice-small"
    assert plan.punc_model is None  # 自带标点，ct-punc 被忽略


def test_streaming_sensevoice_ignores_model_and_chunk_size():
    plan = resolve_runtime_plan(make_args(model="whatever", chunk_size="5,10,5"))
    ignored_flags = {flag for flag, _ in plan.ignored}
    assert "--model whatever" in ignored_flags
    assert "--chunk-size 5,10,5" in ignored_flags


def test_streaming_sensevoice_ignores_punc_model_flag():
    plan = resolve_runtime_plan(make_args(punc_model="ct-punc"))
    ignored_flags = {flag for flag, _ in plan.ignored}
    assert "--punc-model ct-punc" in ignored_flags


def test_streaming_sensevoice_no_ignored_when_flags_at_default():
    plan = resolve_runtime_plan(make_args(punc_model="none"))
    assert plan.ignored == []


# -- 流式 × paraformer（双模型）-----------------------------------------------

def test_streaming_paraformer_offline_uses_two_models():
    plan = resolve_runtime_plan(make_args(offline_model="paraformer-zh"))
    assert plan.streaming is True
    assert plan.single_model is False
    assert plan.offline_model == "paraformer-zh"
    assert plan.preview_model == "paraformer-zh-streaming"  # DEFAULT_STREAMING_MODEL
    assert plan.punc_model == "ct-punc"  # paraformer 需要外挂标点，不忽略
    assert plan.ignored == []


def test_streaming_paraformer_respects_custom_preview_model():
    plan = resolve_runtime_plan(
        make_args(offline_model="paraformer-zh", model="custom-streaming"))
    assert plan.preview_model == "custom-streaming"
    assert plan.ignored == []  # --model 在双模型模式下是生效的，不该被标记忽略


def test_streaming_paraformer_ignores_sensevoice_language():
    plan = resolve_runtime_plan(
        make_args(offline_model="paraformer-zh", sensevoice_language="zh"))
    ignored_flags = {flag for flag, _ in plan.ignored}
    assert "--sensevoice-language zh" in ignored_flags


# -- 非流式 × SenseVoice --------------------------------------------------------

def test_non_streaming_sensevoice_default():
    plan = resolve_runtime_plan(make_args(streaming=False))
    assert plan.streaming is False
    assert plan.offline_model == "sensevoice-small"
    assert plan.preview_model is None
    assert plan.punc_model is None


def test_non_streaming_ignores_offline_model_flag():
    plan = resolve_runtime_plan(
        make_args(streaming=False, offline_model="paraformer-zh"))
    ignored_flags = {flag for flag, _ in plan.ignored}
    assert "--offline-model paraformer-zh" in ignored_flags
    # --offline-model 被忽略不影响最终模型选择：仍由 --model（此处为默认）决定
    assert plan.offline_model == "sensevoice-small"


def test_non_streaming_ignores_chunk_size_flag():
    plan = resolve_runtime_plan(make_args(streaming=False, chunk_size="10,20,10"))
    ignored_flags = {flag for flag, _ in plan.ignored}
    assert "--chunk-size 10,20,10" in ignored_flags


# -- 非流式 × paraformer --------------------------------------------------------

def test_non_streaming_paraformer_keeps_punc_model():
    plan = resolve_runtime_plan(make_args(streaming=False, model="paraformer-zh"))
    assert plan.offline_model == "paraformer-zh"
    assert plan.punc_model == "ct-punc"
    assert plan.ignored == []


def test_non_streaming_punc_model_none_disables_punctuation():
    plan = resolve_runtime_plan(
        make_args(streaming=False, model="paraformer-zh", punc_model="none"))
    assert plan.punc_model is None
