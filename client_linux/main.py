#!/usr/bin/env python3
"""
VoiceTyper Linux Client
Linux Wayland 语音输入客户端 - 入口程序
"""
import signal
import sys
import os
import gi
import logging
import threading
import time
from pathlib import Path

gi.require_version('Gtk', '4.0')

from gi.repository import Gtk, GLib

from config import load_config, get_config_dir, ensure_config_dir, APP_VERSION
from controller import VoiceTyperController


# 配置日志
logging.basicConfig(
    level=logging.INFO,
    format='%(message)s',
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger('VoiceTyper')


class VoiceTyperApp:
    """VoiceTyper 主应用程序"""

    def __init__(self):
        """初始化应用程序"""
        self.config = None
        self.controller = None
        self.loop = None
        self._initialized = False

        # Start async initialization
        threading.Thread(target=self._async_init, daemon=True).start()
        self._setup_signal_handlers()

    def _async_init(self):
        """Asynchronous initialization (background thread)"""
        try:
            t0 = time.time()

            self._update_status("加载配置...")
            ensure_config_dir()
            self.config = load_config()

            self._update_status("初始化...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_status_change
            self.controller.on_stats_change = self._on_stats_change
            self.controller.initialize(callback=self._log)

            self._initialized = True
            self.start()

            self._log(f"启动完成，耗时 {time.time() - t0:.1f}s")

        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            import traceback
            traceback.print_exc()

    def _update_status(self, status: str):
        """Update status display"""
        logger.info(status)

    def _on_status_change(self, status: str):
        """Status change callback"""
        # Linux console doesn't need per-status updates
        pass

    def _on_stats_change(self):
        """Statistics change callback"""
        # Could log stats periodically if desired
        pass

    def _log(self, msg: str):
        """Log message"""
        logger.info(msg)

    def _setup_signal_handlers(self):
        """设置信号处理器"""
        # 处理 SIGINT (Ctrl+C)
        signal.signal(signal.SIGINT, self._signal_handler)
        # 处理 SIGTERM
        signal.signal(signal.SIGTERM, self._signal_handler)

    def _signal_handler(self, signum, frame):
        """信号处理器"""
        logger.info("正在退出...")
        self.quit()
        if self.loop:
            self.loop.quit()

    def start(self):
        """Start after initialization"""
        self.controller.start()
        key = self.config.hotkey.key.upper()
        mods = '+'.join(m.title() for m in self.config.hotkey.modifiers)
        logger.info(f"按 {mods}+{key} 开始录音")

    def quit(self):
        """退出应用程序"""
        if self.controller:
            self.controller.stop()

    def run(self):
        """运行主循环"""
        self.loop = GLib.MainLoop()
        self.loop.run()


def main():
    """主函数"""
    # 检查 Wayland 环境
    session_type = Path('/proc/self/session_type').read_text().strip() if Path('/proc/self/session_type').exists() else None
    xdg_session = os.environ.get('XDG_SESSION_TYPE', '')

    if session_type != 'wayland' and xdg_session != 'wayland':
        logger.warning("当前环境可能不是 Wayland，某些功能可能无法正常工作")

    # 创建应用程序
    try:
        app = VoiceTyperApp()
        app.start()
        app.run()

    except KeyboardInterrupt:
        logger.info("正在退出...")
        sys.exit(0)
    except Exception as e:
        logger.error(f"应用程序错误: {e}")
        sys.exit(1)


if __name__ == '__main__':
    main()
