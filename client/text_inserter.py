"""
文本输入模块
提供两种文本插入方式：
1. DirectTextInserter: 使用 CGEvent API 直接注入文本，不使用剪贴板（推荐）
2. TextInserter: 使用剪贴板 + Cmd+V 方式（回退方案）

技术说明：
- DirectTextInserter 使用 macOS Core Graphics 的 CGEvent API
- 通过 UCKeyTranslate 将 Unicode 字符转换为键盘事件
- 完全不使用剪贴板，用户剪贴板内容不受影响
- 支持所有 Unicode 字符（中文、emoji、符号）
"""
import time
import subprocess
import ctypes
import logging
import threading
from typing import Optional, Tuple, List, Union
from pynput.keyboard import Controller, Key

logger = logging.getLogger(__name__)

# 模块级锁，用于线程安全初始化
_inserter_lock = threading.Lock()


class DirectTextInserter:
    """使用 CGEvent API 直接注入文本，不使用剪贴板

    这是 macOS 平台最优的文本插入方式：
    - 完全不使用剪贴板
    - 支持所有 Unicode 字符（中文、emoji、符号）
    - 用户剪贴板内容完全不受影响

    技术实现：
    - 使用 UCKeyTranslate 将 Unicode 转换为键码
    - 使用 CGEventCreateKeyboardEvent 创建键盘事件
    - 使用 CGEventPost 发送事件到系统
    - 正确管理 Core Foundation 对象生命周期
    """

    def __init__(self):
        """初始化 DirectTextInserter

        Raises:
            RuntimeError: 如果无法获取键盘布局数据
            ImportError: 如果 Quartz 模块不可用
        """
        try:
            from Quartz import (
                TISCopyCurrentKeyboardLayoutInputSource,
                TISGetInputSourceProperty,
                kTISPropertyUnicodeKeyLayoutData,
                CGEventCreateKeyboardEvent,
                CGEventPost,
                CGEventSetFlags,
                kCGSessionEventTap,
                UCKeyTranslate,
                kUCKeyActionDown,
                kUCKeyTranslateNoDeadKeysBit,
                LMGetKbdType,
                CFRelease,
            )
            from Cocoa import CFDataGetBytePtr

            # 保存引用供后续使用
            self._cf_release = CFRelease
            self._tis_copy_current_keyboard_layout_input_source = TISCopyCurrentKeyboardLayoutInputSource
            self._tis_get_input_source_property = TISGetInputSourceProperty
            self._k_tis_property_unicode_key_layout_data = kTISPropertyUnicodeKeyLayoutData
            self._cgevent_create_keyboard_event = CGEventCreateKeyboardEvent
            self._cgevent_post = CGEventPost
            self._cgevent_set_flags = CGEventSetFlags
            self._k_cg_session_event_tap = kCGSessionEventTap
            self._uc_key_translate = UCKeyTranslate
            self._k_uc_key_action_down = kUCKeyActionDown
            self._k_uc_key_translate_no_dead_keys_bit = kUCKeyTranslateNoDeadKeysBit
            self._lm_get_kbd_type = LMGetKbdType
            self._cf_data_get_byte_ptr = CFDataGetBytePtr

            # 获取当前键盘布局（CF 对象，需要释放）
            self._keyboard_layout = self._tis_copy_current_keyboard_layout_input_source()

            # 获取键盘布局数据
            chr_data_ref = self._tis_get_input_source_property(
                self._keyboard_layout,
                self._k_tis_property_unicode_key_layout_data
            )

            if not chr_data_ref:
                # 清理已分配的资源
                self._cf_release(self._keyboard_layout)
                self._keyboard_layout = None
                raise RuntimeError("无法获取键盘布局数据")

            # 获取键盘类型
            self._keyboard_type = self._lm_get_kbd_type()

            # 获取键盘布局数据的指针
            self._chr_data_ptr = self._cf_data_get_byte_ptr(chr_data_ref)

            # 创建 ctypes 的缓冲区用于 UCKeyTranslate
            self._buffer = (ctypes.c_uint16 * 8)()

            logger.debug("DirectTextInserter 初始化成功")

        except Exception as e:
            logger.error(f"DirectTextInserter 初始化失败: {e}", exc_info=True)
            raise

    def insert(self, text: str) -> None:
        """直接注入文本，不使用剪贴板

        Args:
            text: 要插入的文本

        Raises:
            ValueError: 如果文本包含无法转换的字符
            RuntimeError: 如果文本插入失败
        """
        if not text:
            return

        try:
            # 批量创建和发送事件，提高性能
            events = self._create_events_for_text(text)

            # 批量发送事件
            for event in events:
                self._cgevent_post(self._k_cg_session_event_tap, event)

            # 释放所有事件
            for event in events:
                self._cf_release(event)

            logger.debug(f"直接注入文本成功: {len(text)} 字符")

        except Exception as e:
            logger.error(f"直接注入文本失败: {e}", exc_info=True)
            raise RuntimeError(f"文本插入失败: {e}") from e

    def _create_events_for_text(self, text: str) -> List:
        """为文本批量创建键盘事件

        Args:
            text: 要创建事件的文本

        Returns:
            事件对象列表

        Raises:
            ValueError: 如果文本包含无法转换的字符
        """
        events = []

        try:
            for char in text:
                keycode, modifiers = self._unicode_to_keycode(ord(char))

                # 创建键盘按下事件
                press_event = self._cgevent_create_keyboard_event(None, keycode, True)
                if modifiers:
                    self._cgevent_set_flags(press_event, modifiers)

                # 创建键盘释放事件
                release_event = self._cgevent_create_keyboard_event(None, keycode, False)

                events.extend([press_event, release_event])

        except Exception as e:
            # 如果创建过程中出错，释放已创建的事件
            for event in events:
                try:
                    self._cf_release(event)
                except Exception:
                    pass
            raise

        return events

    def _unicode_to_keycode(self, code_point: int) -> Tuple[int, int]:
        """将 Unicode 码点转换为键码和修饰键

        Args:
            code_point: Unicode 码点

        Returns:
            (键码, 修饰键标志) 元组

        Raises:
            ValueError: 如果无法转换字符
        """
        # UCKeyTranslate 输出参数
        key_code = ctypes.c_uint16()
        modifiers = ctypes.c_uint32()

        # 调用 UCKeyTranslate
        result = self._uc_key_translate(
            self._chr_data_ptr,
            code_point,
            self._k_uc_key_action_down,
            0,  # 修饰键
            self._keyboard_type,
            self._k_uc_key_translate_no_dead_keys_bit,
            None,
            ctypes.byref(key_code),
            ctypes.byref(modifiers),
            None,
            self._buffer
        )

        if result != 0:
            error_msg = f"UCKeyTranslate 失败 (code={result}), 字符 U+{code_point:04X}"
            logger.error(error_msg)
            raise ValueError(error_msg)

        # 检查返回的键码是否有效
        if key_code.value == 0:
            # 某些字符可能无法通过键盘直接输入
            # 尝试使用备用方案或记录警告
            logger.warning(f"字符 U+{code_point:04X} 没有对应的键码，可能无法输入")

        return key_code.value, modifiers.value

    def close(self) -> None:
        """显式清理资源

        释放 Core Foundation 对象，防止内存泄漏。
        """
        if hasattr(self, '_keyboard_layout') and self._keyboard_layout is not None:
            try:
                self._cf_release(self._keyboard_layout)
                self._keyboard_layout = None
                logger.debug("DirectTextInserter 资源已清理")
            except Exception as e:
                logger.warning(f"清理 DirectTextInserter 资源时出错: {e}")

    def __del__(self) -> None:
        """析构函数，确保资源被释放"""
        self.close()

    def __enter__(self):
        """支持上下文管理器协议"""
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        """支持上下文管理器协议"""
        self.close()


class TextInserter:
    """使用剪贴板的文本插入器（回退方案）

    这是传统的文本插入方式：
    - 将文本写入剪贴板
    - 模拟 Cmd+V 粘贴
    - 会覆盖用户的剪贴板内容

    仅在 DirectTextInserter 不可用时使用。
    """

    def __init__(self):
        """初始化 TextInserter"""
        self._keyboard = Controller()
        logger.debug("使用剪贴板文本插入器")

    def insert(self, text: str) -> None:
        """插入文本到当前光标位置

        Args:
            text: 要插入的文本

        Raises:
            RuntimeError: 如果文本插入失败
        """
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
            logger.debug(f"剪贴板插入文本成功: {len(text)} 字符")

        except Exception as e:
            logger.error(f"剪贴板文本插入失败: {e}", exc_info=True)
            raise RuntimeError(f"文本插入失败: {e}") from e

    def close(self) -> None:
        """清理资源（当前无资源需要清理）"""
        pass


# 全局文本插入器实例
_inserter: Optional[Union[DirectTextInserter, TextInserter]] = None


def _create_inserter() -> Union[DirectTextInserter, TextInserter]:
    """创建文本插入器，优先使用直接注入

    Returns:
        文本插入器实例

    Raises:
        RuntimeError: 如果所有插入器都初始化失败
    """
    try:
        # 优先尝试使用 DirectTextInserter
        return DirectTextInserter()
    except Exception as e:
        logger.warning(f"DirectTextInserter 初始化失败，回退到剪贴板方案: {e}")
        try:
            return TextInserter()
        except Exception as e2:
            logger.error(f"TextInserter 初始化也失败: {e2}", exc_info=True)
            raise RuntimeError("无法初始化文本插入器") from e2


def insert_text(text: str) -> None:
    """插入文本到当前焦点位置

    优先使用直接注入（不使用剪贴板），失败则回退到剪贴板方案。
    此函数是线程安全的。

    Args:
        text: 要插入的文本

    Raises:
        RuntimeError: 如果文本插入失败
    """
    global _inserter

    # 线程安全的懒加载初始化（双重检查锁定）
    if _inserter is None:
        with _inserter_lock:
            if _inserter is None:
                _inserter = _create_inserter()

    if not text:
        return

    try:
        _inserter.insert(text)
        logger.debug(f"文本插入成功: {len(text)} 字符")
    except Exception as e:
        logger.error(f"文本插入失败: {e}", exc_info=True)

        # 如果使用的是 DirectTextInserter 且失败，尝试回退到剪贴板方案
        if isinstance(_inserter, DirectTextInserter):
            logger.info("尝试回退到剪贴板方案...")
            try:
                # 先清理 DirectTextInserter 的资源
                _inserter.close()

                # 切换到 TextInserter
                _inserter = TextInserter()
                _inserter.insert(text)
                logger.info("回退方案成功")
            except Exception as e2:
                logger.error(f"回退方案也失败: {e2}", exc_info=True)
                raise RuntimeError(f"文本插入失败（回退方案也失败）: {e2}") from e2
        else:
            raise


def cleanup() -> None:
    """清理文本插入器资源

    在应用退出时调用，确保资源被正确释放。
    """
    global _inserter

    if _inserter is not None:
        try:
            _inserter.close()
            _inserter = None
            logger.debug("文本插入器资源已清理")
        except Exception as e:
            logger.warning(f"清理文本插入器资源时出错: {e}")
