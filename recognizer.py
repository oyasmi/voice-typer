"""
FunASR 语音识别封装模块
"""
import numpy as np
from typing import Optional, Dict, Any, List, Generator
import threading


class SpeechRecognizer:
    """语音识别器 - 封装 FunASR"""
    
    def __init__(
        self,
        model_name: str = "paraformer-zh",
        streaming_model_name: str = "paraformer-zh-streaming",
        punc_model: Optional[str] = "ct-punc",
        device: str = "mps",
        hotwords: str = "",
    ):
        self.model_name = model_name
        self.streaming_model_name = streaming_model_name
        self.punc_model = punc_model
        self.device = device
        self.hotwords = hotwords
        
        self._model = None
        self._streaming_model = None
        self._punc = None
        self._lock = threading.Lock()
        self._initialized = False
        
    def initialize(self, callback=None):
        """初始化模型（首次使用时会下载模型）
        
        Args:
            callback: 进度回调函数，签名: (message: str) -> None
        """
        if self._initialized:
            return
            
        from funasr import AutoModel
        
        if callback:
            callback("正在加载主模型...")
        
        # 加载非流式模型
        self._model = AutoModel(
            model=self.model_name,
            device=self.device,
            disable_update=True,
        )
        
        if callback:
            callback("正在加载流式模型...")
        
        # 加载流式模型
        self._streaming_model = AutoModel(
            model=self.streaming_model_name,
            device=self.device,
            disable_update=True,
        )
        
        # 加载标点模型
        if self.punc_model:
            if callback:
                callback("正在加载标点模型...")
            self._punc = AutoModel(
                model=self.punc_model,
                device=self.device,
                disable_update=True,
            )
        
        self._initialized = True
        if callback:
            callback("模型加载完成")
    
    @property
    def is_initialized(self) -> bool:
        return self._initialized
    
    def recognize(self, audio: np.ndarray) -> str:
        """非流式识别（一次性识别整段音频）
        
        Args:
            audio: 音频数据，float32，16kHz 采样率
            
        Returns:
            识别结果文本
        """
        if not self._initialized:
            raise RuntimeError("识别器未初始化，请先调用 initialize()")
        
        if len(audio) == 0:
            return ""
        
        with self._lock:
            # 构建识别参数
            kwargs = {"input": audio}
            if self.hotwords:
                kwargs["hotword"] = self.hotwords
            
            result = self._model.generate(**kwargs)
            
            if not result or len(result) == 0:
                return ""
            
            text = result[0].get("text", "")
            
            # 添加标点
            if self._punc and text:
                punc_result = self._punc.generate(input=text)
                if punc_result and len(punc_result) > 0:
                    text = punc_result[0].get("text", text)
            
            return text
    
    def recognize_streaming(
        self,
        audio_generator: Generator[np.ndarray, None, None],
        chunk_size: List[int] = [0, 10, 5],
        on_partial: Optional[callable] = None,
    ) -> str:
        """流式识别
        
        Args:
            audio_generator: 音频数据生成器，每次 yield 一个音频块
            chunk_size: 流式识别的 chunk 配置
            on_partial: 部分结果回调，签名: (partial_text: str) -> None
            
        Returns:
            完整识别结果
        """
        if not self._initialized:
            raise RuntimeError("识别器未初始化，请先调用 initialize()")
        
        cache = {}
        full_text_parts = []
        chunk_stride = chunk_size[1] * 960  # 每个 chunk 的采样点数
        
        audio_buffer = np.array([], dtype=np.float32)
        
        with self._lock:
            for audio_chunk in audio_generator:
                audio_buffer = np.concatenate([audio_buffer, audio_chunk])
                
                # 当缓冲区足够大时处理
                while len(audio_buffer) >= chunk_stride:
                    chunk = audio_buffer[:chunk_stride]
                    audio_buffer = audio_buffer[chunk_stride:]
                    
                    result = self._streaming_model.generate(
                        input=chunk,
                        cache=cache,
                        is_final=False,
                        chunk_size=chunk_size,
                    )
                    
                    if result and len(result) > 0:
                        partial_text = result[0].get("text", "")
                        if partial_text:
                            full_text_parts.append(partial_text)
                            if on_partial:
                                on_partial(partial_text)
            
            # 处理剩余音频
            if len(audio_buffer) > 0:
                result = self._streaming_model.generate(
                    input=audio_buffer,
                    cache=cache,
                    is_final=True,
                    chunk_size=chunk_size,
                )
                if result and len(result) > 0:
                    partial_text = result[0].get("text", "")
                    if partial_text:
                        full_text_parts.append(partial_text)
                        if on_partial:
                            on_partial(partial_text)
        
        return "".join(full_text_parts)


class StreamingSession:
    """流式识别会话 - 用于长时间录音的实时识别"""
    
    def __init__(
        self,
        recognizer: SpeechRecognizer,
        chunk_size: List[int] = [0, 10, 5],
        on_result: Optional[callable] = None,
    ):
        self.recognizer = recognizer
        self.chunk_size = chunk_size
        self.on_result = on_result
        
        self._cache = {}
        self._buffer = np.array([], dtype=np.float32)
        self._chunk_stride = chunk_size[1] * 960
        self._lock = threading.Lock()
        self._text_parts = []
    
    def feed(self, audio_chunk: np.ndarray) -> Optional[str]:
        """喂入音频数据
        
        Args:
            audio_chunk: 音频块
            
        Returns:
            如果有新的识别结果则返回，否则返回 None
        """
        with self._lock:
            self._buffer = np.concatenate([self._buffer, audio_chunk])
            
            result_text = None
            while len(self._buffer) >= self._chunk_stride:
                chunk = self._buffer[:self._chunk_stride]
                self._buffer = self._buffer[self._chunk_stride:]
                
                result = self.recognizer._streaming_model.generate(
                    input=chunk,
                    cache=self._cache,
                    is_final=False,
                    chunk_size=self.chunk_size,
                )
                
                if result and len(result) > 0:
                    text = result[0].get("text", "")
                    if text:
                        self._text_parts.append(text)
                        result_text = text
                        if self.on_result:
                            self.on_result(text)
            
            return result_text
    
    def finalize(self) -> str:
        """结束会话，处理剩余音频
        
        Returns:
            最后一部分识别结果
        """
        with self._lock:
            if len(self._buffer) > 0:
                result = self.recognizer._streaming_model.generate(
                    input=self._buffer,
                    cache=self._cache,
                    is_final=True,
                    chunk_size=self.chunk_size,
                )
                self._buffer = np.array([], dtype=np.float32)
                
                if result and len(result) > 0:
                    text = result[0].get("text", "")
                    if text:
                        self._text_parts.append(text)
                        if self.on_result:
                            self.on_result(text)
                        return text
            
            return ""
    
    def get_full_text(self) -> str:
        """获取完整识别文本"""
        return "".join(self._text_parts)


if __name__ == "__main__":
    # 简单测试
    import time
    from recorder import AudioRecorder
    
    print("初始化识别器...")
    recognizer = SpeechRecognizer(device="mps")
    recognizer.initialize(callback=print)
    
    print("\n开始录音测试 (3秒)...")
    recorder = AudioRecorder()
    recorder.start()
    time.sleep(3)
    audio = recorder.stop()
    
    print("识别中...")
    text = recognizer.recognize(audio)
    print(f"识别结果: {text}")