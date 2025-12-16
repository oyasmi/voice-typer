"""
录音提示窗口模块 - 使用 PyObjC 原生实现
不依赖 tkinter，避免 macOS Tcl/Tk 兼容性问题
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
    
    # 定义一个全局的 MainThreadExecutor 类（只定义一次）
    class _MainThreadHelper(NSObject):
        """用于在主线程执行任务的辅助类"""
        
        def initWithCallback_(self, callback):
            self = objc.super(_MainThreadHelper, self).init()
            if self is None:
                return None
            self._callback = callback
            return self
        
        def execute_(self, _):
            """在主线程执行回调"""
            if self._callback:
                try:
                    self._callback()
                except Exception as e:
                    print(f"主线程执行错误: {e}")

except ImportError:
    HAS_PYOBJC = False
    print("警告: PyObjC 未安装，提示窗口将不可用")
    print("请运行: pip install pyobjc-framework-Cocoa")


def run_on_main_thread(func: Callable, wait: bool = False):
    """在主线程执行函数
    
    Args:
        func: 要执行的函数
        wait: 是否等待执行完成
    """
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
    """录音状态提示窗口 - PyObjC 原生实现"""
    
    def __init__(
        self,
        width: int = 240,
        height: int = 70,
        opacity: float = 0.85,
    ):
        self.width = width
        self.height = height
        self.opacity = opacity
        
        self._window = None
        self._label = None
        self._time_label = None
        self._content_view = None
        self._visible = False
        self._start_time: Optional[float] = None
        self._update_running = False
        self._is_streaming = False
        self._lock = threading.Lock()
        self._initialized = False
    
    def _create_window(self):
        """创建窗口（必须在主线程调用）"""
        if self._initialized or not HAS_PYOBJC:
            return
        
        # 确保 NSApplication 已初始化
        if NSApp is None:
            NSApplication.sharedApplication()
        
        # 计算窗口位置（屏幕中下方）
        screen = NSScreen.mainScreen()
        if screen:
            screen_frame = screen.frame()
            x = (screen_frame.size.width - self.width) / 2
        else:
            x = 400
        y = 120  # 距离底部的距离
        
        # 创建窗口
        rect = NSMakeRect(x, y, self.width, self.height)
        style = NSWindowStyleMaskBorderless
        self._window = NSWindow.alloc().initWithContentRect_styleMask_backing_defer_(
            rect,
            style,
            NSBackingStoreBuffered,
            False,
        )
        
        # 窗口属性
        self._window.setLevel_(NSFloatingWindowLevel)
        self._window.setAlphaValue_(self.opacity)
        self._window.setOpaque_(False)
        self._window.setBackgroundColor_(NSColor.clearColor())
        self._window.setHasShadow_(True)
        
        # 创建内容视图（带圆角背景）
        self._content_view = NSView.alloc().initWithFrame_(
            NSMakeRect(0, 0, self.width, self.height)
        )
        self._content_view.setWantsLayer_(True)
        self._content_view.layer().setBackgroundColor_(
            NSColor.colorWithWhite_alpha_(0.18, 0.95).CGColor()
        )
        self._content_view.layer().setCornerRadius_(12)
        
        # 状态标签
        self._label = NSTextField.alloc().initWithFrame_(
            NSMakeRect(0, 28, self.width, 30)
        )
        self._label.setStringValue_("🎤 录音中...")
        self._label.setBezeled_(False)
        self._label.setDrawsBackground_(False)
        self._label.setEditable_(False)
        self._label.setSelectable_(False)
        self._label.setAlignment_(NSTextAlignmentCenter)
        self._label.setFont_(NSFont.systemFontOfSize_(16))
        self._label.setTextColor_(NSColor.whiteColor())
        
        # 时间标签
        self._time_label = NSTextField.alloc().initWithFrame_(
            NSMakeRect(0, 8, self.width, 20)
        )
        self._time_label.setStringValue_("0.0s")
        self._time_label.setBezeled_(False)
        self._time_label.setDrawsBackground_(False)
        self._time_label.setEditable_(False)
        self._time_label.setSelectable_(False)
        self._time_label.setAlignment_(NSTextAlignmentCenter)
        self._time_label.setFont_(NSFont.systemFontOfSize_(12))
        self._time_label.setTextColor_(NSColor.lightGrayColor())
        
        # 添加标签到视图
        self._content_view.addSubview_(self._label)
        self._content_view.addSubview_(self._time_label)
        
        self._window.setContentView_(self._content_view)
        self._initialized = True
    
    def _update_time_loop(self):
        """更新录音时间显示（后台线程）"""
        while self._update_running:
            with self._lock:
                if not self._visible:
                    break
                start_time = self._start_time
                is_streaming = self._is_streaming
            
            if start_time and self._time_label:
                elapsed = time.time() - start_time
                time_text = f"{elapsed:.1f}s"
                if is_streaming:
                    time_text += " [流式]"
                
                # 捕获变量用于闭包
                text_to_set = time_text
                label = self._time_label
                
                def update():
                    try:
                        if label:
                            label.setStringValue_(text_to_set)
                    except:
                        pass
                
                run_on_main_thread(update, wait=False)
            
            time.sleep(0.1)
    
    def show(self):
        """显示提示窗口"""
        if not HAS_PYOBJC:
            print("\n🎤 开始录音...")
            return
        
        with self._lock:
            if self._visible:
                return
            self._visible = True
            self._start_time = time.time()
            self._is_streaming = False
            self._update_running = True
        
        def _do_show():
            self._create_window()
            if self._window:
                if self._label:
                    self._label.setStringValue_("🎤 录音中...")
                if self._time_label:
                    self._time_label.setStringValue_("0.0s")
                self._window.orderFront_(None)
        
        run_on_main_thread(_do_show, wait=True)
        
        # 启动时间更新线程
        update_thread = threading.Thread(target=self._update_time_loop, daemon=True)
        update_thread.start()
    
    def hide(self):
        """隐藏提示窗口"""
        with self._lock:
            was_visible = self._visible
            start_time = self._start_time
            self._visible = False
            self._update_running = False
            self._start_time = None
        
        if not HAS_PYOBJC:
            if was_visible and start_time:
                elapsed = time.time() - start_time
                print(f"🎤 录音结束 ({elapsed:.1f}s)")
            return
        
        def _do_hide():
            if self._window:
                self._window.orderOut_(None)
        
        run_on_main_thread(_do_hide, wait=False)
    
    def set_streaming(self, is_streaming: bool = True):
        """设置流式识别状态"""
        with self._lock:
            self._is_streaming = is_streaming
        
        if not HAS_PYOBJC:
            if is_streaming:
                print("🎤 切换到流式识别模式...")
            return
        
        label = self._label
        
        def _do_set():
            if label:
                text = "🎤 流式识别中..." if is_streaming else "🎤 录音中..."
                label.setStringValue_(text)
        
        run_on_main_thread(_do_set, wait=False)
    
    def set_status(self, text: str):
        """设置状态文本"""
        if not HAS_PYOBJC:
            print(f"📢 {text}")
            return
        
        label = self._label
        
        def _do_set():
            if label:
                label.setStringValue_(text)
        
        run_on_main_thread(_do_set, wait=False)
    
    def destroy(self):
        """销毁窗口"""
        with self._lock:
            self._update_running = False
            self._visible = False
        
        if not HAS_PYOBJC:
            return
        
        window = self._window
        
        def _do_destroy():
            if window:
                window.close()
        
        run_on_main_thread(_do_destroy, wait=False)
        
        self._window = None
        self._initialized = False


# 简单的回退方案
class SimpleIndicator:
    """简单的终端提示（回退方案）"""
    
    def __init__(self, **kwargs):
        self._visible = False
        self._start_time = None
        self._is_streaming = False
    
    def show(self):
        self._visible = True
        self._start_time = time.time()
        print("\n🎤 开始录音...")
    
    def hide(self):
        if self._visible and self._start_time:
            elapsed = time.time() - self._start_time
            print(f"🎤 录音结束 ({elapsed:.1f}s)")
        self._visible = False
    
    def set_streaming(self, is_streaming: bool = True):
        self._is_streaming = is_streaming
        if is_streaming:
            print("🎤 切换到流式识别模式...")
    
    def set_status(self, text: str):
        print(f"📢 {text}")
    
    def destroy(self):
        pass


# 单例
_indicator = None


def get_indicator(
    width: int = 240,
    height: int = 70,
    opacity: float = 0.85,
):
    """获取提示窗口单例"""
    global _indicator
    if _indicator is None:
        if HAS_PYOBJC:
            _indicator = RecordingIndicator(width, height, opacity)
        else:
            _indicator = SimpleIndicator(width=width, height=height, opacity=opacity)
    return _indicator


if __name__ == "__main__":
    # 测试
    print("测试录音提示窗口...")
    print("将在 2 秒后显示窗口")
    time.sleep(2)
    
    indicator = get_indicator()
    
    print("显示窗口")
    indicator.show()
    
    print("等待 3 秒...")
    time.sleep(3)
    
    print("切换到流式模式")
    indicator.set_streaming(True)
    
    print("等待 3 秒...")
    time.sleep(3)
    
    print("隐藏窗口")
    indicator.hide()
    
    print("测试完成")