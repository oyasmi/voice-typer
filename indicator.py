"""
录音提示窗口模块 - 使用 PyObjC 原生实现
"""
import threading
import time
from typing import Optional, Callable

try:
    import objc
    from AppKit import (
        NSApplication,
        NSWindow,
        NSView,
        NSColor,
        NSFont,
        NSTextField,
        NSMakeRect,
        NSScreen,
        NSWindowStyleMaskBorderless,
        NSBackingStoreBuffered,
        NSFloatingWindowLevel,
        NSTextAlignmentCenter,
        NSApp,
    )
    from Foundation import NSObject
    HAS_PYOBJC = True
    
    class _MainThreadHelper(NSObject):
        """主线程执行辅助类"""
        
        def initWithCallback_(self, callback):
            self = objc.super(_MainThreadHelper, self).init()
            if self is None:
                return None
            self._callback = callback
            return self
        
        def execute_(self, _):
            if self._callback:
                try:
                    self._callback()
                except Exception as e:
                    print(f"主线程执行错误: {e}")

except ImportError:
    HAS_PYOBJC = False
    print("警告: PyObjC 未安装，提示窗口不可用")


def run_on_main_thread(func: Callable, wait: bool = False):
    """在主线程执行函数"""
    if not HAS_PYOBJC:
        func()
        return
    
    if threading.current_thread() is threading.main_thread():
        func()
        return
    
    helper = _MainThreadHelper.alloc().initWithCallback_(func)
    helper.performSelectorOnMainThread_withObject_waitUntilDone_(
        'execute:', None, wait
    )


class RecordingIndicator:
    """录音状态提示窗口"""
    
    def __init__(self, width: int = 240, height: int = 70, opacity: float = 0.85):
        self.width = width
        self.height = height
        self.opacity = opacity
        
        self._window = None
        self._label = None
        self._time_label = None
        self._visible = False
        self._start_time: Optional[float] = None
        self._update_thread: Optional[threading.Thread] = None
        self._lock = threading.Lock()
        self._initialized = False
    
    def _create_window(self):
        """创建窗口（必须在主线程调用）"""
        if self._initialized or not HAS_PYOBJC:
            return
        
        if NSApp is None:
            NSApplication.sharedApplication()
        
        # 窗口位置（屏幕底部居中）
        screen = NSScreen.mainScreen()
        if screen:
            screen_frame = screen.frame()
            x = (screen_frame.size.width - self.width) / 2
        else:
            x = 400
        y = 120
        
        # 创建窗口
        rect = NSMakeRect(x, y, self.width, self.height)
        self._window = NSWindow.alloc().initWithContentRect_styleMask_backing_defer_(
            rect, NSWindowStyleMaskBorderless, NSBackingStoreBuffered, False
        )
        self._window.setLevel_(NSFloatingWindowLevel)
        self._window.setAlphaValue_(self.opacity)
        self._window.setOpaque_(False)
        self._window.setBackgroundColor_(NSColor.clearColor())
        self._window.setHasShadow_(True)
        
        # 内容视图
        content = NSView.alloc().initWithFrame_(NSMakeRect(0, 0, self.width, self.height))
        content.setWantsLayer_(True)
        content.layer().setBackgroundColor_(NSColor.colorWithWhite_alpha_(0.18, 0.95).CGColor())
        content.layer().setCornerRadius_(12)
        
        # 状态标签
        self._label = NSTextField.alloc().initWithFrame_(NSMakeRect(0, 28, self.width, 30))
        self._label.setStringValue_("🎤 录音中...")
        self._label.setBezeled_(False)
        self._label.setDrawsBackground_(False)
        self._label.setEditable_(False)
        self._label.setSelectable_(False)
        self._label.setAlignment_(NSTextAlignmentCenter)
        self._label.setFont_(NSFont.systemFontOfSize_(16))
        self._label.setTextColor_(NSColor.whiteColor())
        
        # 时间标签
        self._time_label = NSTextField.alloc().initWithFrame_(NSMakeRect(0, 8, self.width, 20))
        self._time_label.setStringValue_("0.0s")
        self._time_label.setBezeled_(False)
        self._time_label.setDrawsBackground_(False)
        self._time_label.setEditable_(False)
        self._time_label.setSelectable_(False)
        self._time_label.setAlignment_(NSTextAlignmentCenter)
        self._time_label.setFont_(NSFont.systemFontOfSize_(12))
        self._time_label.setTextColor_(NSColor.lightGrayColor())
        
        content.addSubview_(self._label)
        content.addSubview_(self._time_label)
        self._window.setContentView_(content)
        self._initialized = True
    
    def _update_time_loop(self):
        """更新时间显示"""
        while True:
            with self._lock:
                if not self._visible:
                    break
                start = self._start_time
            
            if start and self._time_label:
                elapsed = time.time() - start
                text = f"{elapsed:.1f}s"
                label = self._time_label
                
                def update():
                    try:
                        label.setStringValue_(text)
                    except:
                        pass
                
                run_on_main_thread(update, wait=False)
            
            time.sleep(0.1)
    
    def show(self):
        """显示窗口"""
        if not HAS_PYOBJC:
            print("\n🎤 开始录音...")
            self._start_time = time.time()
            return
        
        with self._lock:
            if self._visible:
                return
            self._visible = True
            self._start_time = time.time()
        
        def _show():
            self._create_window()
            if self._window:
                if self._label:
                    self._label.setStringValue_("🎤 录音中...")
                if self._time_label:
                    self._time_label.setStringValue_("0.0s")
                self._window.orderFront_(None)
        
        run_on_main_thread(_show, wait=True)
        
        # 启动时间更新线程
        self._update_thread = threading.Thread(target=self._update_time_loop, daemon=True)
        self._update_thread.start()
    
    def hide(self):
        """隐藏窗口"""
        with self._lock:
            was_visible = self._visible
            start = self._start_time
            self._visible = False
            self._start_time = None
        
        if not HAS_PYOBJC:
            if was_visible and start:
                print(f"🎤 录音结束 ({time.time() - start:.1f}s)")
            return
        
        def _hide():
            if self._window:
                self._window.orderOut_(None)
        
        run_on_main_thread(_hide, wait=False)
    
    def set_status(self, text: str):
        """设置状态文本"""
        if not HAS_PYOBJC:
            print(f"📢 {text}")
            return
        
        label = self._label
        
        def _set():
            if label:
                label.setStringValue_(text)
        
        run_on_main_thread(_set, wait=False)
    
    def destroy(self):
        """销毁窗口"""
        with self._lock:
            self._visible = False
        
        if self._window:
            def _close():
                self._window.close()
            run_on_main_thread(_close, wait=False)
            self._window = None
            self._initialized = False


# 单例
_indicator = None


def get_indicator(width: int = 240, height: int = 70, opacity: float = 0.85):
    """获取提示窗口单例"""
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator(width, height, opacity)
    return _indicator