"""
录音指示器 - Windows版本
使用 Tkinter 实现透明悬浮窗
"""
import tkinter as tk
import threading
from typing import Optional

class RecordingIndicator:
    def __init__(self, width=400, height=120, opacity=0.7):
        self._width = width
        self._height = height
        self._opacity = opacity
        self._root: Optional[tk.Tk] = None
        self._canvas: Optional[tk.Canvas] = None
        self._stop_event = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._thread: Optional[threading.Thread] = None
        self._visible = False
        self._current_text = "正在听..."

    def _create_window(self):
        """在单独的线程中创建窗口"""
        try:
            self._root = tk.Tk()
            
            # 设置窗口属性: 无边框, 顶层, 透明背景
            self._root.overrideredirect(True)
            self._root.attributes('-topmost', True)
            self._root.attributes('-alpha', self._opacity)  # 使用配置的透明度
            # Windows特定: 设置透明色
            self._root.wm_attributes('-transparentcolor', 'white')

            # 居中显示 (偏下)
            screen_width = self._root.winfo_screenwidth()
            screen_height = self._root.winfo_screenheight()
            x = (screen_width - self._width) // 2
            y = screen_height - self._height - 150  # 距离底部 150px
            self._root.geometry(f"{self._width}x{self._height}+{x}+{y}")

            # 创建画布
            self._canvas = tk.Canvas(
                self._root, 
                width=self._width, 
                height=self._height, 
                bg='white', 
                highlightthickness=0
            )
            self._canvas.pack(fill='both', expand=True)

            # 绘制背景和图标
            # 圆角矩形背景 (深色)
            self._draw_rounded_rect(0, 0, self._width, self._height, radius=40, fill='#333333')
            
            # Icon and text scaling based on height (rough heuristic)
            scale = self._height / 120.0
            
            # 麦克风图标 (简单的红色圆点 + 文字)
            dot_size = 40 * scale
            self._dot = self._canvas.create_oval(
                dot_size, dot_size, dot_size * 2, dot_size * 2, 
                fill='#ff4444', outline=''
            )
            
            # 文字提示
            font_size = int(24 * scale)
            self._text = self._canvas.create_text(
                dot_size * 3, dot_size * 1.5, 
                text=self._current_text, 
                fill='#ffffff', 
                anchor='w',
                font=('Microsoft YaHei UI', font_size, 'bold')
            )

            # 如果初始不可见，则隐藏
            if not self._visible:
                self._root.withdraw()

            # 启动事件循环
            self._root.mainloop()
        except Exception as e:
            print(f"Indicator error: {e}")

    def _draw_rounded_rect(self, x1, y1, x2, y2, radius=25, **kwargs):
        """绘制圆角矩形"""
        points = [
            x1 + radius, y1,
            x1 + radius, y1,
            x2 - radius, y1,
            x2 - radius, y1,
            x2, y1,
            x2, y1 + radius,
            x2, y1 + radius,
            x2, y2 - radius,
            x2, y2 - radius,
            x2, y2,
            x2 - radius, y2,
            x2 - radius, y2,
            x1 + radius, y2,
            x1 + radius, y2,
            x1, y2,
            x1, y2 - radius,
            x1, y2 - radius,
            x1, y1 + radius,
            x1, y1 + radius,
            x1, y1
        ]
        return self._canvas.create_polygon(points, **kwargs, smooth=True)

    def start(self):
        """启动指示器线程"""
        if self._thread and self._thread.is_alive():
            return
        
        self._thread = threading.Thread(target=self._create_window, daemon=True)
        self._thread.start()

    def show(self):
        """显示指示器"""
        self._visible = True
        if self._root:
            self._root.after(0, self._root.deiconify)

    def hide(self):
        """隐藏指示器"""
        self._visible = False
        if self._root:
            self._root.after(0, self._root.withdraw)

    def set_text(self, text: str):
        """更新提示文字"""
        self._current_text = text
        if self._root and self._canvas:
            self._root.after(0, lambda: self._canvas.itemconfig(self._text, text=text))

    def destroy(self):
        """销毁指示器"""
        if self._root:
            self._root.after(0, self._root.destroy)
            self._root = None

_indicator = None

def get_indicator(width=400, height=120, opacity=0.7):
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator(width, height, opacity)
        _indicator.start()
    return _indicator
