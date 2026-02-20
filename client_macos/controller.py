"""
核心控制器
"""
import time
import threading
import logging
from typing import Optional, Callable, Set, Any
from pynput import keyboard

try:
    import Quartz
except ImportError:
    Quartz = None

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from asr_client import ASRClient
from text_inserter import insert_text
from indicator import get_indicator

logger = logging.getLogger('VoiceTyper')


class HotkeyListener:
    """热键监听器"""
    
    MODIFIER_KEYS = {
        'ctrl': {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r},
        'alt': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'option': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'shift': {keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r},
        'cmd': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
        'command': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},
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
            elif m in ('cmd', 'command'):
                self.required_modifiers.add('cmd')
        
        # 解析主键
        k = key.lower()
        self.target_key = self.SPECIAL_KEYS.get(k, keyboard.KeyCode.from_char(k))
        
        self._pressed_keys: Set = set()
        self._hotkey_active = False
        self._listener = None
        self._stopped = False
    
    def _get_pressed_modifiers(self) -> Set[str]:
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
                    elif name in ('cmd', 'command'):
                        mods.add('cmd')
        return mods
    
    def _is_target_key(self, key) -> bool:
        if key == self.target_key:
            return True
        try:
            if hasattr(key, 'char') and key.char:
                return key.char.lower() == str(self.target_key).replace("'", "")
        except:
            pass
        return False
    
    def _on_press(self, key):
        if self._stopped:
            return False
        self._pressed_keys.add(key)
        
        if not self._hotkey_active:
            if self._get_pressed_modifiers() == self.required_modifiers and self._is_target_key(key):
                self._hotkey_active = True
                try:
                    self.on_press_callback()
                except Exception as e:
                    logger.error(f"热键回调错误: {e}")
    
    def _on_release(self, key):
        if self._stopped:
            return False
        self._pressed_keys.discard(key)
        
        if self._hotkey_active and self._is_target_key(key):
            self._hotkey_active = False
            try:
                self.on_release_callback()
            except Exception as e:
                logger.error(f"热键释放错误: {e}")
    
    def start(self):
        self._stopped = False
        self._listener = keyboard.Listener(on_press=self._on_press, on_release=self._on_release)
        self._listener.start()
    
    def stop(self):
        self._stopped = True
        if self._listener:
            self._listener.stop()
            self._listener = None


class FnKeyListener:
    """Fn键监听器 (监听长按地球仪/Fn键)"""
    
    def __init__(self, on_press: Callable, on_release: Callable):
        self.on_press_callback = on_press
        self.on_release_callback = on_release
        
        self._hotkey_active = False
        self._stopped = False
        self._tap = None
        self._runLoopSource = None
        self._runLoop = None
        self._lock = threading.Lock()
        
    def _event_tap_callback(self, proxy, event_type, event, refcon):
        if self._stopped:
            return event
            
        if Quartz and event_type == Quartz.kCGEventFlagsChanged:
            flags = Quartz.CGEventGetFlags(event)
            is_fn = bool(flags & Quartz.kCGEventFlagMaskSecondaryFn)
            
            with self._lock:
                if is_fn and not self._hotkey_active:
                    self._hotkey_active = True
                    try:
                        self.on_press_callback()
                    except Exception as e:
                        logger.error(f"Fn热键回调错误: {e}")
                elif not is_fn and self._hotkey_active:
                    self._hotkey_active = False
                    try:
                        self.on_release_callback()
                    except Exception as e:
                        logger.error(f"Fn热键释放错误: {e}")
        return event
        
    def _run_listener(self):
        if not Quartz:
            logger.error("未安装 Quartz，无法监听Fn键！")
            return

        self._tap = Quartz.CGEventTapCreate(
            Quartz.kCGSessionEventTap,
            Quartz.kCGHeadInsertEventTap,
            Quartz.kCGEventTapOptionListenOnly,
            Quartz.CGEventMaskBit(Quartz.kCGEventFlagsChanged),
            self._event_tap_callback,
            None
        )
        
        if not self._tap:
            logger.error("无法监听Fn键：请在'系统偏好设置 -> 隐私与安全性 -> 辅助功能'中授予权限")
            return
            
        self._runLoopSource = Quartz.CFMachPortCreateRunLoopSource(None, self._tap, 0)
        self._runLoop = Quartz.CFRunLoopGetCurrent()
        
        Quartz.CFRunLoopAddSource(self._runLoop, self._runLoopSource, Quartz.kCFRunLoopCommonModes)
        Quartz.CGEventTapEnable(self._tap, True)
        
        Quartz.CFRunLoopRun()
        
    def start(self):
        self._stopped = False
        threading.Thread(target=self._run_listener, daemon=True).start()
        
    def stop(self):
        self._stopped = True
        if Quartz:
            if self._tap:
                Quartz.CGEventTapEnable(self._tap, False)
            if self._runLoop:
                try:
                    Quartz.CFRunLoopStop(self._runLoop)
                except Exception:
                    pass


class VoiceTyperController:
    """语音输入控制器"""
    
    def __init__(self, config: AppConfig):
        self.config = config
        self._asr_client: Optional[ASRClient] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None
        self._hotkey_listener: Optional[Any] = None
        self._recording = False
        self._lock = threading.Lock()
        self._hotwords = get_hotwords_string(config.hotwords)
        
        self.on_status_change: Optional[Callable[[str], None]] = None
        self._input_count = 0
        self._char_count = 0
        self.on_stats_change: Optional[Callable[[], None]] = None
    
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
        if self.config.hotkey.key.lower() == 'fn':
            self._hotkey_listener = FnKeyListener(
                on_press=self._on_hotkey_press,
                on_release=self._on_hotkey_release,
            )
        else:
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
        self._ensure_indicator()
        if self._hotkey_listener:
            self._hotkey_listener.start()
    
    def stop(self):
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

    def get_stats_display(self) -> str:
        """Get formatted stats for display"""
        chars = self._char_count
        if chars >= 10000:
            chars_str = f"{chars/10000:.1f}万字"
        else:
            chars_str = f"{chars}字"
        return f"已输入：{chars_str}（{self._input_count}次）"

    def _on_hotkey_press(self):
        with self._lock:
            if self._recording:
                return
            self._recording = True
        
        self._ensure_indicator()
        self._indicator.show()
        self._recorder.start()
        self._update_status("录音中...")
    
    def _on_hotkey_release(self):
        with self._lock:
            if not self._recording:
                return
            self._recording = False
        
        self._indicator.hide()
        audio = self._recorder.stop()
        
        if len(audio) > 4800:
            self._update_status("识别中...")

            def do_recognize():
                try:
                    text = self._asr_client.recognize(audio, self._hotwords)
                    # 检查文本非空且非纯空格
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
            def reset_status():
                if len(audio) > 0:
                    self._update_status("录音过短")
                else:
                    self._update_status("录音为空")
                time.sleep(1)
                self._update_status("就绪")
            threading.Thread(target=reset_status, daemon=True).start()
