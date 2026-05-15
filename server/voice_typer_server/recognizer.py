"""
FunASR ONNXRuntime 语音识别封装

提供两种识别器：
- SpeechRecognizer      离线整段识别（paraformer-zh）
- StreamingSpeechRecognizer  流式逐块识别（paraformer-zh-streaming）
"""
import importlib
import importlib.machinery
import logging
from pathlib import Path
import sys
import types
from typing import List, Optional, Tuple, Type, Union

import numpy as np

logger = logging.getLogger("VoiceTyper")


# ---------------------------------------------------------------------------
# 公共工具
# ---------------------------------------------------------------------------

def _bypass_load(submodule: str) -> object:
    """绕过 funasr_onnx 包级 import 里的 torch 依赖，直接加载指定子模块。"""
    pkg = "funasr_onnx"
    if pkg not in sys.modules:
        spec = importlib.machinery.PathFinder.find_spec(pkg, sys.path)
        if spec is None or spec.submodule_search_locations is None:
            raise ImportError("未找到 funasr_onnx 包，请先 pip install funasr-onnx")
        package = types.ModuleType(pkg)
        package.__path__ = list(spec.submodule_search_locations)
        package.__spec__ = spec
        sys.modules[pkg] = package
    return importlib.import_module(submodule)


def _resolve_device_id(device: str) -> Union[str, int]:
    d = (device or "cpu").lower()
    if d == "cpu":
        return "-1"
    if d.startswith("cuda:"):
        return int(d.split(":", 1)[1])
    if d == "cuda":
        return 0
    logger.warning(f"ONNX 后端暂不支持 device={device}，已回退到 CPU")
    return "-1"


def _prepare_model_dir(model_name: Optional[str], aliases: dict) -> Tuple[Optional[str], bool]:
    """返回 (model_dir, quantize)；不存在则从 ModelScope 下载。"""
    if not model_name:
        return None, False

    resolved = aliases.get(model_name, model_name)
    model_dir = Path(resolved).expanduser()

    if not model_dir.exists():
        cache_path = Path.home() / ".cache/modelscope/hub/models" / resolved
        if cache_path.exists():
            model_dir = cache_path
        else:
            from modelscope.hub.snapshot_download import snapshot_download
            logger.info(f"下载 ONNX 模型: {resolved}")
            model_dir = Path(snapshot_download(resolved))

    if (model_dir / "model.onnx").exists():
        return str(model_dir), False
    if (model_dir / "model_quant.onnx").exists():
        logger.info(f"仅找到量化模型，自动使用: {model_dir}")
        return str(model_dir), True

    raise FileNotFoundError(f"未找到 ONNX 模型文件: {model_dir}")


def _extract_preds_text(asr_result) -> str:
    """从 funasr-onnx ASR 结果中提取文本，兼容 online/offline 两种返回格式。"""
    if not asr_result:
        return ""
    first = asr_result[0]
    preds = first.get("preds", "") if isinstance(first, dict) else first
    if isinstance(preds, str):
        return preds
    if isinstance(preds, (list, tuple)) and preds:
        head = preds[0]
        return head if isinstance(head, str) else str(head)
    return str(preds) if preds else ""


def _extract_punc_text(punc_result) -> str:
    if not punc_result:
        return ""
    if isinstance(punc_result, str):
        return punc_result
    if isinstance(punc_result, (list, tuple)) and punc_result:
        head = punc_result[0]
        if isinstance(head, str):
            return head
        if isinstance(head, (list, tuple)) and head:
            nested = head[0]
            return nested if isinstance(nested, str) else str(nested)
        return str(head)
    return str(punc_result)


# ---------------------------------------------------------------------------
# 离线整段识别器
# ---------------------------------------------------------------------------

class SpeechRecognizer:
    """基于 funasr-onnx paraformer_bin 的离线整段识别器。"""

    MODEL_ALIASES = {
        "paraformer-zh": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
        "ct-punc":       "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
    }

    def __init__(
        self,
        model_name: str = "paraformer-zh",
        punc_model: Optional[str] = "ct-punc",
        device: str = "cpu",
        intra_op_num_threads: int = 4,
    ):
        self.model_name = model_name
        self.punc_model_name = punc_model
        self.device = device
        self.intra_op_num_threads = intra_op_num_threads
        self._model = None
        self._punc_model = None
        self._initialized = False
        self._hotword_supported: Optional[bool] = None

    def initialize(self):
        paraformer_mod = _bypass_load("funasr_onnx.paraformer_bin")
        punc_mod       = _bypass_load("funasr_onnx.punc_bin")

        device_id = _resolve_device_id(self.device)
        asr_dir, asr_q   = _prepare_model_dir(self.model_name,      self.MODEL_ALIASES)
        punc_dir, punc_q = _prepare_model_dir(self.punc_model_name, self.MODEL_ALIASES)

        logger.info(f"[1/2] 加载 ONNX 离线 ASR 模型: {asr_dir}")
        self._model = paraformer_mod.Paraformer(
            asr_dir,
            batch_size=1,
            device_id=device_id,
            quantize=asr_q,
            intra_op_num_threads=self.intra_op_num_threads,
        )

        if punc_dir:
            logger.info(f"[2/2] 加载 ONNX 标点恢复模型: {punc_dir}")
            try:
                self._punc_model = punc_mod.CT_Transformer(
                    punc_dir,
                    device_id=device_id,
                    quantize=punc_q,
                    intra_op_num_threads=self.intra_op_num_threads,
                )
            except Exception as exc:
                logger.warning(f"标点模型加载失败，将跳过标点: {exc}")
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    def recognize(self, audio: np.ndarray, hotwords: str = "") -> str:
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")
        if len(audio) == 0:
            return ""

        result = self._call_with_hotword(audio, hotwords)
        if not result:
            return ""

        text = self._extract_asr_text(result)
        if self._punc_model and text:
            try:
                punc_result = self._punc_model(text)
                normalized = _extract_punc_text(punc_result)
                if normalized:
                    text = normalized
            except Exception as exc:
                logger.warning(f"标点恢复失败: {exc}")
        return text

    def _call_with_hotword(self, audio, hotwords):
        if not hotwords:
            return self._model(audio)
        if self._hotword_supported is False:
            return self._model(audio)
        try:
            result = self._model(audio, hotword=hotwords)
            self._hotword_supported = True
            return result
        except TypeError:
            logger.warning("当前 funasr-onnx 运行时不支持 hotword 参数，已忽略热词")
            self._hotword_supported = False
            return self._model(audio)

    def _extract_asr_text(self, asr_result) -> str:
        return _extract_preds_text(asr_result)


# ---------------------------------------------------------------------------
# 流式逐块识别器
# ---------------------------------------------------------------------------

class StreamingSpeechRecognizer:
    """基于 funasr-onnx paraformer_online_bin 的流式识别器。"""

    MODEL_ALIASES = {
        "paraformer-zh-streaming": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-onnx",
        "ct-punc":                 "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
    }

    def __init__(
        self,
        model_name: str = "paraformer-zh-streaming",
        punc_model: Optional[str] = "ct-punc",
        device: str = "cpu",
        chunk_size: List[int] = None,
        intra_op_num_threads: int = 4,
    ):
        self.model_name = model_name
        self.punc_model_name = punc_model
        self.device = device
        self.chunk_size = chunk_size or [0, 10, 5]
        self.intra_op_num_threads = intra_op_num_threads
        self._model = None
        self._punc_model = None
        self._initialized = False

    def initialize(self):
        online_mod = _bypass_load("funasr_onnx.paraformer_online_bin")
        punc_mod   = _bypass_load("funasr_onnx.punc_bin")

        device_id = _resolve_device_id(self.device)
        asr_dir, asr_q   = _prepare_model_dir(self.model_name,      self.MODEL_ALIASES)
        punc_dir, punc_q = _prepare_model_dir(self.punc_model_name, self.MODEL_ALIASES)

        logger.info(f"[1/2] 加载 ONNX 流式 ASR 模型: {asr_dir}")
        self._model = online_mod.Paraformer(
            model_dir=asr_dir,
            batch_size=1,
            chunk_size=self.chunk_size,
            device_id=device_id,
            quantize=asr_q,
            intra_op_num_threads=self.intra_op_num_threads,
        )

        if punc_dir:
            logger.info(f"[2/2] 加载 ONNX 标点恢复模型: {punc_dir}")
            try:
                self._punc_model = punc_mod.CT_Transformer(
                    punc_dir,
                    device_id=device_id,
                    quantize=punc_q,
                    intra_op_num_threads=self.intra_op_num_threads,
                )
            except Exception as exc:
                logger.warning(f"标点模型加载失败，将跳过标点: {exc}")
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    def new_session(self) -> "Session":
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")
        return Session(self)

    def _extract_fragment(self, asr_result) -> str:
        return _extract_preds_text(asr_result)

    def _apply_punc(self, text: str) -> str:
        if not self._punc_model or not text.strip():
            return text
        try:
            return _extract_punc_text(self._punc_model(text)) or text
        except Exception as exc:
            logger.warning(f"标点恢复失败: {exc}")
            return text


class Session:
    """单次录音会话，对应一个 WebSocket 连接。每次新建，用完即弃。"""

    def __init__(self, owner: StreamingSpeechRecognizer):
        self._owner = owner
        self._cache: dict = {}
        self._fragments: List[str] = []

    def feed(self, audio_chunk: np.ndarray) -> str:
        """喂入约 600ms 的 PCM chunk，返回该 chunk 的文本增量（可能为空）。"""
        if len(audio_chunk) == 0:
            return ""
        try:
            result = self._owner._model(
                audio_chunk,
                param_dict={"is_final": False, "cache": self._cache},
            )
            fragment = self._owner._extract_fragment(result)
            if fragment:
                self._fragments.append(fragment)
            return fragment
        except Exception as exc:
            logger.error(f"流式 feed 失败: {exc}")
            return ""

    def finalize(self, tail_chunk: Optional[np.ndarray] = None) -> str:
        """Flush 尾音 → 拼接 → ct-punc → 返回最终文本。"""
        if tail_chunk is not None and len(tail_chunk) > 0:
            try:
                result = self._owner._model(
                    tail_chunk,
                    param_dict={"is_final": True, "cache": self._cache},
                )
                fragment = self._owner._extract_fragment(result)
                if fragment:
                    self._fragments.append(fragment)
            except Exception as exc:
                logger.error(f"流式 finalize flush 失败: {exc}")
        else:
            try:
                self._owner._model(
                    np.zeros(0, dtype=np.float32),
                    param_dict={"is_final": True, "cache": self._cache},
                )
            except Exception as exc:
                logger.debug(f"流式 finalize flush 跳过: {exc}")

        full_text = "".join(self._fragments)
        return self._owner._apply_punc(full_text)
