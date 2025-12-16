"""
文本输入模块 - 在当前光标位置插入文本
"""
import time
import threading
from typing import Optional
from pynput.keyboard import Controller, Key


class TextInserter:
    """文本插入器 - 模拟键盘输入"""
    
    def __init__(self):
        self._keyboard = Controller()
        self._lock = threading.Lock()
    
    def insert(self, text: str, delay: float = 0.01):
        """在当前光标位置插入文本
        
        Args:
            text: 要插入的文本
            delay: 每个字符之间的延迟（秒），用于提高兼容性
        """
        if not text:
            return
        
        with self._lock:
            # 使用 type 方法直接输入
            # 对于中文等非ASCII字符，pynput 会自动处理
            for char in text:
                try:
                    self._keyboard.type(char)
                    if delay > 0:
                        time.sleep(delay)
                except Exception as e:
                    print(f"输入字符 '{char}' 失败: {e}")
    
    def insert_fast(self, text: str):
        """快速插入文本（使用剪贴板）
        
        对于较长文本，使用剪贴板粘贴更快
        """
        if not text:
            return
        
        import subprocess
        
        with self._lock:
            try:
                # 保存当前剪贴板内容
                result = subprocess.run(
                    ['pbpaste'],
                    capture_output=True,
                    text=True
                )
                old_clipboard = result.stdout
                
                # 将文本写入剪贴板
                process = subprocess.Popen(
                    ['pbcopy'],
                    stdin=subprocess.PIPE
                )
                process.communicate(text.encode('utf-8'))
                
                # 模拟 Cmd+V 粘贴
                time.sleep(0.05)
                self._keyboard.press(Key.cmd)
                self._keyboard.press('v')
                self._keyboard.release('v')
                self._keyboard.release(Key.cmd)
                time.sleep(0.05)
                
                # 恢复剪贴板（可选）
                # process = subprocess.Popen(['pbcopy'], stdin=subprocess.PIPE)
                # process.communicate(old_clipboard.encode('utf-8'))
                
            except Exception as e:
                print(f"快速插入失败，回退到逐字符输入: {e}")
                self.insert(text)


# 单例
_inserter: Optional[TextInserter] = None


def get_inserter() -> TextInserter:
    """获取文本插入器单例"""
    global _inserter
    if _inserter is None:
        _inserter = TextInserter()
    return _inserter


def insert_text(text: str, fast: bool = True):
    """便捷函数：插入文本
    
    Args:
        text: 要插入的文本
        fast: 是否使用快速模式（剪贴板）
    """
    inserter = get_inserter()
    if fast and len(text) > 5:
        inserter.insert_fast(text)
    else:
        inserter.insert(text)


if __name__ == "__main__":
    print("3秒后将在光标位置插入测试文本...")
    time.sleep(3)
    insert_text("你好，这是语音输入测试！Hello World!")
    print("完成")