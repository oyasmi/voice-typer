"""
录音提示窗口
"""
import threading
import time
from typing import Optional, Callable

try:
    import objc
    from AppKit import (
        NSApplication, NSWindow, NSView, NSColor, NSFont,
        NSTextField, NSMakeRect, NSScreen,
        NSWindowStyleMaskBorderless, NSBackingStoreBuffered,
        NSFloatingWindowLevel, NSTextAlignmentCenter, NSApp,
    )
    from Foundation import NSObject
    HAS_PYOBJC = True
    
    class _MainThreadHelper(NSObject):
        def initWithCallback_(self, callback):
            self = objc.super(_MainThreadHelper, self).init()
            if self:
                self._callback = callback
            return self
        
        def execute_(self, _):
            if self._callback:
                try:
                    self._callback()
                except:
                    pass

except ImportError:
    HAS_PYOBJC = False


def run_on_main_thread(func: Callable, wait: bool = False):
    if not HAS_PYOBJC:
        func()
        return
    if threading.current_thread() is threading.main_thread():
        func()
        return
    helper = _MainThreadHelper.alloc().initWithCallback_(func)
    helper.performSelectorOnMainThread_withObject_waitUntilDone_('execute:', None, wait)


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
        self._start_time = None
        self._lock = threading.Lock()
        self._initialized = False
    
    def _create_window(self):
        if self._initialized or not HAS_PYOBJC:
            return
        
        if NSApp is None:
            NSApplication.sharedApplication()
        
        screen = NSScreen.mainScreen()
        x = (screen.frame().size.width - self.width) / 2 if screen else 400
        y = 120
        
        rect = NSMakeRect(x, y, self.width, self.height)
        self._window = NSWindow.alloc().initWithContentRect_styleMask_backing_defer_(
            rect, NSWindowStyleMaskBorderless, NSBackingStoreBuffered, False
        )
        self._window.setLevel_(NSFloatingWindowLevel)
        self._window.setAlphaValue_(self.opacity)
        self._window.setOpaque_(False)
        self._window.setBackgroundColor_(NSColor.clearColor())
        self._window.setHasShadow_(True)
        
        content = NSView.alloc().initWithFrame_(NSMakeRect(0, 0, self.width, self.height))
        content.setWantsLayer_(True)
        content.layer().setBackgroundColor_(NSColor.colorWithWhite_alpha_(0.18, 0.95).CGColor())
        content.layer().setCornerRadius_(12)
        
        self._label = NSTextField.alloc().initWithFrame_(NSMakeRect(0, 28, self.width, 30))
        self._label.setStringValue_("🎤 录音中...")
        self._label.setBezeled_(False)
        self._label.setDrawsBackground_(False)
        self._label.setEditable_(False)
        self._label.setSelectable_(False)
        self._label.setAlignment_(NSTextAlignmentCenter)
        self._label.setFont_(NSFont.systemFontOfSize_(16))
        self._label.setTextColor_(NSColor.whiteColor())
        
        self._time_label = NSTextField.alloc().initWithFrame_(NSMakeRect(0, 8, self.width, 20))
        self._time_label.setStringValue_("0s")
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
        while True:
            with self._lock:
                if not self._visible:
                    break
                start = self._start_time
            
            if start and self._time_label:
                text = f"{int(time.time() - start)}s"
                label = self._time_label
                run_on_main_thread(lambda: label.setStringValue_(text), wait=False)

            time.sleep(1.0)
    
    def show(self):
        if not HAS_PYOBJC:
            print("🎤 录音中...")
            return
        
        with self._lock:
            if self._visible:
                return
            self._visible = True
            self._start_time = time.time()
        
        def _show():
            self._create_window()
            if self._window:
                self._label.setStringValue_("🎤 录音中...")
                self._time_label.setStringValue_("0s")
                self._window.orderFront_(None)
        
        run_on_main_thread(_show, wait=True)
        threading.Thread(target=self._update_time_loop, daemon=True).start()
    
    def hide(self):
        with self._lock:
            self._visible = False
            self._start_time = None
        
        if not HAS_PYOBJC:
            return
        
        run_on_main_thread(lambda: self._window.orderOut_(None) if self._window else None)
    
    def set_status(self, text: str):
        if not HAS_PYOBJC or not self._label:
            return
        run_on_main_thread(lambda: self._label.setStringValue_(text))
    
    def destroy(self):
        with self._lock:
            self._visible = False
        if self._window:
            run_on_main_thread(lambda: self._window.close() if self._window else None)
            self._window = None
            self._initialized = False


_indicator = None

def get_indicator(width=240, height=70, opacity=0.85):
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator(width, height, opacity)
    return _indicator