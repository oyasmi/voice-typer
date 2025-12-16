"""
文本输入模块 - 在当前光标位置插入文本
"""
import time
import subprocess
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器"""
    
    def __init__(self):
        self._keyboard = Controller()
    
    def insert(self, text: str):
        """在当前光标位置插入文本（使用剪贴板方式，速度快）"""
        if not text:
            return
        
        try:
            # 保存剪贴板
            result = subprocess.run(['pbpaste'], capture_output=True, text=True)
            old_clipboard = result.stdout
            
            # 写入文本到剪贴板
            process = subprocess.Popen(['pbcopy'], stdin=subprocess.PIPE)
            process.communicate(text.encode('utf-8'))
            
            # 粘贴
            time.sleep(0.02)
            self._keyboard.press(Key.cmd)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.cmd)
            time.sleep(0.02)
            
            # 恢复剪贴板（可选，注释掉可保留识别结果在剪贴板）
            # process = subprocess.Popen(['pbcopy'], stdin=subprocess.PIPE)
            # process.communicate(old_clipboard.encode('utf-8'))
            
        except Exception as e:
            print(f"文本插入失败: {e}")
            # 回退到逐字符输入
            self._keyboard.type(text)


# 单例
_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)