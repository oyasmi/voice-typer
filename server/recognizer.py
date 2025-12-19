"""
FunASR 语音识别封装
"""
import time
import warnings
import numpy as np
from typing import Optional, Callable

warnings.filterwarnings('ignore')


class SpeechRecognizer:
    """语音识别器"""
    
    def __init__(
        self,
        model_name: str = "paraformer-zh",
        punc_model: Optional[str] = "ct-punc",
        device: str = "mps",
    ):
        self.model_name = model_name
        self.punc_model_name = punc_model
        self.device = device
        
        self._model = None
        self._punc_model = None
        self._initialized = False
    
    def initialize(self, log: Optional[Callable[[str], None]] = None):
        """初始化模型"""
        log = log or print
        
        from funasr import AutoModel
        
        # 加载主模型
        log(f"[1/2] 加载语音识别模型: {self.model_name}")
        t0 = time.time()
        self._model = AutoModel(
            model=self.model_name,
            device=self.device,
            disable_update=True,
        )
        log(f"      完成，耗时 {time.time() - t0:.1f}s")
        
        # 加载标点模型
        if self.punc_model_name:
            log(f"[2/2] 加载标点恢复模型: {self.punc_model_name}")
            t0 = time.time()
            try:
                self._punc_model = AutoModel(
                    model=self.punc_model_name,
                    device=self.device,
                    disable_update=True,
                )
                log(f"      完成，耗时 {time.time() - t0:.1f}s")
            except Exception as e:
                log(f"      标点模型加载失败: {e}")
                self._punc_model = None
        else:
            log("[2/2] 标点恢复模型: 已禁用")
        
        self._initialized = True
    
    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None
    
    def recognize(self, audio: np.ndarray, hotwords: str = "") -> str:
        """识别音频
        
        Args:
            audio: float32 音频数据，16kHz 采样率
            hotwords: 热词，空格分隔
            
        Returns:
            识别结果文本
        """
        if not self.is_ready:
            raise RuntimeError("模型未初始化")
        
        if len(audio) == 0:
            return ""
        
        # 语音识别
        kwargs = {"input": audio}
        if hotwords:
            kwargs["hotword"] = hotwords
        
        result = self._model.generate(**kwargs)
        
        if not result or len(result) == 0:
            return ""
        
        text = result[0].get("text", "")
        
        # 标点恢复
        if self._punc_model and text:
            try:
                punc_result = self._punc_model.generate(input=text)
                if punc_result and len(punc_result) > 0:
                    text = punc_result[0].get("text", text)
            except Exception:
                pass
        
        return text