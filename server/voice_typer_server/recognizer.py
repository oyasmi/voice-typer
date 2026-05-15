"""
FunASR ONNXRuntime 流式语音识别封装
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


class StreamingSpeechRecognizer:
    """基于 funasr-onnx paraformer_online_bin 的流式语音识别器"""

    MODEL_ALIASES = {
        "paraformer-zh-streaming": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-onnx",
        "ct-punc": "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
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

    # ------------------------------------------------------------------
    # 模型加载
    # ------------------------------------------------------------------

    def _resolve_model_name(self, model_name: Optional[str]) -> Optional[str]:
        if not model_name:
            return model_name
        return self.MODEL_ALIASES.get(model_name, model_name)

    def _prepare_model_dir(self, model_name: Optional[str]) -> Tuple[Optional[str], bool]:
        """返回 (model_dir, quantize)；不存在则下载。"""
        if not model_name:
            return None, False

        resolved = self._resolve_model_name(model_name)
        model_dir = Path(resolved).expanduser()

        if not model_dir.exists():
            # 尝试 ModelScope 缓存路径
            cache_path = Path.home() / ".cache/modelscope/hub/models" / resolved
            if cache_path.exists():
                model_dir = cache_path
            else:
                from modelscope.hub.snapshot_download import snapshot_download
                logger.info(f"下载 ONNX 模型: {resolved}")
                model_dir = Path(snapshot_download(resolved))

        # streaming 模型有 encoder (model.onnx) + decoder (decoder.onnx)
        # 离线模型只有 model.onnx（或 model_quant.onnx）
        if (model_dir / "model.onnx").exists():
            return str(model_dir), False
        if (model_dir / "model_quant.onnx").exists():
            logger.info(f"仅找到量化模型，自动使用: {model_dir}")
            return str(model_dir), True

        raise FileNotFoundError(f"未找到 ONNX 模型文件: {model_dir}")

    def _resolve_device_id(self) -> Union[str, int]:
        device = (self.device or "cpu").lower()
        if device == "cpu":
            return "-1"
        if device.startswith("cuda:"):
            return int(device.split(":", 1)[1])
        if device == "cuda":
            return 0
        logger.warning(f"ONNX 后端暂不支持 device={self.device}，已回退到 CPU")
        return "-1"

    def _load_onnx_classes(self) -> Tuple[Type, Type]:
        """绕过 funasr_onnx 包级 import 里的 torch 依赖，直接加载所需子模块。"""
        pkg = "funasr_onnx"
        if pkg not in sys.modules:
            spec = importlib.machinery.PathFinder.find_spec(pkg, sys.path)
            if spec is None or spec.submodule_search_locations is None:
                raise ImportError("未找到 funasr_onnx 包")
            package = types.ModuleType(pkg)
            package.__path__ = list(spec.submodule_search_locations)
            package.__spec__ = spec
            sys.modules[pkg] = package

        online_mod = importlib.import_module("funasr_onnx.paraformer_online_bin")
        punc_mod = importlib.import_module("funasr_onnx.punc_bin")
        return online_mod.Paraformer, punc_mod.CT_Transformer

    def initialize(self):
        """初始化 ONNX 模型（阻塞，耗时较长）。"""
        ParaformerOnline, CT_Transformer = self._load_onnx_classes()

        device_id = self._resolve_device_id()
        asr_model_dir, asr_quantized = self._prepare_model_dir(self.model_name)
        punc_model_dir, punc_quantized = self._prepare_model_dir(self.punc_model_name)

        logger.info(f"[1/2] 加载 ONNX Streaming ASR 模型: {asr_model_dir}")
        self._model = ParaformerOnline(
            model_dir=asr_model_dir,
            batch_size=1,
            chunk_size=self.chunk_size,
            device_id=device_id,
            quantize=asr_quantized,
            intra_op_num_threads=self.intra_op_num_threads,
        )

        if punc_model_dir:
            logger.info(f"[2/2] 加载 ONNX 标点恢复模型: {punc_model_dir}")
            try:
                self._punc_model = CT_Transformer(
                    punc_model_dir,
                    device_id=device_id,
                    quantize=punc_quantized,
                    intra_op_num_threads=self.intra_op_num_threads,
                )
            except Exception as exc:
                logger.warning(f"ONNX 标点模型加载失败，将跳过标点: {exc}")
                self._punc_model = None
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    # ------------------------------------------------------------------
    # 会话工厂
    # ------------------------------------------------------------------

    def new_session(self) -> "Session":
        """为每个 WebSocket 连接创建一个独立的识别会话。"""
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")
        return Session(self)

    # ------------------------------------------------------------------
    # 内部：文本提取工具
    # ------------------------------------------------------------------

    def _extract_asr_fragment(self, asr_result) -> str:
        """从 paraformer_online_bin 的返回结果中提取文本增量。"""
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

    def _apply_punc(self, text: str) -> str:
        """对最终拼接文本打标点；失败时返回原文本。"""
        if not self._punc_model or not text.strip():
            return text
        try:
            punc_result = self._punc_model(text)
            return self._extract_punc_text(punc_result) or text
        except Exception as exc:
            logger.warning(f"ONNX 标点恢复失败: {exc}")
            return text

    def _extract_punc_text(self, punc_result) -> str:
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


class Session:
    """
    单次录音会话，对应一个 WebSocket 连接。

    - feed()     录音期间每 600ms 调用一次，返回增量文本（可能为空）。
    - finalize() 用户松开热键后调用一次，返回带标点的最终文本。
    - 关闭连接后直接丢弃此对象，cache 随之释放。
    """

    def __init__(self, owner: StreamingSpeechRecognizer):
        self._owner = owner
        self._cache: dict = {}
        self._fragments: List[str] = []

    def feed(self, audio_chunk: np.ndarray) -> str:
        """
        喂入一个 chunk（约 600ms，9600 samples @ 16kHz），返回该 chunk 的识别增量。
        线程不安全：调用方（executor）保证串行。
        """
        if len(audio_chunk) == 0:
            return ""
        try:
            result = self._owner._model(
                audio_chunk,
                param_dict={"is_final": False, "cache": self._cache},
            )
            fragment = self._owner._extract_asr_fragment(result)
            if fragment:
                self._fragments.append(fragment)
            return fragment
        except Exception as exc:
            logger.error(f"Streaming feed 失败: {exc}")
            return ""

    def finalize(self, tail_chunk: Optional[np.ndarray] = None) -> str:
        """
        Flush 尾音 → 拼接所有片段 → 打标点 → 返回最终文本。
        tail_chunk 是录音停止时不足 600ms 的剩余音频，可为 None 或空数组。
        """
        if tail_chunk is not None and len(tail_chunk) > 0:
            try:
                result = self._owner._model(
                    tail_chunk,
                    param_dict={"is_final": True, "cache": self._cache},
                )
                fragment = self._owner._extract_asr_fragment(result)
                if fragment:
                    self._fragments.append(fragment)
            except Exception as exc:
                logger.error(f"Streaming finalize flush 失败: {exc}")
        else:
            # 无尾巴：发一个空的 is_final flush 让模型清空内部 cache
            try:
                empty = np.zeros(0, dtype=np.float32)
                self._owner._model(
                    empty,
                    param_dict={"is_final": True, "cache": self._cache},
                )
            except Exception:
                pass

        full_text = "".join(self._fragments)
        return self._owner._apply_punc(full_text)
