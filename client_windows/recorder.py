"""
录音模块
"""
import numpy as np
import sounddevice as sd
import threading
import logging
from typing import Optional

logger = logging.getLogger("VoiceTyper")


class AudioRecorder:
    """音频录制器"""
    
    SAMPLE_RATE = 16000
    CHANNELS = 1
    DTYPE = np.float32
    
    def __init__(self):
        self._recording = False
        self._audio_buffer = []
        self._stream: Optional[sd.InputStream] = None
        self._stream_lock = threading.Lock()
        self._lock = threading.Lock()
        
        # 预初始化 stream (热启动)
        # 只要没有 start，占用资源很小
        self._init_stream()

    def _init_stream(self):
        try:
            self._stream = sd.InputStream(
                samplerate=self.SAMPLE_RATE,
                channels=self.CHANNELS,
                dtype=self.DTYPE,
                callback=self._callback,
                blocksize=int(self.SAMPLE_RATE * 0.05),
                # 增加延迟容忍度，避免 xrun 警告
                latency='low', 
            )
        except Exception as e:
            logger.error(f"Error initializing audio stream: {e}")
            self._stream = None

    def _callback(self, indata, frames, time_info, status):
        # if status:
        #     logger.warning(f"Audio status: {status}")
        if self._recording:
            with self._lock:
                self._audio_buffer.append(indata[:, 0].copy())

    def start(self):
        """开始录音"""
        if self._recording:
            return
        
        with self._lock:
            self._audio_buffer = []

        self._recording = True
        
        if self._stream:
            # 如果 stream 被关闭了（例如异常），尝试重新创建
            if self._stream.closed:
                 self._init_stream()
            
            if self._stream and not self._stream.active:
                try:
                    self._stream.start()
                except Exception as e:
                    logger.error(f"Error starting stream: {e}")
                    # 尝试重建
                    self._init_stream()
                    if self._stream:
                        self._stream.start()
        else:
             self._init_stream()
             if self._stream:
                 self._stream.start()
    
    def stop(self) -> np.ndarray:
        """停止录音并返回音频"""
        self._recording = False
        
        if self._stream:
            try:
                # 只停止不关闭，以便下次快速启动
                # 如果长时间不使用，可以考虑关闭，但为了响应速度，保持开启
                self._stream.stop()
            except Exception as e:
                logger.error(f"Error stopping stream: {e}")
                
        
        with self._lock:
            if self._audio_buffer:
                audio = np.concatenate(self._audio_buffer)
            else:
                audio = np.array([], dtype=self.DTYPE)
            self._audio_buffer = []
        
        return audio
    
    def close(self):
        """彻底关闭资源"""
        if self._stream:
            self._stream.close()
            self._stream = None
