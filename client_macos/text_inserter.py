"""
文本输入模块
"""
import time
import logging
import threading
from typing import Optional

from AppKit import NSPasteboard, NSPasteboardTypeString
from pynput.keyboard import Controller, Key

logger = logging.getLogger('VoiceTyper')


class TextInserter:
    """文本插入器"""
    
    def __init__(self):
        self._keyboard = Controller()
        self._pasteboard = NSPasteboard.generalPasteboard()
        self._lock = threading.Lock()
    
    def _get_clipboard(self) -> str:
        """获取当前剪贴板内容"""
        try:
            text = self._pasteboard.stringForType_(NSPasteboardTypeString)
            return text if text is not None else ""
        except Exception:
            return ""
    
    def _set_clipboard(self, text: str) -> Optional[int]:
        """设置剪贴板并返回变更计数"""
        try:
            self._pasteboard.clearContents()
            success = self._pasteboard.setString_forType_(text, NSPasteboardTypeString)
            if not success:
                raise RuntimeError("剪贴板写入失败")
            return self._pasteboard.changeCount()
        except Exception as e:
            logger.error(f"设置剪贴板失败: {e}")
            return None

    def _wait_for_clipboard(self, expected: str, timeout: float = 0.08) -> bool:
        """短轮询确认剪贴板内容已更新，避免固定 sleep"""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self._get_clipboard() == expected:
                return True
            time.sleep(0.005)
        return self._get_clipboard() == expected

    def _restore_clipboard_if_unchanged(
        self,
        inserted_text: str,
        old_clipboard: str,
        expected_change_count: Optional[int],
    ):
        """仅在剪贴板仍未被用户或后续操作修改时恢复"""
        with self._lock:
            try:
                current_text = self._get_clipboard()
                current_change_count = self._pasteboard.changeCount()
                if expected_change_count is None:
                    return
                if current_change_count != expected_change_count:
                    return
                if current_text != inserted_text:
                    return
                self._set_clipboard(old_clipboard)
            except Exception as e:
                logger.error(f"恢复剪贴板失败: {e}")
    
    def insert(self, text: str):
        """插入文本到当前光标位置"""
        if not text:
            return
        
        with self._lock:
            # 备份当前剪贴板
            old_clipboard = self._get_clipboard()
            
            try:
                # 使用原生 Pasteboard API，避免子进程开销
                inserted_change_count = self._set_clipboard(text)
                if inserted_change_count is None:
                    return

                if not self._wait_for_clipboard(text):
                    logger.warning("剪贴板写入确认超时，继续尝试粘贴")
                
                # 模拟 Cmd+V 粘贴
                self._keyboard.press(Key.cmd)
                self._keyboard.press('v')
                self._keyboard.release('v')
                self._keyboard.release(Key.cmd)
            except Exception as e:
                logger.error(f"文本插入失败: {e}")
                return

        # 延迟恢复剪贴板；若期间用户复制了新内容，则不覆盖
        threading.Timer(
            0.35,
            lambda: self._restore_clipboard_if_unchanged(
                text,
                old_clipboard,
                inserted_change_count,
            )
        ).start()


_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)
