"""
核心控制器 - Windows版本
"""
import time
import threading
from typing import Optional, Callable

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from indicator import get_indicator

class VoiceTyperController:
    """语音输入控制器"""

    def __init__(self, config: AppConfig):
        self.config = config
        self._asr_client: Optional[ASRClient] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None  # 指示器实例
        self._hotkey_listener: Optional[HotkeyListener] = None
        self._recording = False
        self._lock = threading.Lock()
        self._hotwords = get_hotwords_string(config.hotwords)
        self._recording_start_time = None
        self._status_update_timer = None

        # 状态更新回调 - 由系统托盘UI设置
        self.on_status_change: Optional[Callable[[str], None]] = None

    def initialize(self, callback: Optional[Callable[[str], None]] = None):
        """初始化"""
        log = callback or print

        # 初始化 ASR 客户端
        log("连接语音识别服务...")
        self._asr_client = ASRClient(
            host=self.config.server.host,
            port=self.config.server.port,
            timeout=self.config.server.timeout,
            api_key=self.config.server.api_key,
            llm_recorrect=self.config.server.llm_recorrect,
        )

        # 检查服务状态
        if self._asr_client.health_check():
            llm_status = "（LLM修正: 已启用）" if self.config.server.llm_recorrect else ""
            log(f"语音识别服务已连接 {llm_status}")
        else:
            log("警告: 语音识别服务未就绪")

        # 初始化录音器
        log("初始化录音设备...")
        self._recorder = AudioRecorder()

        # 初始化指示器
        log("初始化UI...")
        self._indicator = get_indicator()

        # 初始化热键
        log("初始化热键监听...")
        self._hotkey_listener = HotkeyListener(
            modifiers=self.config.hotkey.modifiers,
            key=self.config.hotkey.key,
            on_press=self._on_hotkey_press,
            on_release=self._on_hotkey_release,
        )

        log("初始化完成")

    def start(self):
        """启动控制器"""
        if self._hotkey_listener:
            self._hotkey_listener.start()

    def stop(self):
        """停止控制器"""
        if self._hotkey_listener:
            self._hotkey_listener.stop()
        if self._asr_client:
            self._asr_client.close()
        if self._recorder:
            self._recorder.close()
        if self._indicator:
            self._indicator.destroy()

        # 停止状态更新定时器
        if self._status_update_timer:
            # self._status_update_timer.join() # 不等待，避免阻塞
            self._status_update_timer = None

    def _update_status(self, status: str):
        """更新状态"""
        if self.on_status_change:
            # 放入线程池或异步执行，避免阻塞录音线程
            threading.Thread(target=self.on_status_change, args=(status,), daemon=True).start()

    def _start_recording_timer(self):
        """启动录音时长更新定时器"""
        self._status_update_timer = threading.Thread(target=self._run_timer_loop, daemon=True)
        self._status_update_timer.start()

    def _run_timer_loop(self):
        """定时器循环"""
        while self._recording and self._recording_start_time:
            elapsed = int(time.time() - self._recording_start_time)
            # 这里的状态更新会触发UI刷新，频率不宜过高
            self._update_status(f"录音中... ({elapsed}s)")
            
            # 使用简单的 sleep，每秒更新一次
            for _ in range(10): # Check every 0.1s to allow fast exit
                if not self._recording:
                    return
                time.sleep(0.1)

    def _stop_recording_timer(self):
        """停止录音时长更新定时器"""
        self._status_update_timer = None

    def _on_hotkey_press(self):
        """热键按下 - 开始录音"""
        # 1. 极致优化：最先处理标志位和录音启动
        with self._lock:
            if self._recording:
                return
            self._recording = True
        
        self._recording_start_time = time.time()
        
        # 2. 立即启动录音 (recorder 内部已经预初始化了 stream，start 很快)
        self._recorder.start()
        
        # 3. 显示 UI (稍微延后，不阻塞录音)
        if self._indicator:
            self._indicator.show()
            
        # 4. 更新托盘状态和定时器 (放在最后/异步)
        self._start_recording_timer()

    def _on_hotkey_release(self):
        """热键释放 - 停止录音并识别"""
        with self._lock:
            if not self._recording:
                return
            self._recording = False

        # 1. 立即获取音频 (最优先)
        audio = self._recorder.stop()
        
        # 2. 隐藏指示器
        if self._indicator:
            self._indicator.hide()
            
        # 3. 停止定时器
        self._stop_recording_timer()

        # 4. 异步处理识别
        if len(audio) > 0:
            self._update_status("识别中...") # 异步更新

            def do_recognize():
                try:
                    text = self._asr_client.recognize(audio, self._hotwords)
                    if text:
                        insert_text(text)
                        self._update_status(f"已输入 ({len(text)}字)")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    self._update_status(f"识别失败: {e}")

                # 1.5秒后恢复就绪状态
                time.sleep(1.5)
                self._update_status("就绪")

            threading.Thread(target=do_recognize, daemon=True).start()
        else:
            self._update_status("录音为空") # 异步更新
            
            def reset_status():
                time.sleep(1)
                self._update_status("就绪")
            threading.Thread(target=reset_status, daemon=True).start()
