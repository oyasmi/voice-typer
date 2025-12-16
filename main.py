#!/usr/bin/env python3
"""
VoiceTyper - macOS 本地语音输入工具
主入口 & 菜单栏应用
"""
import sys
import os
import threading
import subprocess
from pathlib import Path

import rumps

from config import load_config, get_config_path, AppConfig
from controller import VoiceTyperController


class VoiceTyperApp(rumps.App):
    """菜单栏应用"""
    
    def __init__(self):
        super().__init__(
            name="VoiceTyper",
            icon=None,
            title="🎤",
            quit_button=None,
        )
        
        self.config: AppConfig = None
        self.controller: VoiceTyperController = None
        self._initialized = False
        self._enabled = False
        
        # 状态菜单项
        self._status_item = rumps.MenuItem("状态: 初始化中...", callback=None)
        self._toggle_item = rumps.MenuItem("启用语音输入", callback=self.toggle_enabled)
        
        # 构建菜单
        self.menu = [
            self._status_item,
            None,  # 分隔线
            self._toggle_item,
            None,
            rumps.MenuItem("打开配置文件", callback=self.open_config),
            rumps.MenuItem("重新加载配置", callback=self.reload_config),
            None,
            rumps.MenuItem("关于", callback=self.show_about),
            rumps.MenuItem("退出", callback=self.quit_app),
        ]
        
        # 异步初始化（仅加载模型，不创建 UI 组件）
        threading.Thread(target=self._async_init, daemon=True).start()
    
    def _async_init(self):
        """异步初始化"""
        try:
            self._update_status("正在加载配置...")
            self.config = load_config()
            
            self._update_status("正在初始化引擎 (首次需下载模型)...")
            self.controller = VoiceTyperController(self.config)
            self.controller.on_status_change = self._on_controller_status
            self.controller.initialize(progress_callback=self._update_status)
            
            self._initialized = True
            self._update_status("就绪 - 点击启用")
            
            # 更新菜单项文字，显示热键
            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}"
            self._toggle_item.title = f"启用语音输入 ({hotkey})"
            
        except Exception as e:
            self._update_status(f"初始化失败: {e}")
            print(f"初始化错误: {e}")
            import traceback
            traceback.print_exc()
    
    def _update_status(self, status: str):
        """更新状态显示"""
        self._status_item.title = f"状态: {status}"
        
        # 更新菜单栏图标状态
        if "录音" in status or "识别" in status:
            self.title = "🔴"
        elif self._enabled:
            self.title = "🎤"
        else:
            self.title = "⚪"  # 未启用时用灰色
    
    def _on_controller_status(self, status: str):
        """控制器状态回调"""
        self._update_status(status)
    
    def toggle_enabled(self, sender):
        """切换启用状态"""
        if not self._initialized:
            rumps.notification(
                title="VoiceTyper",
                subtitle="",
                message="请等待初始化完成",
            )
            return
        
        if self._enabled:
            # 禁用
            self._enabled = False
            self.controller.stop()
            sender.state = False
            self._update_status("已禁用")
            self.title = "⚪"
        else:
            # 启用（在主线程中启动，这样 indicator 会在主线程创建）
            self._enabled = True
            self.controller.start()
            sender.state = True
            self._update_status("已启用")
            self.title = "🎤"
            
            # 显示通知
            hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}"
            rumps.notification(
                title="VoiceTyper 已启用",
                subtitle="",
                message=f"按住 {hotkey} 开始语音输入",
            )
    
    def open_config(self, _):
        """打开配置文件"""
        config_path = get_config_path()
        
        # 确保配置文件存在
        if not config_path.exists():
            load_config()  # 这会创建默认配置
        
        # 使用默认编辑器打开
        subprocess.run(["open", str(config_path)])
    
    def reload_config(self, _):
        """重新加载配置"""
        if not self._initialized:
            rumps.notification(
                title="VoiceTyper",
                subtitle="",
                message="请等待初始化完成",
            )
            return
        
        was_enabled = self._enabled
        
        # 停止当前服务
        if self._enabled:
            self._enabled = False
            self.controller.stop()
            self._toggle_item.state = False
        
        # 重新加载
        self._update_status("重新加载配置中...")
        
        def _reload():
            try:
                self.config = load_config()
                self.controller = VoiceTyperController(self.config)
                self.controller.on_status_change = self._on_controller_status
                self.controller.initialize(progress_callback=self._update_status)
                
                # 更新热键显示
                hotkey = f"{'+'.join(self.config.hotkey.modifiers)}+{self.config.hotkey.key}"
                self._toggle_item.title = f"启用语音输入 ({hotkey})"
                
                self._update_status("配置已更新")
                
                # 如果之前是启用的，需要用户手动重新启用
                # （因为需要在主线程启动）
                if was_enabled:
                    rumps.notification(
                        title="VoiceTyper",
                        subtitle="配置已重新加载",
                        message="请重新点击启用语音输入",
                    )
                else:
                    rumps.notification(
                        title="VoiceTyper",
                        subtitle="",
                        message="配置已重新加载",
                    )
                
            except Exception as e:
                self._update_status(f"重载失败: {e}")
                rumps.notification(
                    title="VoiceTyper",
                    subtitle="配置重载失败",
                    message=str(e),
                )
        
        threading.Thread(target=_reload, daemon=True).start()
    
    def show_about(self, _):
        """显示关于信息"""
        rumps.alert(
            title="VoiceTyper",
            message=(
                "本地语音输入工具 v1.0\n\n"
                "基于 FunASR 的离线语音识别\n"
                "所有处理均在本地完成，保护隐私\n\n"
                "https://github.com/modelscope/FunASR"
            ),
            ok="确定",
        )
    
    def quit_app(self, _):
        """退出应用"""
        if self._enabled and self.controller:
            self.controller.stop()
        rumps.quit_application()


def check_accessibility_permission():
    """检查辅助功能权限"""
    try:
        from AppKit import NSWorkspace
        # 简单检查，实际权限会在使用时请求
        return True
    except Exception:
        return False


def check_microphone_permission():
    """检查麦克风权限"""
    try:
        import sounddevice as sd
        # 尝试查询设备，这会触发权限请求
        sd.query_devices()
        return True
    except Exception as e:
        print(f"麦克风检查: {e}")
        return False


def main():
    """主函数"""
    print("=" * 50)
    print("VoiceTyper - macOS 本地语音输入工具")
    print("=" * 50)
    
    # 检查麦克风权限
    print("检查麦克风权限...")
    if not check_microphone_permission():
        print("警告: 可能没有麦克风权限，请在系统设置中授权")
    
    print("启动应用...")
    print("提示: 首次运行需要在「系统设置 → 隐私与安全性 → 辅助功能」中授权")
    print("")
    
    app = VoiceTyperApp()
    app.run()


if __name__ == "__main__":
    main()