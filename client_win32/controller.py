"""
核心控制器 - Windows版本
"""
import time
import threading
from typing import Optional, Callable
import keyboard

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from text_inserter import insert_text
from indicator import get_indicator


class HotkeyListener:
    """热键监听器 - 使用keyboard库"""
    
    def __init__(self, modifiers: list, key: str, on_press: Callable, on_release: Callable):
        self.on_press_callback = on_press
        self.on_release_callback = on_release
        self._is_pressed = False
        self._stopped = False
        
        # 构建热键字符串
        mod_parts = []
        for mod in modifiers:
            m = mod.lower()
            if m in ('ctrl', 'control'):
                mod_parts.append('ctrl')
            elif m == 'alt':
                mod_parts.append('alt')
            elif m == 'shift':
                mod_parts.append('shift')
            elif m in ('win', 'windows'):
                mod_parts.append('win')
        
        # 键名标准化
        k = key.lower()
        if k == 'space':
            k = 'space'
        elif len(k) == 1:
            k = k.lower()
        
        self.hotkey_str = '+'.join(mod_parts + [k])
        self.main_key = k
        print(f"热键设置为: {self.hotkey_str}")
    
    def _on_hotkey_press(self):
        if self._stopped or self._is_pressed:
            return
        self._is_pressed = True
        try:
            self.on_press_callback()
        except Exception as e:
            print(f"热键回调错误: {e}")
    
    def _on_hotkey_release(self, event):
        """检测热键释放"""
        if self._stopped or not self._is_pressed:
            return
        
        # 检查是否是主键释放
        if event.event_type == 'up':
            # 检查释放的是否是主键
            if event.name == self.main_key or event.name == self.main_key.upper():
                self._is_pressed = False
                try:
                    self.on_release_callback()
                except Exception as e:
                    print(f"热键释放错误: {e}")
    
    def start(self):
        """启动热键监听"""
        self._stopped = False
        self._is_pressed = False
        
        try:
            # 注册按下事件
            keyboard.add_hotkey(self.hotkey_str, self._on_hotkey_press, suppress=True)
            
            # 注册释放事件监听
            keyboard.on_release_key(self.main_key, self._on_hotkey_release, suppress=False)
            
            print(f"热键监听已启动: {self.hotkey_str}")
        except Exception as e:
            print(f"热键注册失败: {e}")
            raise
    
    def stop(self):
        """停止热键监听"""
        self._stopped = True
        try:
            keyboard.unhook_all()
            print("热键监听已停止")
        except Exception as e:
            print(f"停止热键监听出错: {e}")


class VoiceTyperController:
    """语音输入控制器"""
    
    def __init__(self, config: AppConfig):
        self.config = config
        self._asr_client: Optional[ASRClient] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None
        self._hotkey_listener: Optional[HotkeyListener] = None
        self._recording = False
        self._lock = threading.Lock()
        self._hotwords = get_hotwords_string(config.hotwords)
        
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
        )
        
        # 检查服务状态
        if self._asr_client.health_check():
            log("语音识别服务已连接")
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
    
    def _ensure_indicator(self):
        if self._indicator is None:
            self._indicator = get_indicator(
                width=self.config.ui.width,
                height=self.config.ui.height,
                opacity=self.config.ui.opacity,
            )
    
    def start(self):
        """启动控制器"""
        self._ensure_indicator()
        if self._hotkey_listener:
            self._hotkey_listener.start()
    
    def stop(self):
        """停止控制器"""
        if self._hotkey_listener:
            self._hotkey_listener.stop()
        if self._indicator:
            self._indicator.destroy()
            self._indicator = None
        if self._asr_client:
            self._asr_client.close()
    
    def _update_status(self, status: str):
        if self.on_status_change:
            self.on_status_change(status)
    
    def _on_hotkey_press(self):
        """热键按下"""
        with self._lock:
            if self._recording:
                return
            self._recording = True
        
        self._ensure_indicator()
        self._indicator.show()
        self._recorder.start()
        self._update_status("录音中...")
    
    def _on_hotkey_release(self):
        """热键释放"""
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
                    if text:
                        insert_text(text)
                        self._update_status(f"已输入 ({len(text)}字)")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    self._update_status(f"识别失败: {e}")
                    import traceback
                    traceback.print_exc()
                
                time.sleep(1.5)
                self._update_status("就绪")
            
            threading.Thread(target=do_recognize, daemon=True).start()
        else:
            self._update_status("录音为空")
            time.sleep(1)
            self._update_status("就绪")