"""
核心控制器 (Linux Wayland)
协调录音、识别、文本插入等组件
"""
import time
import threading
import logging
from typing import Optional, Callable

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from hotkey_listener import HotkeyListener
from text_inserter import insert_text
from indicator import get_indicator

logger = logging.getLogger('VoiceTyper')


class VoiceTyperController:
    """语音输入控制器"""

    def __init__(self, config: AppConfig):
        """
        初始化控制器

        Args:
            config: 应用配置
        """
        self.config = config
        self._asr_client: Optional[ASRClient] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None
        self._hotkey_listener: Optional[HotkeyListener] = None
        self._recording = False
        self._lock = threading.Lock()
        self._hotwords = get_hotwords_string(config.hotword_files)

        self._input_count = 0
        self._char_count = 0

        self.on_status_change: Optional[Callable[[str], None]] = None
        self.on_stats_change: Optional[Callable[[], None]] = None

    def initialize(self, callback: Optional[Callable[[str], None]] = None):
        """
        初始化所有组件

        Args:
            callback: 状态更新回调函数
        """
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

        # 初始化热键监听器
        log("初始化热键监听...")
        self._hotkey_listener = HotkeyListener(
            modifiers=self.config.hotkey.modifiers,
            key=self.config.hotkey.key,
            on_press=self._on_hotkey_press,
            on_release=self._on_hotkey_release,
        )

        # 预初始化文本插入器（创建 UInput 设备）
        log("初始化虚拟键盘设备...")
        from text_inserter import initialize
        initialize()
        log("初始化完成")

    def _ensure_indicator(self):
        """确保录音指示器已创建"""
        if self._indicator is None:
            self._indicator = get_indicator(
                width=self.config.ui.width,
                height=self.config.ui.height,
                opacity=self.config.ui.opacity,
            )

    def start(self):
        """启动热键监听"""
        self._ensure_indicator()
        if self._hotkey_listener:
            self._hotkey_listener.start()
            self._update_status("就绪")

    def stop(self):
        """停止热键监听"""
        if self._hotkey_listener:
            self._hotkey_listener.stop()
        if self._indicator:
            self._indicator.destroy()
            self._indicator = None
        if self._asr_client:
            self._asr_client.close()
        self._update_status("已停止")

    def _update_status(self, status: str):
        """
        更新状态

        Args:
            status: 状态文本
        """
        if self.on_status_change:
            try:
                self.on_status_change(status)
            except Exception as e:
                logger.error(f"状态更新回调错误: {e}")

    def get_stats_display(self) -> str:
        """Get formatted statistics for display"""
        chars = self._char_count
        if chars >= 10000:
            chars_str = f"{chars/10000:.1f}万字"
        else:
            chars_str = f"{chars}字"
        return f"已输入：{chars_str}（{self._input_count}次）"

    def _on_hotkey_press(self):
        """热键按下处理"""
        with self._lock:
            if self._recording:
                return
            self._recording = True

        self._ensure_indicator()
        self._indicator.show()
        self._recorder.start()
        self._update_status("录音中...")

    def _on_hotkey_release(self):
        """热键释放处理"""
        with self._lock:
            if not self._recording:
                return
            self._recording = False

        self._indicator.hide()
        audio = self._recorder.stop()

        if len(audio) > 0:
            self._update_status("识别中...")

            def do_recognize():
                try:
                    text = self._asr_client.recognize(audio, self._hotwords)
                    if text and text.strip():
                        insert_text(text)
                        logger.info(f"识别: {text}")

                        # Track statistics
                        self._input_count += 1
                        self._char_count += len(text)
                        if self.on_stats_change:
                            self.on_stats_change()

                        self._update_status(f"已输入 ({len(text)}字)")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    logger.error(f"识别失败: {e}")
                    self._update_status(f"识别失败: {e}")

                time.sleep(1.5)
                self._update_status("就绪")

            threading.Thread(target=do_recognize, daemon=True).start()
        else:
            self._update_status("录音为空")
            time.sleep(1)
            self._update_status("就绪")
