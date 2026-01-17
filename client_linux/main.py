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
from pathlib import Path

gi.require_version('Gtk', '4.0')

from gi.repository import Gtk, GLib

from config import load_config, get_config_dir, ensure_config_dir
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

        # 初始化配置
        self._init_config()

        # 初始化控制器
        self._init_controller()

        # 设置信号处理
        self._setup_signal_handlers()

    def _init_config(self):
        """初始化配置"""
        logger.info("加载配置...")
        ensure_config_dir()
        self.config = load_config()

    def _init_controller(self):
        """初始化控制器"""
        logger.info("初始化控制器...")
        self.controller = VoiceTyperController(self.config)
        self.controller.initialize(callback=self._on_status_change)

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

    def _on_status_change(self, status: str):
        """状态变化回调"""
        # 不记录状态变化，太啰嗦
        pass

    def start(self):
        """启动应用程序"""
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
