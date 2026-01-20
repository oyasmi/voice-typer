#!/usr/bin/env python3
"""
VoiceTyper macOS 客户端
"""
import sys
import time
import threading
import subprocess
import signal
import atexit
import logging

import rumps

from config import (
    load_config, get_config_path, get_config_dir,
    get_default_hotwords_path, ensure_default_files,
    AppConfig, APP_NAME, APP_VERSION,
)
from controller import VoiceTyperController


class VoiceTyperApp(rumps.App):
    """菜单栏应用"""
    
    def __init__(self):
        super().__init__(name=APP_NAME, title="⏸️", quit_button=None)
        
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False

        self._status_item = rumps.MenuItem("", callback=None)
        self._stats_item = rumps.MenuItem("已输入：0字（0次）", callback=None)
        self._recent_menu = rumps.MenuItem("📝 最近转换", callback=None)
        self._recent_items: list[rumps.MenuItem] = []

        self._prefs_item = rumps.MenuItem("🔧 偏好设置...", callback=self.open_config_dir)
        self._about_item = rumps.MenuItem("ℹ️ 关于", callback=self.show_about)
        self._quit_item = rumps.MenuItem("🚪 退出", callback=self.quit_app)

        self.menu = [
            self._status_item,
            None,
            self._stats_item,
            None,
            None,  # 分隔线
            self._recent_menu,
            None,
            None,  # 分隔线
            self._prefs_item,
            self._about_item,
            self._quit_item,
        ]
        
        atexit.register(self._cleanup)
        threading.Thread(target=self._async_init, daemon=True).start()
    
    def _cleanup(self):
        if self._enabled and self.controller:
            try:
                self.controller.stop()
            except:
                pass
    
    def _async_init(self):
        try:
            t0 = time.time()

            self._update_status("加载配置...")
            self.config = load_config()

            self._update_status("初始化...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_status
            self.controller.on_stats_change = self._on_stats_change
            self.controller.on_recent_texts_change = self._update_recent_menu
            self.controller.initialize(callback=self._log)

            self._initialized = True

            self._auto_enable()
            self._on_stats_change()  # Initialize stats display
            self._log(f"启动完成，耗时 {time.time() - t0:.1f}s")

        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            import traceback
            traceback.print_exc()
    
    def _auto_enable(self):
        self._enabled = True
        self.controller.start()
        self.title = "🎤"
        self._update_status("就绪")

        hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
        rumps.notification(APP_NAME, "", f"按住 {hotkey} 开始语音输入")
    
    def _log(self, msg: str):
        print(f"[{APP_NAME}] {msg}")
        self._update_status(msg)
    
    def _update_status(self, status: str):
        # 根据状态确定托盘图标和菜单显示
        if "录音" in status:
            # 托盘图标：绿色圆点
            self.title = "🟢"
            # 菜单状态项：同样显示绿色圆点图标
            self._status_item.title = f"🟢 {status}"
        elif "识别" in status:
            # 托盘图标：黄色圆点
            self.title = "🟡"
            # 菜单状态项：同样显示黄色圆点图标
            self._status_item.title = f"🟡 {status}"
        elif self._enabled:
            # 托盘图标：麦克风（就绪状态）
            self.title = "🎤"
            # 菜单状态项：显示绿色圆点图标 + 热键提示（仅在配置加载完成后）
            if self.config:
                hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
                self._status_item.title = f"🟢 {status}  ({hotkey} 开始录音)"
            else:
                self._status_item.title = f"🟢 {status}"
        else:
            # 托盘图标：暂停符号（已禁用）
            self.title = "⏸️"
            # 菜单状态项：显示白色圆点图标
            self._status_item.title = f"⚪ {status}"
    
    def _on_status(self, status: str):
        self._update_status(status)

    def _on_stats_change(self):
        """Update stats display when statistics change"""
        if self.controller:
            self._stats_item.title = self.controller.get_stats_display()

    def _update_recent_menu(self):
        """更新最近转换菜单项"""
        if not self.controller:
            return

        recent_texts = self.controller.get_recent_texts_display()

        # 移除旧的菜单项
        for item in self._recent_items:
            try:
                self._recent_menu.remove(item)
            except:
                pass
        self._recent_items.clear()

        # 添加新的菜单项
        for text in recent_texts:
            item = rumps.MenuItem(f"  {text}", callback=None)
            self._recent_menu.add(item)
            self._recent_items.append(item)

        # 如果没有最近记录，显示提示
        if not recent_texts:
            item = rumps.MenuItem("  暂无记录", callback=None)
            self._recent_menu.add(item)
            self._recent_items.append(item)

    def open_config_dir(self, _):
        ensure_default_files()
        subprocess.run(["open", str(get_config_dir())])

    def show_about(self, _):
        server = f"{self.config.server.host}:{self.config.server.port}" if self.config else "未配置"
        rumps.alert(
            title=APP_NAME,
            message=(
                f"本地语音输入工具 v{APP_VERSION}\n\n"
                f"识别服务: {server}\n"
                f"配置目录: ~/.config/voice_typer/\n\n"
                "基于 FunASR 的离线语音识别"
            ),
            ok="确定",
        )

    def quit_app(self, _):
        self._cleanup()
        rumps.quit_application()

_app = None


def signal_handler(signum, frame):
    logging.getLogger('VoiceTyper').info("正在退出...")
    if _app:
        _app._cleanup()
    sys.exit(0)


def main():
    global _app

    # 配置日志
    logging.basicConfig(
        level=logging.INFO,
        format='%(message)s',
        handlers=[logging.StreamHandler(sys.stdout)]
    )
    logger = logging.getLogger('VoiceTyper')

    ensure_default_files()

    logger.info(f"{APP_NAME} v{APP_VERSION}")
    logger.info(f"配置目录: {get_config_dir()}")

    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    _app = VoiceTyperApp()
    _app.run()


if __name__ == "__main__":
    main()