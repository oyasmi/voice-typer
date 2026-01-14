"""
录音指示器 (Linux Wayland)
使用 GTK4 + gtk-layer-shell 实现浮动窗口
"""
import threading
import time
import gi

gi.require_version('Gtk', '4.0')
gi.require_version('GtkLayerShell', '1.0')

from gi.repository import Gtk, GLib, Gdk
from gi.repository import GtkLayerShell as LayerShell


class RecordingIndicator:
    """录音指示器窗口"""

    def __init__(self, width: int = 240, height: int = 70, opacity: float = 0.85):
        """
        初始化录音指示器

        Args:
            width: 窗口宽度
            height: 窗口高度
            opacity: 窗口透明度 (0.0 - 1.0)
        """
        self.width = width
        self.height = height
        self.opacity = opacity

        self.window: Optional[Gtk.Window] = None
        self.status_label: Optional[Gtk.Label] = None
        self.time_label: Optional[Gtk.Label] = None

        self.visible = False
        self.start_time = None
        self.lock = threading.Lock()
        self.timer_thread = None

    def _create_window(self):
        """创建指示器窗口"""
        if self.window is not None:
            return

        # 创建窗口
        self.window = Gtk.Window()
        self.window.set_default_size(self.width, self.height)
        self.window.set_decorated(False)  # 无标题栏
        self.window.set_resizable(False)

        # 检查并初始化 Layer Shell (Wayland 特性)
        if LayerShell.is_supported():
            LayerShell.init_for_window(self.window)
            LayerShell.set_layer(self.window, LayerShell.Layer.OVERLAY)
            # 锚定到顶部
            LayerShell.set_anchor(self.window, LayerShell.Edge.TOP, True)
            LayerShell.set_anchor(self.window, LayerShell.Edge.BOTTOM, False)
            LayerShell.set_anchor(self.window, LayerShell.Edge.LEFT, False)
            LayerShell.set_anchor(self.window, LayerShell.Edge.RIGHT, False)
            # 设置边距以居中
            LayerShell.set_margin(self.window, LayerShell.Edge.TOP, 100)
            # 键盘交互模式
            LayerShell.set_keyboard_mode(
                self.window,
                LayerShell.KeyboardMode.ON_DEMAND
            )

        # 设置透明度
        self.window.set_opacity(self.opacity)

        # 加载 CSS 样式
        self._load_css()

        # 创建内容容器
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        box.set_margin_start(16)
        box.set_margin_end(16)
        box.set_margin_top(12)
        box.set_margin_bottom(12)
        box.set_valign(Gtk.Align.CENTER)
        box.set_halign(Gtk.Align.CENTER)

        # 状态标签
        self.status_label = Gtk.Label(label="🎤 录音中...")
        self.status_label.add_css_class("status-label")
        self.status_label.set_halign(Gtk.Align.CENTER)
        box.append(self.status_label)

        # 时间标签
        self.time_label = Gtk.Label(label="0.0s")
        self.time_label.add_css_class("time-label")
        self.time_label.set_halign(Gtk.Align.CENTER)
        box.append(self.time_label)

        # 设置窗口内容
        self.window.set_child(box)

        # 连接关闭信号
        self.window.connect('close-request', self._on_close)

    def _load_css(self):
        """加载 CSS 样式"""
        css = b"""
            window {
                background-color: rgba(30, 30, 30, 0.95);
                border-radius: 12px;
                border: 1px solid rgba(255, 255, 255, 0.1);
            }

            .status-label {
                color: white;
                font-size: 16px;
                font-weight: 500;
            }

            .time-label {
                color: rgba(255, 255, 255, 0.7);
                font-size: 12px;
                font-family: monospace;
            }
        """

        style_provider = Gtk.CssProvider()
        style_provider.load_from_data(css)

        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(),
            style_provider,
            Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )

    def _on_close(self, window):
        """处理窗口关闭事件"""
        self.hide()
        return True  # 阻止默认关闭行为

    def show(self):
        """显示录音指示器"""
        with self.lock:
            if self.visible:
                return
            self.visible = True
            self.start_time = time.time()

        # 在 GTK 主线程中创建和显示窗口
        GLib.idle_add(self._show_on_main_thread)

        # 启动计时器更新线程
        if self.timer_thread is None or not self.timer_thread.is_alive():
            self.timer_thread = threading.Thread(
                target=self._update_timer_loop,
                daemon=True
            )
            self.timer_thread.start()

    def _show_on_main_thread(self):
        """在主线程中显示窗口"""
        self._create_window()
        if self.window:
            self.status_label.set_label("🎤 录音中...")
            self.time_label.set_label("0.0s")
            self.window.present()

    def hide(self):
        """隐藏录音指示器"""
        with self.lock:
            self.visible = False
            self.start_time = None

        if self.window:
            GLib.idle_add(self._hide_on_main_thread)

    def _hide_on_main_thread(self):
        """在主线程中隐藏窗口"""
        if self.window:
            self.window.hide()

    def _update_timer_loop(self):
        """计时器更新循环（在独立线程中运行）"""
        while True:
            with self.lock:
                if not self.visible:
                    break
                start = self.start_time

            if start and self.time_label:
                elapsed = time.time() - start
                GLib.idle_add(
                    lambda: self.time_label.set_label(f"{elapsed:.1f}s")
                )

            time.sleep(0.1)  # 每 100ms 更新一次

    def destroy(self):
        """销毁窗口并清理资源"""
        with self.lock:
            self.visible = False
            self.start_time = None

        if self.window:
            GLib.idle_add(self._destroy_on_main_thread)
            self.window = None

    def _destroy_on_main_thread(self):
        """在主线程中销毁窗口"""
        if self.window:
            self.window.close()
            self.window = None
        self.status_label = None
        self.time_label = None


# 全局单例
_indicator: Optional[RecordingIndicator] = None


def get_indicator(width: int = 240, height: int = 70, opacity: float = 0.85) -> RecordingIndicator:
    """获取录音指示器实例（全局函数）"""
    global _indicator
    if _indicator is None:
        _indicator = RecordingIndicator(width, height, opacity)
    return _indicator


def destroy_indicator():
    """销毁录音指示器（全局函数）"""
    global _indicator
    if _indicator:
        _indicator.destroy()
        _indicator = None
