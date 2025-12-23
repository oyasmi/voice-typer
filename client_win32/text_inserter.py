"""
文本输入模块 - Windows版本
"""
import time
import win32clipboard
import win32con
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器 - 通过剪贴板粘贴"""
    
    def __init__(self):
        self._keyboard = Controller()
    
    def insert(self, text: str):
        """插入文本到当前光标位置"""
        if not text:
            return
        
        try:
            # 保存当前剪贴板内容
            old_clipboard = self._get_clipboard()
            
            # 写入新内容到剪贴板
            self._set_clipboard(text)
            
            # 短暂等待剪贴板就绪
            time.sleep(0.05)
            
            # 模拟 Ctrl+V 粘贴
            self._keyboard.press(Key.ctrl)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.ctrl)
            
            time.sleep(0.05)
            
            # 恢复原剪贴板内容
            if old_clipboard is not None:
                time.sleep(0.1)
                self._set_clipboard(old_clipboard)
            
        except Exception as e:
            print(f"文本插入失败: {e}")
            import traceback
            traceback.print_exc()
    
    def _get_clipboard(self) -> str:
        """获取剪贴板内容"""
        try:
            win32clipboard.OpenClipboard()
            try:
                if win32clipboard.IsClipboardFormatAvailable(win32con.CF_UNICODETEXT):
                    data = win32clipboard.GetClipboardData(win32con.CF_UNICODETEXT)
                    return data
            finally:
                win32clipboard.CloseClipboard()
        except Exception as e:
            print(f"读取剪贴板失败: {e}")
        return None
    
    def _set_clipboard(self, text: str):
        """设置剪贴板内容"""
        try:
            win32clipboard.OpenClipboard()
            try:
                win32clipboard.EmptyClipboard()
                win32clipboard.SetClipboardData(win32con.CF_UNICODETEXT, text)
            finally:
                win32clipboard.CloseClipboard()
        except Exception as e:
            print(f"写入剪贴板失败: {e}")
            raise


_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)