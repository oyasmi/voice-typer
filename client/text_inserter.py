"""
文本输入模块
"""
import time
import subprocess
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器"""
    
    def __init__(self):
        self._keyboard = Controller()
    
    def insert(self, text: str):
        """插入文本到当前光标位置"""
        if not text:
            return
        
        try:
            # 使用 pbcopy 写入剪贴板
            # 关键：设置 LANG 环境变量确保 UTF-8 编码
            process = subprocess.Popen(
                ['pbcopy'],
                stdin=subprocess.PIPE,
                env={'LANG': 'en_US.UTF-8'}
            )
            process.communicate(input=text.encode('utf-8'))
            process.wait()
            
            # 短暂等待剪贴板就绪
            time.sleep(0.05)
            
            # 模拟 Cmd+V 粘贴
            self._keyboard.press(Key.cmd)
            self._keyboard.press('v')
            self._keyboard.release('v')
            self._keyboard.release(Key.cmd)
            
            time.sleep(0.05)
            
        except Exception as e:
            print(f"文本插入失败: {e}")


_inserter = None


def insert_text(text: str):
    """插入文本"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    _inserter.insert(text)