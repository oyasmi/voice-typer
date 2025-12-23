"""
录音提示窗口 - Windows版本
"""
import threading
import time
import tkinter as tk
from typing import Optional


class RecordingIndicator:
    """录音状态提示窗口 - 使用tkinter"""
    
    def __init__(self, width: int = 240, height: int = 70, opacity: float = 0.85):
        self.width = width
        self.height = height
        self.opacity = opacity
        self._window: Optional[tk.Tk] = None
        self._label: Optional[tk.Label] = None
        self._time_label: Optional[tk.Label] = None
        self._visible = False
        self._start_time = None
        self._lock = threading.Lock()
        self._update_thread = None
        self._should_update = False
    
    def _create_window(self):
        """创建窗口（必须在主线程调用）"""
        if self._window is not None:
            return
        
        try:
            self._window = tk.Tk()
            self._window.title("Recording")
            
            # 窗口设置
            self._window.overrideredirect(True)  # 无边框
            self._window.attributes('-topmost', True)  # 置顶
            self._window.attributes('-alpha', self.opacity)  # 透明度
            
            # 设置窗口大小和位置
            screen_width = self._window.winfo_screenwidth()
            x = (screen_width - self.width) // 2
            y = 120
            self._window.geometry(f"{self.width}x{self.height}+{x}+{y}")
            
            # 深色背景
            self._window.configure(bg='#2E2E2E')
            
            # 创建主容器框架
            container = tk.Frame(self._window, bg='#2E2E2E')
            container.place(x=0, y=0, width=self.width, height=self.height)
            
            # 标签 - 录音状态
            self._label = tk.Label(
                container,
                text="🎤 录音中...",
                font=("Microsoft YaHei UI", 14, "bold"),
                fg="white",
                bg="#2E2E2E"
            )
            self._label.pack(pady=(15, 5))
            
            # 时间标签
            self._time_label = tk.Label(
                container,
                text="0.0s",
                font=("Microsoft YaHei UI", 10),
                fg="#AAAAAA",
                bg="#2E2E2E"
            )
            self._time_label.pack()
            
            # 初始隐藏
            self._window.withdraw()
            
            # 处理窗口事件
            self._window.protocol("WM_DELETE_WINDOW", self._on_closing)
            
        except Exception as e:
            print(f"创建窗口失败: {e}")
            import traceback
            traceback.print_exc()
    
    def _on_closing(self):
        """窗口关闭事件"""
        self.hide()
    
    def _update_time_loop(self):
        """更新时间显示的循环"""
        while self._should_update:
            with self._lock:
                if not self._visible:
                    break
                start = self._start_time
            
            if start and self._time_label and self._window:
                try:
                    elapsed = time.time() - start
                    # 使用 after 方法在主线程更新 UI
                    self._window.after(0, lambda e=elapsed: self._update_time_label(e))
                except Exception as e:
                    print(f"更新时间失败: {e}")
                    break
            
            time.sleep(0.1)
        
        self._should_update = False
    
    def _update_time_label(self, elapsed: float):
        """更新时间标签（在主线程调用）"""
        try:
            if self._time_label:
                self._time_label.config(text=f"{elapsed:.1f}s")
        except:
            pass
    
    def show(self):
        """显示窗口"""
        with self._lock:
            if self._visible:
                return
            self._visible = True
            self._start_time = time.time()
            self._should_update = True
        
        # 如果窗口不存在，创建它
        if self._window is None:
            self._create_window()
        
        if self._window is None:
            print("警告: 窗口创建失败，无法显示")
            return
        
        try:
            # 重置显示
            if self._label:
                self._label.config(text="🎤 录音中...")
            if self._time_label:
                self._time_label.config(text="0.0s")
            
            # 显示窗口
            self._window.deiconify()
            self._window.lift()
            self._window.attributes('-topmost', True)
            
            # 启动更新线程
            if self._update_thread is None or not self._update_thread.is_alive():
                self._update_thread = threading.Thread(target=self._update_time_loop, daemon=True)
                self._update_thread.start()
        except Exception as e:
            print(f"显示窗口失败: {e}")
    
    def hide(self):
        """隐藏窗口"""
        with self._lock:
            self._visible = False
            self._start_time = None
            self._should_update = False
        
        if self._window:
            try:
                self._window.withdraw()
            except Exception as e:
                print(f"隐藏窗口失败: {e}")
    
    def set_status(self, text: str):
        """设置状态文本"""
        if self._label and self._window:
            try:
                self._window.after(0, lambda: self._label.config(text=text))
            except Exception as e:
                print(f"设置状态失败: {e}")
    
    def destroy(self):
        """销毁窗口"""
        with self._lock:
            self._visible = False
            self._should_update = False
        
        if self._window:
            try:
                self._window.quit()
                self._window.destroy()
            except Exception as e:
                print(f"销毁窗口失败: {e}")
            finally:
                self._window = None
    
    def update(self):
        """更新窗口（用于事件循环）"""
        if self._window:
            try:
                self._window.update()
            except Exception as e:
                print(f"更新窗口失败: {e}")


_indicator = None


def get_indicator(width=240, height=70, opacity=0.85):
    """获取全局indicator实例"""
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator(width, height, opacity)
    return _indicator