"""
录音模块 - 使用 sounddevice 进行音频采集
"""
import numpy as np
import sounddevice as sd
import threading
from typing import Optional, Callable
from queue import Queue
import time


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
        self._chunk_queue: Queue = Queue()
        self._on_chunk_callback: Optional[Callable] = None
        
    @property
    def is_recording(self) -> bool:
        return self._recording
    
    def start(self, on_chunk: Optional[Callable] = None):
        """开始录音
        
        Args:
            on_chunk: 可选的回调函数，用于流式处理，签名: (audio_chunk: np.ndarray) -> None
        """
        if self._recording:
            return
            
        self._recording = True
        self._audio_buffer = []
        self._on_chunk_callback = on_chunk
        
        # 清空队列
        while not self._chunk_queue.empty():
            try:
                self._chunk_queue.get_nowait()
            except:
                pass
        
        def audio_callback(indata, frames, time_info, status):
            if status:
                print(f"录音状态: {status}")
            if self._recording:
                audio_chunk = indata[:, 0].copy()
                with self._lock:
                    self._audio_buffer.append(audio_chunk)
                if self._on_chunk_callback:
                    self._chunk_queue.put(audio_chunk)
        
        self._stream = sd.InputStream(
            samplerate=self.SAMPLE_RATE,
            channels=self.CHANNELS,
            dtype=self.DTYPE,
            callback=audio_callback,
            blocksize=int(self.SAMPLE_RATE * 0.1),  # 100ms 块
        )
        self._stream.start()
    
    def stop(self) -> np.ndarray:
        """停止录音并返回完整音频数据"""
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
    
    def get_audio_buffer(self) -> np.ndarray:
        """获取当前录音缓冲区的副本（不清空）"""
        with self._lock:
            if self._audio_buffer:
                return np.concatenate(self._audio_buffer)
            return np.array([], dtype=self.DTYPE)
    
    def get_chunk(self, timeout: float = 0.1) -> Optional[np.ndarray]:
        """获取一个音频块（用于流式处理）"""
        try:
            return self._chunk_queue.get(timeout=timeout)
        except:
            return None
    
    def get_duration(self) -> float:
        """获取当前录音时长（秒）"""
        with self._lock:
            total_samples = sum(len(chunk) for chunk in self._audio_buffer)
        return total_samples / self.SAMPLE_RATE


def test_microphone():
    """测试麦克风是否可用"""
    try:
        devices = sd.query_devices()
        default_input = sd.query_devices(kind='input')
        print(f"默认输入设备: {default_input['name']}")
        return True
    except Exception as e:
        print(f"麦克风测试失败: {e}")
        return False


if __name__ == "__main__":
    # 简单测试
    if test_microphone():
        print("开始录音测试 (3秒)...")
        recorder = AudioRecorder()
        recorder.start()
        time.sleep(3)
        audio = recorder.stop()
        print(f"录制完成: {len(audio)} 样本, {len(audio)/16000:.2f} 秒")