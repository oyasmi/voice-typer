"""
核心控制器 - Windows版本
"""
import time
import threading
from typing import Optional, Callable

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from text_inserter import insert_text
from hotkey_listener import HotkeyListener


class VoiceTyperController:
    """语音输入控制器"""

    def __init__(self, config: AppConfig):
        self.config = config
        self._asr_client: Optional[ASRClient] = None
        self._recorder: Optional[AudioRecorder] = None
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

        # 停止状态更新定时器
        if self._status_update_timer:
            self._status_update_timer.cancel()
            self._status_update_timer = None

    def _update_status(self, status: str):
        """更新状态"""
        if self.on_status_change:
            self.on_status_change(status)

    def _start_recording_timer(self):
        """启动录音时长更新定时器"""
        def update_recording_time():
            if self._recording and self._recording_start_time:
                elapsed = int(time.time() - self._recording_start_time)
                self._update_status(f"录音中... ({elapsed}s)")

                # 继续下一次更新
                self._status_update_timer = threading.Timer(1.0, update_recording_time)
                self._status_update_timer.daemon = True
                self._status_update_timer.start()

        self._status_update_timer = threading.Timer(1.0, update_recording_time)
        self._status_update_timer.daemon = True
        self._status_update_timer.start()

    def _stop_recording_timer(self):
        """停止录音时长更新定时器"""
        if self._status_update_timer:
            self._status_update_timer.cancel()
            self._status_update_timer = None

    def _on_hotkey_press(self):
        """热键按下 - 开始录音"""
        with self._lock:
            if self._recording:
                return
            self._recording = True

        self._recording_start_time = time.time()
        self._recorder.start()
        self._update_status("录音中... (0s)")
        self._start_recording_timer()

    def _on_hotkey_release(self):
        """热键释放 - 停止录音并识别"""
        with self._lock:
            if not self._recording:
                return
            self._recording = False

        self._stop_recording_timer()
        audio = self._recorder.stop()

        if len(audio) > 0:
            self._update_status("识别中...")

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
            self._update_status("录音为空")
            time.sleep(1)
            self._update_status("就绪")
