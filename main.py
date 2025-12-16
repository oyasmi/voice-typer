#!/usr/bin/env python3
"""
VoiceTyper - macOS 本地语音输入工具
"""
import sys
import os
import time
import threading
import subprocess
import signal
import atexit

import rumps

from config import (
    load_config, 
    get_config_path, 
    get_config_dir,
    get_default_hotwords_path,
    ensure_default_files,
    AppConfig,
    APP_NAME,
    APP_VERSION,
)
from controller import VoiceTyperController


class VoiceTyperApp(rumps.App):
    """菜单栏应用"""
    
    def __init__(self):
        super().__init__(
            name=APP_NAME,
            title="⚪",
            quit_button=None,
        )
        
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False
        
        # 菜单项
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
            rumps.MenuItem("重新加载配置", callback=self.reload_config),
            None,
            rumps.MenuItem("关于", callback=self.show_about),
            rumps.MenuItem("退出", callback=self.quit_app),
        ]
        
        # 注册清理函数
        atexit.register(self._cleanup)
        
        # 异步初始化
        threading.Thread(target=self._async_init, daemon=True).start()
    
    def _cleanup(self):
        """清理资源"""
        if self._enabled and self.controller:
            try:
                self.controller.stop()
            except:
                pass
    
    def _async_init(self):
        """异步初始化"""
        try:
            total_start = time.time()
            
            self._update_status("加载配置...")
            self.config = load_config()
            
            self._update_status("初始化引擎 (首次需下载模型)...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_controller_status
            self.controller.initialize(callback=self._log)
            
            self._initialized = True
            
            # 更新热键显示
            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
            self._toggle_item.title = f"禁用语音输入 ({hotkey})"
            
            # 自动启用
            self._auto_enable()
            
            total_time = time.time() - total_start
            self._log(f"启动完成，总耗时 {total_time:.1f}s")
            
        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            self._log(f"初始化错误: {e}")
            import traceback
            traceback.print_exc()
    
    def _auto_enable(self):
        """自动启用语音输入"""
        self._enabled = True
        self._toggle_item.state = True
        self.controller.start()
        self.title = "🎤"
        self._update_status("就绪")
        
        hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
        rumps.notification(f"{APP_NAME} 已启动", "", f"按住 {hotkey} 开始语音输入")
    
    def _log(self, msg: str):
        """日志输出"""
        print(f"[{APP_NAME}] {msg}")
        self._update_status(msg)
    
    def _update_status(self, status: str):
        """更新状态"""
        self._status_item.title = f"状态: {status}"
        
        if "录音" in status:
            self.title = "🔴"
        elif "识别" in status:
            self.title = "🟡"
        elif self._enabled:
            self.title = "🎤"
        else:
            self.title = "⚪"
    
    def _on_controller_status(self, status: str):
        """控制器状态回调"""
        self._update_status(status)
    
    def toggle_enabled(self, sender):
        """切换启用状态"""
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
            rumps.notification(f"{APP_NAME} 已启用", "", f"按住 {hotkey} 开始语音输入")
    
    def open_config(self, _):
        """打开配置文件"""
        ensure_default_files()
        subprocess.run(["open", str(get_config_path())])
    
    def open_hotwords(self, _):
        """打开词库文件"""
        ensure_default_files()
        subprocess.run(["open", str(get_default_hotwords_path())])
    
    def open_config_dir(self, _):
        """打开配置目录"""
        ensure_default_files()
        subprocess.run(["open", str(get_config_dir())])
    
    def reload_config(self, _):
        """重新加载配置"""
        if not self._initialized:
            rumps.notification(APP_NAME, "", "请等待初始化完成")
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
                self.controller.on_status_change = self._on_controller_status
                self.controller.initialize(callback=self._log)
                
                self._auto_enable()
                
                hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}".upper()
                self._toggle_item.title = f"禁用语音输入 ({hotkey})"
                
                rumps.notification(APP_NAME, "", "配置已重新加载")
                
            except Exception as e:
                self._update_status(f"重载失败: {e}")
                rumps.notification(APP_NAME, "重载失败", str(e))
        
        threading.Thread(target=_reload, daemon=True).start()
    
    def show_about(self, _):
        """显示关于"""
        config_dir = get_config_dir()
        rumps.alert(
            title=APP_NAME,
            message=(
                f"本地语音输入工具 v{APP_VERSION}\n\n"
                "基于 FunASR 的离线语音识别\n"
                "所有处理均在本地完成\n\n"
                f"配置目录:\n{config_dir}\n\n"
                "https://github.com/modelscope/FunASR"
            ),
            ok="确定",
        )
    
    def quit_app(self, _):
        """退出"""
        self._cleanup()
        rumps.quit_application()


# 全局 app 引用
_app = None


def signal_handler(signum, frame):
    """信号处理"""
    print("\n正在退出...")
    if _app:
        _app._cleanup()
    sys.exit(0)


def main():
    """主函数"""
    global _app
    
    # 确保配置目录存在
    ensure_default_files()
    
    # 控制台输出启动信息
    print("=" * 50)
    print(f"{APP_NAME} - macOS 本地语音输入工具 v{APP_VERSION}")
    print("=" * 50)
    print()
    print(f"配置目录: {get_config_dir()}")
    print()
    
    # 检查麦克风
    print("检查麦克风...")
    try:
        import sounddevice as sd
        default_input = sd.query_devices(kind='input')
        print(f"  默认输入设备: {default_input['name']}")
    except Exception as e:
        print(f"  警告: {e}")
    
    print()
    print("启动应用...")
    print("提示: 需要在「系统设置 → 隐私与安全性 → 辅助功能」中授权")
    print()
    
    # 注册信号处理
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    _app = VoiceTyperApp()
    _app.run()


if __name__ == "__main__":
    main()