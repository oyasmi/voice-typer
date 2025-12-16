"""
核心控制器
"""
import time
import threading
from typing import Optional, Callable, Set
from pynput import keyboard

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from recognizer import SpeechRecognizer
from text_inserter import insert_text
from indicator import get_indicator


class HotkeyListener:
    """热键监听器 - 支持多修饰键组合"""
    
    # 修饰键映射
    MODIFIER_KEYS = {
        'ctrl': {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r},
        'control': {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r},
        'alt': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'option': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'shift': {keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r},
        'cmd': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
        'command': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
    }
    
    # 所有修饰键集合
    ALL_MODIFIERS = set()
    for keys in MODIFIER_KEYS.values():
        ALL_MODIFIERS.update(keys)
    
    # 特殊键映射
    SPECIAL_KEYS = {
        'space': keyboard.Key.space,
        'tab': keyboard.Key.tab,
        'enter': keyboard.Key.enter,
        'return': keyboard.Key.enter,
        'esc': keyboard.Key.esc,
        'escape': keyboard.Key.esc,
        'f1': keyboard.Key.f1, 'f2': keyboard.Key.f2, 'f3': keyboard.Key.f3,
        'f4': keyboard.Key.f4, 'f5': keyboard.Key.f5, 'f6': keyboard.Key.f6,
        'f7': keyboard.Key.f7, 'f8': keyboard.Key.f8, 'f9': keyboard.Key.f9,
        'f10': keyboard.Key.f10, 'f11': keyboard.Key.f11, 'f12': keyboard.Key.f12,
    }
    
    def __init__(
        self,
        modifiers: list[str],
        key: str,
        on_press: Callable,
        on_release: Callable,
    ):
        self.on_press_callback = on_press
        self.on_release_callback = on_release
        
        # 解析需要的修饰键类型
        self.required_modifier_types: Set[str] = set()
        for mod in modifiers:
            mod_lower = mod.lower()
            if mod_lower in ('ctrl', 'control'):
                self.required_modifier_types.add('ctrl')
            elif mod_lower in ('alt', 'option'):
                self.required_modifier_types.add('alt')
            elif mod_lower in ('shift',):
                self.required_modifier_types.add('shift')
            elif mod_lower in ('cmd', 'command'):
                self.required_modifier_types.add('cmd')
        
        # 解析主键
        key_lower = key.lower()
        if key_lower in self.SPECIAL_KEYS:
            self.target_key = self.SPECIAL_KEYS[key_lower]
        else:
            self.target_key = keyboard.KeyCode.from_char(key_lower)
        
        self._pressed_keys: Set = set()
        self._hotkey_active = False
        self._listener: Optional[keyboard.Listener] = None
    
    def _get_pressed_modifier_types(self) -> Set[str]:
        """获取当前按下的修饰键类型"""
        types = set()
        for key in self._pressed_keys:
            if key in self.MODIFIER_KEYS.get('ctrl', set()):
                types.add('ctrl')
            elif key in self.MODIFIER_KEYS.get('alt', set()):
                types.add('alt')
            elif key in self.MODIFIER_KEYS.get('shift', set()):
                types.add('shift')
            elif key in self.MODIFIER_KEYS.get('cmd', set()):
                types.add('cmd')
        return types
    
    def _is_target_key(self, key) -> bool:
        """检查是否是目标主键"""
        if key == self.target_key:
            return True
        try:
            if hasattr(key, 'char') and key.char:
                return key.char.lower() == str(self.target_key).replace("'", "")
        except:
            pass
        return False
    
    def _on_press(self, key):
        """按键按下"""
        self._pressed_keys.add(key)
        
        if not self._hotkey_active:
            pressed_types = self._get_pressed_modifier_types()
            if pressed_types == self.required_modifier_types and self._is_target_key(key):
                self._hotkey_active = True
                try:
                    self.on_press_callback()
                except Exception as e:
                    print(f"热键按下回调错误: {e}")
    
    def _on_release(self, key):
        """按键释放"""
        self._pressed_keys.discard(key)
        
        if self._hotkey_active and self._is_target_key(key):
            self._hotkey_active = False
            try:
                self.on_release_callback()
            except Exception as e:
                print(f"热键释放回调错误: {e}")
    
    def start(self):
        """开始监听"""
        self._listener = keyboard.Listener(
            on_press=self._on_press,
            on_release=self._on_release,
        )
        self._listener.start()
    
    def stop(self):
        """停止监听"""
        if self._listener:
            self._listener.stop()
            self._listener = None


class VoiceTyperController:
    """语音输入控制器"""
    
    def __init__(self, config: AppConfig):
        self.config = config
        self._recognizer: Optional[SpeechRecognizer] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None
        self._hotkey_listener: Optional[HotkeyListener] = None
        self._recording = False
        self._lock = threading.Lock()
        
        self.on_status_change: Optional[Callable[[str], None]] = None
    
    def initialize(self, callback: Optional[Callable[[str], None]] = None):
        """初始化"""
        log = callback or print
        total_start = time.time()
        
        # 初始化录音器
        log("初始化录音设备...")
        t0 = time.time()
        self._recorder = AudioRecorder()
        log(f"  录音设备就绪，耗时 {time.time() - t0:.1f}s")
        
        # 初始化识别器
        log("初始化语音识别引擎...")
        hotwords = get_hotwords_string(self.config.hotwords)
        self._recognizer = SpeechRecognizer(
            model_name=self.config.model.name,
            punc_model=self.config.model.punc_model,
            device=self.config.model.device,
            hotwords=hotwords,
        )
        self._recognizer.initialize(callback=log)
        
        # 初始化热键监听
        log("初始化热键监听...")
        t0 = time.time()
        self._hotkey_listener = HotkeyListener(
            modifiers=self.config.hotkey.modifiers,
            key=self.config.hotkey.key,
            on_press=self._on_hotkey_press,
            on_release=self._on_hotkey_release,
        )
        log(f"  热键监听就绪，耗时 {time.time() - t0:.1f}s")
        
        log(f"初始化完成！总耗时 {time.time() - total_start:.1f}s")
    
    def _ensure_indicator(self):
        """确保 indicator 已创建"""
        if self._indicator is None:
            self._indicator = get_indicator(
                width=self.config.ui.width,
                height=self.config.ui.height,
                opacity=self.config.ui.opacity,
            )
    
    def start(self):
        """启动"""
        self._ensure_indicator()
        if self._hotkey_listener:
            self._hotkey_listener.start()
            self._update_status("就绪")
    
    def stop(self):
        """停止"""
        if self._hotkey_listener:
            self._hotkey_listener.stop()
        if self._indicator:
            self._indicator.destroy()
            self._indicator = None
        self._update_status("已停止")
    
    def _update_status(self, status: str):
        """更新状态"""
        if self.on_status_change:
            self.on_status_change(status)
    
    def _on_hotkey_press(self):
        """热键按下 - 开始录音"""
        with self._lock:
            if self._recording:
                return
            self._recording = True
        
        self._ensure_indicator()
        if self._indicator:
            self._indicator.show()
        
        if self._recorder:
            self._recorder.start()
        
        self._update_status("录音中...")
    
    def _on_hotkey_release(self):
        """热键释放 - 停止录音并识别"""
        with self._lock:
            if not self._recording:
                return
            self._recording = False
        
        # 隐藏提示
        if self._indicator:
            self._indicator.hide()
        
        # 停止录音
        audio = None
        if self._recorder:
            audio = self._recorder.stop()
        
        # 识别
        if audio is not None and len(audio) > 0:
            self._update_status("识别中...")
            
            # 在后台线程识别，避免阻塞
            def do_recognize():
                try:
                    text = self._recognizer.recognize(audio)
                    if text:
                        insert_text(text)
                        self._update_status(f"已输入 ({len(text)}字)")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    self._update_status(f"识别失败: {e}")
                    print(f"识别错误: {e}")
                
                # 延迟后恢复状态
                time.sleep(1.5)
                self._update_status("就绪")
            
            threading.Thread(target=do_recognize, daemon=True).start()
        else:
            self._update_status("录音为空")
            time.sleep(1)
            self._update_status("就绪")