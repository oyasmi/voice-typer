"""
热键监听器 - Windows版本
支持 Windows 键 (Win_L/Win_R)
"""
from typing import Optional, Callable, Set
import logging
from pynput import keyboard


logger = logging.getLogger("VoiceTyper")

class HotkeyListener:
    """热键监听器"""

    MODIFIER_KEYS = {
        'ctrl': {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r},
        'alt': {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r},
        'shift': {keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r},
        'cmd': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},  # macOS
        'win': {keyboard.Key.cmd, keyboard.Key.cmd_l, keyboard.Key.cmd_r},  # Windows (mapped to cmd in pynput)
        'win_l': {keyboard.Key.cmd_l},  # Windows左键
        'win_r': {keyboard.Key.cmd_r},  # Windows右键
    }

    SPECIAL_KEYS = {
        'space': keyboard.Key.space,
        'tab': keyboard.Key.tab,
        'enter': keyboard.Key.enter,
        'f1': keyboard.Key.f1, 'f2': keyboard.Key.f2, 'f3': keyboard.Key.f3,
        'f4': keyboard.Key.f4, 'f5': keyboard.Key.f5, 'f6': keyboard.Key.f6,
        'f7': keyboard.Key.f7, 'f8': keyboard.Key.f8, 'f9': keyboard.Key.f9,
        'f10': keyboard.Key.f10, 'f11': keyboard.Key.f11, 'f12': keyboard.Key.f12,
    }

    def __init__(self, modifiers: list, key: str, on_press: Callable, on_release: Callable):
        self.on_press_callback = on_press
        self.on_release_callback = on_release

        # 解析修饰键
        self.required_modifiers: Set[str] = set()
        for mod in modifiers:
            m = mod.lower()
            if m in ('ctrl', 'control'):
                self.required_modifiers.add('ctrl')
            elif m == 'alt':
                self.required_modifiers.add('alt')
            elif m == 'shift':
                self.required_modifiers.add('shift')
            elif m in ('cmd', 'command', 'win'):
                # cmd在Windows上对应Win键
                self.required_modifiers.add('win')
            elif m in ('win_l', 'win_r'):
                # 具体的左/右Win键
                self.required_modifiers.add(m)

        # 解析主键
        k = key.lower()
        self.target_key = self.SPECIAL_KEYS.get(k, keyboard.KeyCode.from_char(k))

        self._pressed_keys: Set = set()
        self._hotkey_active = False
        self._listener = None
        self._stopped = False

    def _get_pressed_modifiers(self) -> Set[str]:
        """获取当前按下的修饰键"""
        mods = set()
        for key in self._pressed_keys:
            # 检查具体的键
            if key == keyboard.Key.cmd_l:
                mods.add('win_l')
                mods.add('win')  # 也添加通用的win标记
            elif key == keyboard.Key.cmd_r:
                mods.add('win_r')
                mods.add('win')  # 也添加通用的win标记
            elif key in (keyboard.Key.cmd,):
                # pynput在Windows上可能使用通用的cmd
                mods.add('win')

            # 检查其他修饰键
            for name, keys in self.MODIFIER_KEYS.items():
                if key in keys:
                    if name in ('ctrl', 'control'):
                        mods.add('ctrl')
                    elif name == 'alt':
                        mods.add('alt')
                    elif name == 'shift':
                        mods.add('shift')
        return mods

    def _is_target_key(self, key) -> bool:
        """检查是否是目标键"""
        if key == self.target_key:
            return True
        try:
            if hasattr(key, 'char') and key.char:
                target_str = str(self.target_key).replace("'", "").lower()
                key_str = key.char.lower()
                return key_str == target_str
        except:
            pass
        return False

    def _on_press(self, key):
        """按键按下事件"""
        if self._stopped:
            return False
        self._pressed_keys.add(key)

        if not self._hotkey_active:
            pressed_mods = self._get_pressed_modifiers()

            # 检查修饰键是否匹配
            # 需要精确匹配: 如果配置的是win_l，则只能用左Win键
            modifiers_match = True

            # 构建期望的修饰键集合
            expected_mods = set()
            for mod in self.required_modifiers:
                if mod == 'win':
                    # 通用win，接受左或右
                    expected_mods.update(['win_l', 'win_r', 'win'])
                elif mod in ('win_l', 'win_r'):
                    expected_mods.add(mod)
                else:
                    expected_mods.add(mod)

            # 检查当前按下的键是否包含所有要求的修饰键
            for req in self.required_modifiers:
                if req == 'win':
                    # 需要任意Win键
                    if not ('win_l' in pressed_mods or 'win_r' in pressed_mods or 'win' in pressed_mods):
                        modifiers_match = False
                        break
                elif req == 'win_l':
                    if 'win_l' not in pressed_mods:
                        modifiers_match = False
                        break
                elif req == 'win_r':
                    if 'win_r' not in pressed_mods:
                        modifiers_match = False
                        break
                elif req not in pressed_mods:
                    modifiers_match = False
                    break

            if modifiers_match and self._is_target_key(key):
                self._hotkey_active = True
                try:
                    self.on_press_callback()
                except Exception as e:
                    logger.error(f"热键回调错误: {e}")

    def _on_release(self, key):
        """按键释放事件"""
        if self._stopped:
            return False
        self._pressed_keys.discard(key)

        if self._hotkey_active and self._is_target_key(key):
            self._hotkey_active = False
            try:
                self.on_release_callback()
            except Exception as e:
                logger.error(f"热键释放错误: {e}")

    def start(self):
        """启动热键监听"""
        self._stopped = False
        self._listener = keyboard.Listener(on_press=self._on_press, on_release=self._on_release)
        self._listener.start()

    def stop(self):
        """停止热键监听"""
        self._stopped = True
        if self._listener:
            self._listener.stop()
            self._listener = None
