"""
FunASR ONNXRuntime 语音识别封装
"""
import logging
from pathlib import Path
import numpy as np
from typing import Optional, Union

logger = logging.getLogger("VoiceTyper")


class SpeechRecognizer:
    """基于 funasr-onnx 的语音识别器"""

    MODEL_ALIASES = {
        "paraformer-zh": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
        "ct-punc": "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
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

    def _resolve_model_name(self, model_name: Optional[str]) -> Optional[str]:
        """兼容短别名写法"""
        if not model_name:
            return model_name
        return self.MODEL_ALIASES.get(model_name, model_name)

    def _prepare_model_dir(self, model_name: Optional[str]):
        """下载或解析本地 ONNX 模型目录，并优先使用完整版模型"""
        if not model_name:
            return None, False

        resolved = self._resolve_model_name(model_name)
        model_dir = Path(resolved).expanduser()

        if not model_dir.exists():
            from modelscope.hub.snapshot_download import snapshot_download

            logger.info(f"下载 ONNX 模型: {resolved}")
            model_dir = Path(snapshot_download(resolved))

        if (model_dir / "model.onnx").exists():
            return str(model_dir), False
        if (model_dir / "model_quant.onnx").exists():
            logger.info(f"模型目录仅包含量化 ONNX，自动使用量化模型: {model_dir}")
            return str(model_dir), True

        raise FileNotFoundError(f"未找到 ONNX 模型文件: {model_dir}")

    def _resolve_device_id(self) -> Union[str, int]:
        """将 device 参数转换为 funasr-onnx 所需的 device_id"""
        device = (self.device or "cpu").lower()
        if device == "cpu":
            return "-1"
        if device.startswith("cuda:"):
            return int(device.split(":", 1)[1])
        if device == "cuda":
            return 0

        logger.warning(f"ONNX 后端暂不支持 device={self.device}，已回退到 CPU")
        return "-1"

    def initialize(self):
        """初始化 ONNX 模型"""
        from funasr_onnx import CT_Transformer, Paraformer

        device_id = self._resolve_device_id()
        asr_model_dir, asr_quantized = self._prepare_model_dir(self.model_name)
        punc_model_dir, punc_quantized = self._prepare_model_dir(self.punc_model_name)

        logger.info(f"[1/2] 加载 ONNX 语音识别模型: {asr_model_dir}")
        self._model = Paraformer(
            asr_model_dir,
            batch_size=1,
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
            except Exception as e:
                logger.warning(f"      ONNX 标点模型加载失败: {e}")
                self._punc_model = None
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    def _recognize_with_hotword(self, audio: np.ndarray, hotwords: str):
        """尽量利用 ONNX 模型的热词能力；若运行时版本不支持则自动回退"""
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
        """兼容 Paraformer ONNX 的不同 preds 结构"""
        if not asr_result:
            return ""

        first = asr_result[0]
        preds = first.get("preds", "") if isinstance(first, dict) else first

        if isinstance(preds, str):
            return preds
        if isinstance(preds, tuple):
            head = preds[0]
            return head if isinstance(head, str) else str(head)
        if isinstance(preds, list) and preds:
            head = preds[0]
            return head if isinstance(head, str) else str(head)

        return str(preds)

    def _extract_punc_text(self, punc_result) -> str:
        """兼容 CT_Transformer 的不同返回结构"""
        if not punc_result:
            return ""
        if isinstance(punc_result, str):
            return punc_result
        if isinstance(punc_result, tuple):
            head = punc_result[0]
            return head if isinstance(head, str) else str(head)
        if isinstance(punc_result, list) and punc_result:
            head = punc_result[0]
            if isinstance(head, str):
                return head
            if isinstance(head, tuple):
                nested = head[0]
                return nested if isinstance(nested, str) else str(nested)
            return str(head)
        return str(punc_result)

    def recognize(self, audio: np.ndarray, hotwords: str = "") -> str:
        """识别音频并返回最终文本"""
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")

        if len(audio) == 0:
            return ""

        result = self._recognize_with_hotword(audio, hotwords)
        if not result:
            return ""

        text = self._extract_asr_text(result)
        if self._punc_model and text:
            try:
                punc_result = self._punc_model(text)
                normalized_text = self._extract_punc_text(punc_result)
                if normalized_text:
                    text = normalized_text
            except Exception as e:
                logger.warning(f"ONNX 标点恢复失败: {e}")

        return text
