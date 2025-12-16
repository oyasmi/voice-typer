"""
录音模块
"""
import numpy as np
import sounddevice as sd
import threading
from typing import Optional


class AudioRecorder:
    """音频录制器"""
    
    SAMPLE_RATE = 16000  # FunASR 要求 16kHz
    CHANNELS = 1
    DTYPE = np.float32
    
    def __init__(self):
        self._recording = False
        self._audio_buffer: list = []
        self._stream: Optional[sd.InputStream] = None
        self._lock = threading.Lock()
    
    @property
    def is_recording(self) -> bool:
        return self._recording
    
    def start(self):
        """开始录音"""
        if self._recording:
            return
        
        self._recording = True
        self._audio_buffer = []
        
        def callback(indata, frames, time_info, status):
            if status:
                print(f"录音状态: {status}")
            if self._recording:
                with self._lock:
                    self._audio_buffer.append(indata[:, 0].copy())
        
        self._stream = sd.InputStream(
            samplerate=self.SAMPLE_RATE,
            channels=self.CHANNELS,
            dtype=self.DTYPE,
            callback=callback,
            blocksize=int(self.SAMPLE_RATE * 0.1),  # 100ms
        )
        self._stream.start()
    
    def stop(self) -> np.ndarray:
        """停止录音并返回音频数据"""
        self._recording = False
        
        if self._stream:
            self._stream.stop()
            self._stream.close()
            self._stream = None
        
        with self._lock:
            if self._audio_buffer:
                audio = np.concatenate(self._audio_buffer)
            else:
                audio = np.array([], dtype=self.DTYPE)
            self._audio_buffer = []
        
        return audio
    
    def get_duration(self) -> float:
        """获取当前录音时长（秒）"""
        with self._lock:
            total_samples = sum(len(chunk) for chunk in self._audio_buffer)
        return total_samples / self.SAMPLE_RATE