"""
录音指示器 - Windows版本
使用 Tkinter 实现透明悬浮窗
"""
import tkinter as tk
import threading
from typing import Optional

class RecordingIndicator:
    def __init__(self, width=200, height=60):
        self._width = width
        self._height = height
        self._root: Optional[tk.Tk] = None
        self._canvas: Optional[tk.Canvas] = None
        self._stop_event = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._visible = False

    def _create_window(self):
        """在单独的线程中创建窗口"""
        try:
            self._root = tk.Tk()
            
            # 设置窗口属性: 无边框, 顶层, 透明背景
            self._root.overrideredirect(True)
            self._root.attributes('-topmost', True)
            self._root.attributes('-alpha', 0.9)
            # Windows特定: 设置透明色
            self._root.wm_attributes('-transparentcolor', 'white')

            # 居中显示 (偏下)
            screen_width = self._root.winfo_screenwidth()
            screen_height = self._root.winfo_screenheight()
            x = (screen_width - self._width) // 2
            y = screen_height - self._height - 100  # 距离底部 100px
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
            self._draw_rounded_rect(0, 0, self._width, self._height, radius=20, fill='#333333')
            
            # 麦克风图标 (简单的红色圆点 + 文字)
            # 录音状态：红色圆点闪烁效果由 update 循环处理
            self._dot = self._canvas.create_oval(
                20, 20, 40, 40, 
                fill='#ff4444', outline=''
            )
            
            # 文字提示
            self._text = self._canvas.create_text(
                60, 30, 
                text="正在听...", 
                fill='#ffffff', 
                anchor='w',
                font=('Microsoft YaHei UI', 12, 'bold')
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

    def destroy(self):
        """销毁指示器"""
        if self._root:
            self._root.after(0, self._root.destroy)
            self._root = None

_indicator = None

def get_indicator():
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator()
        _indicator.start()
    return _indicator
