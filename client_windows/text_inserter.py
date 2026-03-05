"""
文本输入模块 - Windows版本
使用剪贴板 + Ctrl+V 模拟粘贴
"""
import time
import threading
import pyperclip
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器"""

    def __init__(self):
        self._keyboard = Controller()

    def insert(self, text: str):
        """插入文本到当前光标位置"""
        if not text:
            return

        # 备份当前剪贴板
        try:
            old_clipboard = pyperclip.paste()
        except Exception:
            old_clipboard = ""

        try:
            # 使用 pyperclip 写入剪贴板 (自动处理UTF-8)
            pyperclip.copy(text)

            # 短暂等待剪贴板就绪
            time.sleep(0.05)

            # 模拟 Ctrl+V 粘贴
            self._keyboard.press(Key.ctrl)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.ctrl)

            time.sleep(0.05)

        except Exception as e:
            print(f"文本插入失败: {e}")
        finally:
            # 延迟恢复剪贴板
            def restore_clipboard():
                try:
                    pyperclip.copy(old_clipboard)
                except Exception:
                    pass
            threading.Timer(0.5, restore_clipboard).start()


_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)
