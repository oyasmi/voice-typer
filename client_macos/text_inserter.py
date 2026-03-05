"""
文本输入模块
"""
import time
import subprocess
import logging
import threading
from pynput.keyboard import Controller, Key

logger = logging.getLogger('VoiceTyper')


class TextInserter:
    """文本插入器"""
    
    def __init__(self):
        self._keyboard = Controller()
    
    def _get_clipboard(self) -> str:
        """获取当前剪贴板内容"""
        try:
            process = subprocess.Popen(
                ['pbpaste'],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env={'LANG': 'en_US.UTF-8'}
            )
            stdout, _ = process.communicate(timeout=2.0)
            return stdout.decode('utf-8')
        except Exception:
            return ""
    
    def _set_clipboard(self, text: str):
        """设置剪贴板内容"""
        process = subprocess.Popen(
            ['pbcopy'],
            stdin=subprocess.PIPE,
            env={'LANG': 'en_US.UTF-8'}
        )
        process.communicate(input=text.encode('utf-8'))
        process.wait()
    
    def insert(self, text: str):
        """插入文本到当前光标位置"""
        if not text:
            return
        
        # 备份当前剪贴板
        old_clipboard = self._get_clipboard()
        
        try:
            # 使用 pbcopy 写入剪贴板
            self._set_clipboard(text)
            
            # 短暂等待剪贴板就绪
            time.sleep(0.05)
            
            # 模拟 Cmd+V 粘贴
            self._keyboard.press(Key.cmd)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.cmd)
            
            time.sleep(0.05)

        except Exception as e:
            logger.error(f"文本插入失败: {e}")
        finally:
            # 延迟恢复剪贴板
            threading.Timer(0.5, lambda: self._set_clipboard(old_clipboard)).start()


_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)