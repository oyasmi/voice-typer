"""
核心控制器 - 协调各模块工作
"""
import time
import threading
from typing import Optional, Callable
from pynput import keyboard

from config import AppConfig, get_hotwords_string
from recorder import AudioRecorder
from recognizer import SpeechRecognizer, StreamingSession
from text_inserter import insert_text
from indicator import get_indicator


class HotkeyListener:
    """热键监听器"""
    
    # 修饰键映射
    MODIFIER_MAP = {
        'ctrl': keyboard.Key.ctrl,
        'control': keyboard.Key.ctrl,
        'alt': keyboard.Key.alt,
        'option': keyboard.Key.alt,
        'shift': keyboard.Key.shift,
        'cmd': keyboard.Key.cmd,
        'command': keyboard.Key.cmd,
    }
    
    # 特殊键映射
    SPECIAL_KEY_MAP = {
        'space': keyboard.Key.space,
        'tab': keyboard.Key.tab,
        'enter': keyboard.Key.enter,
        'return': keyboard.Key.enter,
        'esc': keyboard.Key.esc,
        'escape': keyboard.Key.esc,
        'backspace': keyboard.Key.backspace,
        'delete': keyboard.Key.delete,
        'up': keyboard.Key.up,
        'down': keyboard.Key.down,
        'left': keyboard.Key.left,
        'right': keyboard.Key.right,
        'f1': keyboard.Key.f1,
        'f2': keyboard.Key.f2,
        'f3': keyboard.Key.f3,
        'f4': keyboard.Key.f4,
        'f5': keyboard.Key.f5,
        'f6': keyboard.Key.f6,
        'f7': keyboard.Key.f7,
        'f8': keyboard.Key.f8,
        'f9': keyboard.Key.f9,
        'f10': keyboard.Key.f10,
        'f11': keyboard.Key.f11,
        'f12': keyboard.Key.f12,
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
        
        # 解析修饰键
        self.required_modifiers = set()
        for mod in modifiers:
            mod_lower = mod.lower()
            if mod_lower in self.MODIFIER_MAP:
                self.required_modifiers.add(self.MODIFIER_MAP[mod_lower])
        
        # 解析主键
        key_lower = key.lower()
        if key_lower in self.SPECIAL_KEY_MAP:
            self.target_key = self.SPECIAL_KEY_MAP[key_lower]
        else:
            self.target_key = keyboard.KeyCode.from_char(key_lower)
        
        self._pressed_modifiers = set()
        self._hotkey_active = False
        self._listener: Optional[keyboard.Listener] = None
    
    def _on_press(self, key):
        """按键按下处理"""
        # 跟踪修饰键
        if key in self.MODIFIER_MAP.values():
            self._pressed_modifiers.add(key)
        
        # 检查是否匹配热键
        if not self._hotkey_active:
            key_match = (key == self.target_key)
            if not key_match:
                # 尝试字符匹配
                try:
                    if hasattr(key, 'char') and key.char:
                        key_match = (key.char.lower() == str(self.target_key).lower())
                except:
                    pass
            
            modifiers_match = self.required_modifiers.issubset(self._pressed_modifiers)
            
            if key_match and modifiers_match:
                self._hotkey_active = True
                try:
                    self.on_press_callback()
                except Exception as e:
                    print(f"热键回调错误: {e}")
    
    def _on_release(self, key):
        """按键释放处理"""
        # 更新修饰键状态
        if key in self.MODIFIER_MAP.values():
            self._pressed_modifiers.discard(key)
        
        # 检查热键释放
        if self._hotkey_active:
            key_match = (key == self.target_key)
            if not key_match:
                try:
                    if hasattr(key, 'char') and key.char:
                        key_match = (key.char.lower() == str(self.target_key).lower())
                except:
                    pass
            
            if key_match:
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
        
        # 组件（延迟初始化）
        self._recognizer: Optional[SpeechRecognizer] = None
        self._recorder: Optional[AudioRecorder] = None
        self._indicator = None  # 延迟到主线程初始化
        self._hotkey_listener: Optional[HotkeyListener] = None
        self._streaming_session: Optional[StreamingSession] = None
        
        # 状态
        self._recording = False
        self._start_time: Optional[float] = None
        self._is_streaming_mode = False
        self._streaming_thread: Optional[threading.Thread] = None
        self._lock = threading.Lock()
        self._initialized = False
        
        # 回调
        self.on_status_change: Optional[Callable[[str], None]] = None
    
    def initialize(self, progress_callback: Optional[Callable[[str], None]] = None):
        """初始化所有组件（在后台线程调用，UI 组件除外）"""
        callback = progress_callback or (lambda x: print(x))
        
        # 初始化识别器
        callback("初始化语音识别引擎...")
        hotwords = get_hotwords_string(self.config.hotwords)
        self._recognizer = SpeechRecognizer(
            model_name=self.config.model.name,
            streaming_model_name=self.config.model.streaming_name,
            punc_model=self.config.model.punc_model,
            device=self.config.model.device,
            hotwords=hotwords,
        )
        self._recognizer.initialize(callback=callback)
        
        # 初始化录音器
        callback("初始化录音设备...")
        self._recorder = AudioRecorder()
        
        # 注意: indicator 将在 start() 中初始化（主线程）
        callback("准备 UI 组件...")
        
        # 初始化热键监听
        callback("初始化热键监听...")
        self._hotkey_listener = HotkeyListener(
            modifiers=self.config.hotkey.modifiers,
            key=self.config.hotkey.key,
            on_press=self._on_hotkey_press,
            on_release=self._on_hotkey_release,
        )
        
        self._initialized = True
        callback("初始化完成！")
    
    def _ensure_indicator(self):
        """确保 indicator 已初始化（在主线程调用）"""
        if self._indicator is None:
            self._indicator = get_indicator(
                width=self.config.ui.width,
                height=self.config.ui.height,
                opacity=self.config.ui.opacity,
            )
    
    def start(self):
        """启动服务（在主线程调用）"""
        # 在主线程初始化 indicator
        self._ensure_indicator()
        
        if self._hotkey_listener:
            self._hotkey_listener.start()
            self._update_status("就绪")
    
    def stop(self):
        """停止服务"""
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
        """热键按下处理"""
        with self._lock:
            if self._recording:
                return
            
            self._recording = True
            self._start_time = time.time()
            self._is_streaming_mode = False
        
        # 显示提示窗口
        self._ensure_indicator()
        if self._indicator:
            self._indicator.show()
        
        # 开始录音
        if self._recorder:
            self._recorder.start()
        self._update_status("录音中...")
        
        # 启动流式检查线程
        self._streaming_thread = threading.Thread(
            target=self._check_streaming_switch,
            daemon=True
        )
        self._streaming_thread.start()
    
    def _check_streaming_switch(self):
        """检查是否需要切换到流式模式"""
        threshold = self.config.streaming.threshold_seconds
        
        while self._recording and not self._is_streaming_mode:
            if self._start_time:
                elapsed = time.time() - self._start_time
                if elapsed >= threshold:
                    self._switch_to_streaming_mode()
                    break
            
            time.sleep(0.1)
    
    def _switch_to_streaming_mode(self):
        """切换到流式识别模式"""
        with self._lock:
            if self._is_streaming_mode:
                return
            self._is_streaming_mode = True
        
        self._update_status("流式识别中...")
        
        if self._indicator:
            self._indicator.set_streaming(True)
        
        # 创建流式会话
        if self._recognizer:
            self._streaming_session = StreamingSession(
                recognizer=self._recognizer,
                chunk_size=self.config.streaming.chunk_size,
                on_result=self._on_streaming_result,
            )
        
        # 启动流式识别线程
        streaming_worker = threading.Thread(
            target=self._streaming_worker,
            daemon=True
        )
        streaming_worker.start()
    
    def _streaming_worker(self):
        """流式识别工作线程"""
        while self._recording and self._is_streaming_mode:
            if self._recorder:
                chunk = self._recorder.get_chunk(timeout=0.1)
                if chunk is not None and self._streaming_session:
                    try:
                        self._streaming_session.feed(chunk)
                    except Exception as e:
                        print(f"流式识别错误: {e}")
    
    def _on_streaming_result(self, text: str):
        """流式识别结果回调"""
        if text:
            # 实时插入识别到的文字
            try:
                insert_text(text, fast=False)
            except Exception as e:
                print(f"文本插入错误: {e}")
    
    def _on_hotkey_release(self):
        """热键释放处理"""
        with self._lock:
            if not self._recording:
                return
            self._recording = False
            was_streaming = self._is_streaming_mode
        
        # 隐藏提示窗口
        if self._indicator:
            self._indicator.hide()
        
        # 停止录音
        audio = None
        if self._recorder:
            audio = self._recorder.stop()
        
        if was_streaming:
            # 流式模式：结束会话，处理剩余音频
            self._update_status("完成流式识别...")
            if self._streaming_session:
                try:
                    final_text = self._streaming_session.finalize()
                    if final_text:
                        insert_text(final_text, fast=False)
                except Exception as e:
                    print(f"流式识别结束错误: {e}")
                self._streaming_session = None
        else:
            # 非流式模式：一次性识别整段音频
            self._update_status("识别中...")
            if audio is not None and len(audio) > 0 and self._recognizer:
                try:
                    text = self._recognizer.recognize(audio)
                    if text:
                        insert_text(text, fast=True)
                        self._update_status(f"已输入: {text[:20]}...")
                    else:
                        self._update_status("未识别到文字")
                except Exception as e:
                    self._update_status(f"识别失败: {e}")
                    print(f"识别错误: {e}")
            else:
                self._update_status("录音为空")
        
        # 重置状态
        with self._lock:
            self._is_streaming_mode = False
            self._streaming_session = None
        
        self._update_status("就绪")


if __name__ == "__main__":
    # 简单测试
    from config import load_config
    
    config = load_config()
    controller = VoiceTyperController(config)
    controller.on_status_change = print
    
    print("正在初始化...")
    controller.initialize(progress_callback=print)
    
    print(f"\n按 {'+'.join(config.hotkey.modifiers)}+{config.hotkey.key} 开始录音")
    print("按 Ctrl+C 退出\n")
    
    controller.start()
    
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n正在退出...")
        controller.stop()