#!/usr/bin/env python3
"""
VoiceTyper Windows 客户端
"""
import sys
import time
import threading
import os
import signal
import atexit
from pathlib import Path

import pystray
from PIL import Image, ImageDraw

from config import (
    load_config, get_config_path, get_config_dir,
    get_default_hotwords_path, ensure_default_files,
    AppConfig, APP_NAME, APP_VERSION,
)
from controller import VoiceTyperController


class VoiceTyperApp:
    """系统托盘应用"""
    
    # 图标颜色
    COLOR_DISABLED = '#808080'  # 灰色 - 禁用
    COLOR_READY = '#4CAF50'     # 绿色 - 就绪
    COLOR_RECORDING = '#FF5722'  # 红色 - 录音中
    COLOR_PROCESSING = '#FFC107' # 黄色 - 处理中
    
    def __init__(self):
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False
        self._icon: pystray.Icon = None
        self._current_status = "初始化中..."
        
        atexit.register(self._cleanup)
    
    def _cleanup(self):
        """清理资源"""
        if self._enabled and self.controller:
            try:
                self.controller.stop()
            except:
                pass
    
    def _create_icon_image(self, color: str) -> Image.Image:
        """创建托盘图标"""
        size = 64
        img = Image.new('RGB', (size, size), color='white')
        draw = ImageDraw.Draw(img)
        
        # 绘制圆形背景
        draw.ellipse([8, 8, size-8, size-8], fill=color, outline=color)
        
        # 绘制麦克风图标（简化版）
        # 麦克风主体
        mic_color = 'white'
        draw.ellipse([24, 18, 40, 34], fill=mic_color)
        draw.rectangle([24, 26, 40, 38], fill=mic_color)
        
        # 麦克风支架
        draw.rectangle([30, 38, 34, 46], fill=mic_color)
        draw.rectangle([24, 46, 40, 50], fill=mic_color)
        
        return img
    
    def _update_icon(self, color: str = None):
        """更新托盘图标"""
        if color is None:
            if not self._enabled:
                color = self.COLOR_DISABLED
            elif "录音" in self._current_status:
                color = self.COLOR_RECORDING
            elif "识别" in self._current_status or "处理" in self._current_status:
                color = self.COLOR_PROCESSING
            else:
                color = self.COLOR_READY
        
        if self._icon:
            try:
                self._icon.icon = self._create_icon_image(color)
            except:
                pass
    
    def _log(self, msg: str):
        """日志输出"""
        print(f"[{APP_NAME}] {msg}")
        self._update_status(msg)
    
    def _update_status(self, status: str):
        """更新状态"""
        self._current_status = status
        
        # 更新托盘标题
        if self._icon:
            self._icon.title = f"{APP_NAME} - {status}"
        
        # 更新图标颜色
        self._update_icon()
    
    def _on_status(self, status: str):
        """状态变化回调"""
        self._update_status(status)
    
    def _async_init(self):
        """异步初始化"""
        try:
            t0 = time.time()
            
            self._update_status("加载配置...")
            self.config = load_config()
            
            self._update_status("初始化...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_status
            self.controller.initialize(callback=self._log)
            
            self._initialized = True
            
            self._log(f"启动完成，耗时 {time.time() - t0:.1f}s")
            
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
        
        hotkey = '+'.join(self.config.hotkey.modifiers + [self.config.hotkey.key]).upper()
        self._show_notification(f"按住 {hotkey} 开始语音输入")
    
    def _show_notification(self, message: str):
        """显示通知"""
        if self._icon:
            try:
                self._icon.notify(message, APP_NAME)
            except:
                print(f"通知: {message}")
    
    def _toggle_enabled(self, icon, item):
        """切换启用/禁用"""
        if not self._initialized:
            self._show_notification("请等待初始化完成")
            return
        
        hotkey = '+'.join(self.config.hotkey.modifiers + [self.config.hotkey.key]).upper()
        
        if self._enabled:
            self._enabled = False
            self.controller.stop()
            self._update_status("已禁用")
            self._show_notification("语音输入已禁用")
        else:
            self._enabled = True
            self.controller.start()
            self._update_status("就绪")
            self._show_notification(f"按住 {hotkey} 开始语音输入")
    
    def _open_config(self, icon, item):
        """打开配置文件"""
        ensure_default_files()
        try:
            os.startfile(str(get_config_path()))
        except Exception as e:
            print(f"无法打开配置文件: {e}")
    
    def _open_hotwords(self, icon, item):
        """打开词库文件"""
        ensure_default_files()
        try:
            os.startfile(str(get_default_hotwords_path()))
        except Exception as e:
            print(f"无法打开词库文件: {e}")
    
    def _open_config_dir(self, icon, item):
        """打开配置目录"""
        ensure_default_files()
        try:
            os.startfile(str(get_config_dir()))
        except Exception as e:
            print(f"无法打开配置目录: {e}")
    
    def _show_about(self, icon, item):
        """显示关于信息"""
        server = f"{self.config.server.host}:{self.config.server.port}" if self.config else "未配置"
        message = (
            f"{APP_NAME} v{APP_VERSION}\n"
            f"本地语音输入工具\n\n"
            f"识别服务: {server}\n"
            f"配置目录: {get_config_dir()}\n\n"
            f"基于 FunASR 的离线语音识别"
        )
        print("\n" + "="*50)
        print(message)
        print("="*50 + "\n")
        self._show_notification(f"版本 {APP_VERSION}")
    
    def _quit_app(self, icon, item):
        """退出应用"""
        self._cleanup()
        if self._icon:
            self._icon.stop()
    
    def _create_menu(self):
        """创建托盘菜单"""
        return pystray.Menu(
            pystray.MenuItem(
                "启用/禁用",
                self._toggle_enabled,
                default=True,
                checked=lambda item: self._enabled
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("打开配置文件", self._open_config),
            pystray.MenuItem("打开词库文件", self._open_hotwords),
            pystray.MenuItem("打开配置目录", self._open_config_dir),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("关于", self._show_about),
            pystray.MenuItem("退出", self._quit_app),
        )
    
    def run(self):
        """运行应用"""
        # 创建托盘图标
        self._icon = pystray.Icon(
            APP_NAME,
            self._create_icon_image(self.COLOR_DISABLED),
            f"{APP_NAME} - 初始化中...",
            self._create_menu()
        )
        
        # 启动初始化线程
        threading.Thread(target=self._async_init, daemon=True).start()
        
        # 运行托盘图标（阻塞）
        self._icon.run()


def signal_handler(signum, frame):
    """信号处理"""
    print("\n正在退出...")
    sys.exit(0)


def main():
    """主函数"""
    ensure_default_files()
    
    print("=" * 50)
    print(f"{APP_NAME} v{APP_VERSION}")
    print("=" * 50)
    print(f"配置目录: {get_config_dir()}")
    print()
    
    # 注册信号处理
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    # 创建并运行应用
    app = VoiceTyperApp()
    app.run()


if __name__ == "__main__":
    main()