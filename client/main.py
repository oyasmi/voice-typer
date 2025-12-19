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
        super().__init__(name=APP_NAME, title="⚪", quit_button=None)
        
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False
        
        self._status_item = rumps.MenuItem("状态: 初始化中...", callback=None)
        self._toggle_item = rumps.MenuItem("启用语音输入", callback=self.toggle_enabled)
        
        self.menu = [
            self._status_item,
            None,
            self._toggle_item,
            None,
            rumps.MenuItem("打开配置文件", callback=self.open_config),
            rumps.MenuItem("打开词库文件", callback=self.open_hotwords),
            rumps.MenuItem("打开配置目录", callback=self.open_config_dir),
            # rumps.MenuItem("重新加载配置", callback=self.reload_config),
            None,
            rumps.MenuItem("关于", callback=self.show_about),
            rumps.MenuItem("退出", callback=self.quit_app),
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
            self.controller.initialize(callback=self._log)
            
            self._initialized = True
            
            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
            self._toggle_item.title = f"禁用语音输入 ({hotkey})"
            
            self._auto_enable()
            self._log(f"启动完成，耗时 {time.time() - t0:.1f}s")
            
        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            import traceback
            traceback.print_exc()
    
    def _auto_enable(self):
        self._enabled = True
        self._toggle_item.state = True
        self.controller.start()
        self.title = "🎤"
        self._update_status("就绪")
        
        hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
        rumps.notification(APP_NAME, "", f"按住 {hotkey} 开始语音输入")
    
    def _log(self, msg: str):
        print(f"[{APP_NAME}] {msg}")
        self._update_status(msg)
    
    def _update_status(self, status: str):
        self._status_item.title = f"状态: {status}"
        
        if "录音" in status:
            self.title = "🔴"
        elif "识别" in status:
            self.title = "🟡"
        elif self._enabled:
            self.title = "🎤"
        else:
            self.title = "⚪"
    
    def _on_status(self, status: str):
        self._update_status(status)
    
    def toggle_enabled(self, sender):
        if not self._initialized:
            rumps.notification(APP_NAME, "", "请等待初始化完成")
            return
        
        hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
        
        if self._enabled:
            self._enabled = False
            self.controller.stop()
            sender.state = False
            sender.title = f"启用语音输入 ({hotkey})"
            self.title = "⚪"
            self._update_status("已禁用")
        else:
            self._enabled = True
            self.controller.start()
            sender.state = True
            sender.title = f"禁用语音输入 ({hotkey})"
            self.title = "🎤"
            self._update_status("就绪")
            rumps.notification(APP_NAME, "", f"按住 {hotkey} 开始语音输入")
    
    def open_config(self, _):
        ensure_default_files()
        subprocess.run(["open", str(get_config_path())])
    
    def open_hotwords(self, _):
        ensure_default_files()
        subprocess.run(["open", str(get_default_hotwords_path())])
    
    def open_config_dir(self, _):
        ensure_default_files()
        subprocess.run(["open", str(get_config_dir())])
    
    def reload_config(self, _):
        if not self._initialized:
            return
        
        if self._enabled:
            self._enabled = False
            self.controller.stop()
            self._toggle_item.state = False
        
        self._update_status("重新加载...")
        
        def _reload():
            try:
                self.config = load_config()
                self.controller = VoiceTyperController(self.config)
                self.controller.on_status_change = self._on_status
                self.controller.initialize(callback=self._log)
                self._auto_enable()
                
                hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
                self._toggle_item.title = f"禁用语音输入 ({hotkey})"
                rumps.notification(APP_NAME, "", "配置已重新加载")
            except Exception as e:
                self._update_status(f"重载失败: {e}")
        
        threading.Thread(target=_reload, daemon=True).start()
    
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
    print("\n正在退出...")
    if _app:
        _app._cleanup()
    sys.exit(0)


def main():
    global _app
    
    ensure_default_files()
    
    print("=" * 50)
    print(f"{APP_NAME} v{APP_VERSION}")
    print("=" * 50)
    print(f"配置目录: {get_config_dir()}")
    print()
    
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    _app = VoiceTyperApp()
    _app.run()


if __name__ == "__main__":
    main()