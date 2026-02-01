#!/usr/bin/env python3
"""
VoiceTyper Windows 客户端
"""
import sys
import time
import threading
import subprocess
import os
import logging
from pathlib import Path

import pystray
from PIL import Image, ImageDraw, ImageFont

from config import (
    load_config, get_config_path, get_config_dir,
    get_default_hotwords_path, ensure_default_files,
    AppConfig, APP_NAME, APP_VERSION,
)
from controller import VoiceTyperController


class VoiceTyperApp:
    """系统托盘应用"""

    def __init__(self):
        self.logger = logging.getLogger(APP_NAME)
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False
        self._current_status = "初始化中..."

        try:
            from ctypes import windll
            windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass  # Fail gracefully on older Windows versions

        # 创建系统托盘图标
        self.icon = self._create_icon()
        
        # 修复状态栏更新问题：使用 lambda 动态获取文本
        # 注意: pystray 的 Text 属性不直接支持 lambda，但我们可以通过 update_menu 来刷新
        # 或者使用 checked 属性来作为状态指示，但 title 更直观。
        # 实际上 pystray.MenuItem 的 text 参数如果传入 callable，它会在显示时调用
        
        self.tray_icon = pystray.Icon(
            name=APP_NAME,
            icon=self.icon,
            title=APP_NAME,
            menu=self._build_menu()
        )

        # 启动异步初始化
        threading.Thread(target=self._async_init, daemon=True).start()

    def _create_icon(self):
        """创建托盘图标"""
        # 尝试加载自定义图标
        icon_path = Path("assets/icon.ico")
        if getattr(sys, 'frozen', False):
            # PyInstaller 打包后，资源在临时目录
             basedir = sys._MEIPASS
             icon_path = Path(basedir) / "assets" / "icon.ico"
        
        if icon_path.exists():
            try:
                return Image.open(str(icon_path))
            except Exception as e:
                self.logger.error(f"Error loading icon: {e}")

        # 降级：创建一个简单的图标
        width = 64
        height = 64
        image = Image.new('RGB', (width, height), color=(0, 120, 215))
        draw = ImageDraw.Draw(image)

        # 绘制一个简单的麦克风图标
        # 外圆
        draw.ellipse([(20, 15), (44, 39)], fill=(255, 255, 255))
        # 麦克风主体
        draw.ellipse([(26, 20), (38, 34)], fill=(0, 120, 215))
        # 底部支架
        draw.rectangle([(30, 36), (34, 42)], fill=(255, 255, 255))
        # 底座
        draw.ellipse([(24, 42), (40, 46)], fill=(255, 255, 255))

        return image

    def _create_recording_icon(self):
        """创建录音状态图标"""
        width = 64
        height = 64
        image = Image.new('RGB', (width, height), color=(220, 0, 0))
        draw = ImageDraw.Draw(image)

        # 绘制红色录音指示器
        draw.ellipse([(20, 20), (44, 44)], fill=(255, 255, 255))

        return image

    def _update_status(self, status: str):
        """更新状态"""
        self.logger.info(f"Status update: {status}")
        self._current_status = status

        # 更新托盘提示文字
        status_text = f"{APP_NAME} - {status}"
        self.tray_icon.title = status_text

        # 更新菜单中的状态项
        # 由于pystray不直接支持动态更新菜单文本，我们通过更新title来实现

        # 根据状态更新图标
        if "录音" in status:
            self.tray_icon.icon = self._create_recording_icon()
        else:
            self.tray_icon.icon = self._create_icon()
            
        # Rebuild menu to update status text
        self.tray_icon.menu = self._build_menu()

    def _async_init(self):
        """异步初始化"""
        try:
            t0 = time.time()

            self._log("加载配置...")
            self.config = load_config()

            self._log("初始化...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_status
            self.controller.on_stats_change = self._on_stats_change
            self.controller.initialize(callback=self._log)

            self._initialized = True

            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
            self._log(f"启动完成，耗时 {time.time() - t0:.1f}s")
            self._log(f"热键: {hotkey}")

            # 自动启用
            self._auto_enable()

        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            import traceback
            traceback.print_exc()

    def _auto_enable(self):
        """自动启用"""
        self._enabled = True
        self.controller.start()
        self._update_status("就绪")

        hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
        # 注意：Windows上可以添加toast通知，但为了简单起见，这里只更新状态
        self._log(f"按住 {hotkey} 开始语音输入")

    def _log(self, msg: str):
        """日志输出"""
        self.logger.info(msg)
        self._update_status(msg)

    def _on_status(self, status: str):
        """状态变化回调"""
        self._update_status(status)

    def _build_menu(self):
        """构建菜单"""
        stats_text = self.controller.get_stats_display() if self.controller else "已输入：0字（0次）"
        
        return pystray.Menu(
            pystray.MenuItem(f"状态: {self._current_status}", lambda _: None, enabled=False),
            pystray.MenuItem(stats_text, lambda _: None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("启用语音输入", self.toggle_enabled, checked=lambda item: self._enabled),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("打开配置文件", self.open_config),
            pystray.MenuItem("打开词库文件", self.open_hotwords),
            pystray.MenuItem("打开配置目录", self.open_config_dir),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("关于", self.show_about),
            pystray.MenuItem("退出", self.quit_app),
        )

    def _on_stats_change(self):
        """统计数据变化回调"""
        # Rebuild menu to update stats text
        self.tray_icon.menu = self._build_menu()



    def toggle_enabled(self, icon, item):
        """切换启用/禁用状态"""
        if not self._initialized:
            return

        if self._enabled:
            self._enabled = False
            self.controller.stop()
            self._update_status("已禁用")
        else:
            self._enabled = True
            self.controller.start()
            self._update_status("就绪")
            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
            self._log(f"按住 {hotkey} 开始语音输入")

    def open_config(self, icon, item):
        """打开配置文件"""
        ensure_default_files()
        os.startfile(str(get_config_path()))

    def open_hotwords(self, icon, item):
        """打开词库文件"""
        ensure_default_files()
        os.startfile(str(get_default_hotwords_path()))

    def open_config_dir(self, icon, item):
        """打开配置目录"""
        ensure_default_files()
        os.startfile(str(get_config_dir()))

    def show_about(self, icon, item):
        """显示关于信息"""
        server = f"{self.config.server.host}:{self.config.server.port}" if self.config else "未配置"
        config_dir = get_config_dir()

        message = (
            f"{APP_NAME} v{APP_VERSION}\n\n"
            f"识别服务: {server}\n"
            f"配置目录: {config_dir}\n\n"
            "基于 FunASR 的离线语音识别"
        )

        # 在Windows上使用简单的消息框
        import ctypes
        ctypes.windll.user32.MessageBoxW(0, message, f"关于 {APP_NAME}", 0)

    def quit_app(self, icon, item):
        """退出应用"""
        if self.controller:
            self.controller.stop()
        self.tray_icon.stop()

    def run(self):
        """运行应用"""
        # 先设置为初始图标
        self.tray_icon.icon = self._create_icon()
        # 运行系统托盘
        self.tray_icon.run()


def main():
    """主函数"""
    # 配置日志
    logging.basicConfig(
        level=logging.INFO,
        format='%(message)s',
        handlers=[logging.StreamHandler(sys.stdout)]
    )
    logger = logging.getLogger(APP_NAME)

    ensure_default_files()

    logger.info("=" * 50)
    logger.info(f"{APP_NAME} v{APP_VERSION}")
    logger.info("=" * 50)
    logger.info(f"配置目录: {get_config_dir()}")
    logger.info("")

    app = VoiceTyperApp()
    app.run()


if __name__ == "__main__":
    main()
