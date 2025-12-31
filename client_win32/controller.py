"""
核心控制器 - 兼容 macOS 和 Windows
"""
import time
import threading
import platform
from typing import Optional, Callable, Set
from pynput import keyboard

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from text_inserter import insert_text
from indicator import get_indicator


class HotkeyListener:
    """热键监听器 - 跨平台兼容"""
    
    # 检测当前操作系统
    IS_MACOS = platform.system() == 'Darwin'
    IS_WINDOWS = platform.system() == 'Windows'
    
    MODIFIER_KEYS = {
        'ctrl': {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r},
        'alt': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'option': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'shift': {keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r},
        'cmd': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
        'command': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
        'win': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
        'windows': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
    }
    
    SPECIAL_KEYS = {
        'space': keyboard.Key.space,
        'tab': keyboard.Key.tab,
        'enter': keyboard.Key.enter,
        'f1': keyboard.Key.f1, 'f2': keyboard.Key.f2, 'f3': keyboard.Key.f3,
        'f4': keyboard.Key.f4, 'f5': keyboard.Key.f5, 'f6': keyboard.Key.f6,
        'f7': keyboard.Key.f7, 'f8': keyboard.Key.f8, 'f9': keyboard.Key.f9,
        'f10': keyboard.Key.f10, 'f11': keyboard.Key.f11, 'f12': keyboard.Key.f12,
    }
    
    def __init__(self, modifiers: list, key: str, on_press: Callable, on_release: Callable):
        self.on_press_callback = on_press
        self.on_release_callback = on_release
        
        # 解析修饰键
        self.required_modifiers: Set[str] = set()
        for mod in modifiers:
            m = mod.lower()
            if m in ('ctrl', 'control'):
                self.required_modifiers.add('ctrl')
            elif m in ('alt', 'option'):
                self.required_modifiers.add('alt')
            elif m == 'shift':
                self.required_modifiers.add('shift')
            elif m in ('cmd', 'command', 'win', 'windows'):
                # 统一使用 'super' 作为内部标识
                # macOS 上是 Command 键，Windows 上是 Windows 键
                self.required_modifiers.add('super')
        
        # 解析主键
        k = key.lower()
        self.target_key = self.SPECIAL_KEYS.get(k, keyboard.KeyCode.from_char(k))
        
        self._pressed_keys: Set = set()
        self._hotkey_active = False
        self._listener = None
        self._stopped = False
    
    def _get_pressed_modifiers(self) -> Set[str]:
        """获取当前按下的修饰键"""
        mods = set()
        for key in self._pressed_keys:
            for name, keys in self.MODIFIER_KEYS.items():
                if key in keys:
                    if name in ('ctrl', 'control'):
                        mods.add('ctrl')
                    elif name in ('alt', 'option'):
                        mods.add('alt')
                    elif name == 'shift':
                        mods.add('shift')
                    elif name in ('cmd', 'command', 'win', 'windows'):
                        mods.add('super')
        return mods
    
    def _is_target_key(self, key) -> bool:
        """判断是否为目标按键"""
        if key == self.target_key:
            return True
        try:
            if hasattr(key, 'char') and key.char:
                target_char = str(self.target_key).replace("'", "").lower()
                return key.char.lower() == target_char
        except AttributeError:
            pass
        return False
    
    def _on_press(self, key):
        """按键按下事件"""
        if self._stopped:
            return False
        
        self._pressed_keys.add(key)
        
        if not self._hotkey_active:
            if self._get_pressed_modifiers() == self.required_modifiers and self._is_target_key(key):
                self._hotkey_active = True
                try:
                    self.on_press_callback()
                except Exception as e:
                    print(f"热键回调错误: {e}")
    
    def _on_release(self, key):
        """按键释放事件"""
        if self._stopped:
            return False
        
        self._pressed_keys.discard(key)
        
        if self._hotkey_active and self._is_target_key(key):
            self._hotkey_active = False
            try:
                self.on_release_callback()
            except Exception as e:
                print(f"热键释放错误: {e}")
    
    def start(self):
        """启动监听器"""
        self._stopped = False
        self._listener = keyboard.Listener(on_press=self._on_press, on_release=self._on_release)
        self._listener.start()
    
    def stop(self):
        """停止监听器"""
        self._stopped = True
        if self._listener:
            self._listener.stop()
            self._listener = None


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
        
        # 显示当前平台信息
        system_name = platform.system()
        log(f"当前操作系统: {system_name}")
        
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
        hotkey_desc = '+'.join(self.config.hotkey.modifiers + [self.config.hotkey.key])
        log(f"初始化热键监听: {hotkey_desc}")
        self._hotkey_listener = HotkeyListener(
            modifiers=self.config.hotkey.modifiers,
            key=self.config.hotkey.key,
            on_press=self._on_hotkey_press,
            on_release=self._on_hotkey_release,
        )
        
        log("初始化完成")
    
    def _ensure_indicator(self):
        """确保指示器已创建"""
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
        """更新状态"""
        if self.on_status_change:
            self.on_status_change(status)
    
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
                    if text:
                        insert_text(text)
                        self._update_status(f"已输入 ({len(text)}字)")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    self._update_status(f"识别失败: {e}")
                
                time.sleep(1.5)
                self._update_status("就绪")
            
            threading.Thread(target=do_recognize, daemon=True).start()
        else:
            self._update_status("录音为空")
            time.sleep(1)
            self._update_status("就绪")