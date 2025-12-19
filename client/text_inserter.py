"""
文本输入模块
"""
import time
import subprocess
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器（使用剪贴板）"""
    
    def __init__(self):
        self._keyboard = Controller()
    
    def insert(self, text: str):
        """插入文本"""
        if not text:
            return
        
        try:
            # 写入剪贴板
            process = subprocess.Popen(['pbcopy'], stdin=subprocess.PIPE)
            process.communicate(text.encode('utf-8'))
            
            # 粘贴
            time.sleep(0.02)
            self._keyboard.press(Key.cmd)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.cmd)
            time.sleep(0.02)
            
        except Exception as e:
            print(f"文本插入失败: {e}")


_inserter = None

def insert_text(text: str):
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)