"""
文本输入模块 (Linux Wayland)
使用 wl-clipboard + evdev uinput 实现文本插入
"""
import subprocess
import time
import logging
from typing import Optional

from evdev import UInput, ecodes

logger = logging.getLogger('VoiceTyper')


class TextInserter:
    """文本插入器 - Linux Wayland 实现"""

    def __init__(self):
        self._uinput: Optional[UInput] = None

    def _ensure_uinput(self):
        """确保虚拟键盘设备已创建"""
        if self._uinput is None:
            try:
                # 创建虚拟键盘设备
                self._uinput = UInput(
                    name='voicetyper-vkbd',
                    events={
                        ecodes.EV_KEY: [
                            ecodes.KEY_LEFTCTRL,
                            ecodes.KEY_LEFTSHIFT,
                            ecodes.KEY_V,
                        ]
                    }
                )
                # 等待设备被系统识别
                time.sleep(0.05)
            except PermissionError:
                logger.error("无法创建虚拟键盘设备，请检查 uinput 权限")
                logger.error("运行: sudo usermod -aG input $USER")
                raise

    def insert(self, text: str):
        """
        插入文本到当前光标位置

        方法：
        1. 使用 wl-copy 将文本复制到剪贴板
        2. 等待剪贴板就绪
        3. 使用 uinput 模拟 Ctrl+V 粘贴
        """
        if not text:
            return

        try:
            # 步骤 1: 执行 wl-copy 写入剪贴板，设置超时并检查完成
            result = subprocess.run(
                ['wl-copy'],
                input=text.encode('utf-8'),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=2.0
            )

            # 检查写入是否成功
            if result.returncode != 0:
                logger.error(f"写入剪贴板失败，错误码: {result.returncode}")
                if result.stderr:
                    logger.error(f"wl-copy 错误: {result.stderr.decode('utf-8', errors='ignore').strip()}")
                return

            # 步骤 2: 短暂等待剪贴板就绪
            time.sleep(0.08)

            # 步骤 3: 模拟 Ctrl+V 粘贴
            self._simulate_ctrl_v()

            # 步骤 4: 等待粘贴完成
            time.sleep(0.03)

        except subprocess.TimeoutExpired:
            logger.error("写入剪贴板超时")
        except FileNotFoundError:
            logger.error("未找到 wl-copy 命令")
            logger.error("请安装: sudo apt install wl-clipboard")
        except Exception as e:
            logger.error(f"文本插入失败: {e}")

    def _simulate_ctrl_v(self):
        """模拟 Ctrl+V 按键序列"""
        try:
            self._ensure_uinput()

            # 按下 Ctrl
            self._uinput.write(ecodes.EV_KEY, ecodes.KEY_LEFTCTRL, 1)
            self._uinput.syn()

            # 按下 V
            self._uinput.write(ecodes.EV_KEY, ecodes.KEY_V, 1)
            self._uinput.syn()

            # 释放 V
            self._uinput.write(ecodes.EV_KEY, ecodes.KEY_V, 0)
            self._uinput.syn()

            # 释放 Ctrl
            self._uinput.write(ecodes.EV_KEY, ecodes.KEY_LEFTCTRL, 0)
            self._uinput.syn()

        except Exception as e:
            logger.error(f"虚拟键盘模拟失败: {e}")

    def close(self):
        """清理资源"""
        if self._uinput:
            try:
                self._uinput.close()
            except Exception:
                pass
            self._uinput = None


# 全局单例
_inserter: Optional[TextInserter] = None


def initialize():
    """预初始化文本插入器（创建 UInput 设备）"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
        _inserter._ensure_uinput()


def insert_text(text: str):
    """插入文本（全局函数）"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)


def cleanup():
    """清理资源（全局函数）"""
    global _inserter
    if _inserter:
        _inserter.close()
        _inserter = None
