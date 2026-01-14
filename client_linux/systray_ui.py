"""
系统托盘 UI (Linux Wayland)
使用 AppIndicator + GTK 实现系统托盘图标
"""
import subprocess
from typing import TYPE_CHECKING

from gi.repository import Gtk, GLib
from gi.repository import AyatanaAppIndicator3 as AppIndicator

if TYPE_CHECKING:
    from config import AppConfig


class VoiceTyperTray:
    """VoiceTyper 系统托盘图标"""

    def __init__(self, config: 'AppConfig', controller):
        """
        初始化系统托盘

        Args:
            config: 应用配置
            controller: 控制器实例（需要有 start/stop 方法）
        """
        self.config = config
        self.controller = controller
        self.enabled = False

        # 创建 AppIndicator
        self.indicator = AppIndicator.Indicator.new(
            "voicetyper",
            "voicetyper",  # 图标名称（需要安装到系统主题）
            AppIndicator.IndicatorCategory.APPLICATION_STATUS
        )
        self.indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
        self.indicator.set_attention_icon("voicetyper-recording")

        # 创建菜单
        self.menu = self._create_menu()
        self.indicator.set_menu(self.menu)

        # 保存菜单项引用以便更新
        self.status_item = None
        self.toggle_item = None

    def _create_menu(self) -> Gtk.Menu:
        """创建系统托盘菜单"""
        menu = Gtk.Menu()

        # 状态项（不可点击，仅显示）
        self.status_item = Gtk.MenuItem(label="状态: 初始化中...")
        self.status_item.set_sensitive(False)
        menu.append(self.status_item)

        # 分隔符
        menu.append(Gtk.SeparatorMenuItem())

        # 启用/禁用切换
        self.toggle_item = Gtk.MenuItem(label="启用语音输入")
        self.toggle_item.connect("activate", self._on_toggle)
        menu.append(self.toggle_item)

        # 分隔符
        menu.append(Gtk.SeparatorMenuItem())

        # 配置菜单项
        config_item = Gtk.MenuItem(label="打开配置文件")
        config_item.connect("activate", self._on_open_config)
        menu.append(config_item)

        # 词库文件
        hotwords_item = Gtk.MenuItem(label="打开词库文件")
        hotwords_item.connect("activate", self._on_open_hotwords)
        menu.append(hotwords_item)

        # 分隔符
        menu.append(Gtk.SeparatorMenuItem())

        # 关于
        about_item = Gtk.MenuItem(label="关于")
        about_item.connect("activate", self._on_about)
        menu.append(about_item)

        # 退出
        quit_item = Gtk.MenuItem(label="退出")
        quit_item.connect("activate", self._on_quit)
        menu.append(quit_item)

        menu.show_all()
        return menu

    def _on_toggle(self, _):
        """切换启用/禁用状态"""
        if self.enabled:
            self.enabled = False
            if hasattr(self.controller, 'stop'):
                self.controller.stop()
            self.toggle_item.set_label("启用语音输入")
            self.set_status("已禁用")
        else:
            self.enabled = True
            if hasattr(self.controller, 'start'):
                self.controller.start()
            self.toggle_item.set_label("禁用语音输入")
            self.set_status("就绪")

    def _on_open_config(self, _):
        """打开配置文件"""
        try:
            config_path = self.config.get_config_path() if hasattr(self.config, 'get_config_path') else None
            if config_path and config_path.exists():
                subprocess.run(['xdg-open', str(config_path)], check=True)
            else:
                self._show_error_dialog(
                    "配置文件不存在",
                    f"配置文件路径: {config_path}"
                )
        except Exception as e:
            print(f"打开配置文件失败: {e}")

    def _on_open_hotwords(self, _):
        """打开词库文件"""
        try:
            from config import get_default_hotwords_path
            hotwords_path = get_default_hotwords_path()
            subprocess.run(['xdg-open', str(hotwords_path)], check=True)
        except Exception as e:
            print(f"打开词库文件失败: {e}")

    def _on_about(self, _):
        """显示关于对话框"""
        dialog = Gtk.AboutDialog()
        dialog.set_program_name("VoiceTyper")
        dialog.set_version("1.2.0")
        dialog.set_comments("Linux Wayland 语音输入客户端")
        dialog.set_website("https://github.com/yourusername/voice-typer")
        dialog.set_license("GPLv3")
        dialog.run()
        dialog.destroy()

    def _on_quit(self, _):
        """退出应用"""
        self.enabled = False
        if hasattr(self.controller, 'stop'):
            self.controller.stop()
        Gtk.main_quit()

    def _show_error_dialog(self, title: str, message: str):
        """显示错误对话框"""
        dialog = Gtk.MessageDialog(
            message_type=Gtk.MessageType.ERROR,
            buttons=Gtk.ButtonsType.OK,
            text=title
        )
        dialog.format_secondary_text(message)
        dialog.run()
        dialog.destroy()

    def set_status(self, status: str):
        """
        更新状态文本

        Args:
            status: 状态文本
        """
        if self.status_item:
            GLib.idle_add(lambda: self.status_item.set_label(f"状态: {status}"))

        # 根据状态更新图标
        if "录音" in status:
            GLib.idle_add(lambda: self.indicator.set_status(AppIndicator.IndicatorStatus.ATTENTION))
        else:
            GLib.idle_add(lambda: self.indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE))

    def set_enabled(self, enabled: bool):
        """
        设置启用状态

        Args:
            enabled: 是否启用
        """
        self.enabled = enabled
        if self.toggle_item:
            label = "禁用语音输入" if enabled else "启用语音输入"
            GLib.idle_add(lambda: self.toggle_item.set_label(label))

    def quit(self):
        """退出应用"""
        self._on_quit(None)
