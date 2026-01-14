"""
全局热键监听器 (Linux Wayland)
使用 evdev 库直接从 /dev/input/event* 读取键盘事件
"""
import glob
import threading
from select import select
from typing import Callable, Set, List, Optional

from evdev import InputDevice, list_devices, ecodes, categorize


class HotkeyListener:
    """热键监听器 - Linux evdev 实现"""

    # 修饰键映射 (Linux 使用 KEY_LEFTCTRL 等)
    MODIFIER_KEYS = {
        'ctrl': {ecodes.KEY_LEFTCTRL, ecodes.KEY_RIGHTCTRL},
        'alt': {ecodes.KEY_LEFTALT, ecodes.KEY_RIGHTALT},
        'shift': {ecodes.KEY_LEFTSHIFT, ecodes.KEY_RIGHTSHIFT},
        'super': {ecodes.KEY_LEFTMETA, ecodes.KEY_RIGHTMETA},
        'meta': {ecodes.KEY_LEFTMETA, ecodes.KEY_RIGHTMETA},
    }

    # 特殊键映射
    SPECIAL_KEYS = {
        'space': ecodes.KEY_SPACE,
        'tab': ecodes.KEY_TAB,
        'enter': ecodes.KEY_ENTER,
        'return': ecodes.KEY_ENTER,
        'escape': ecodes.KEY_ESC,
        'esc': ecodes.KEY_ESC,
        'backspace': ecodes.KEY_BACKSPACE,
        'delete': ecodes.KEY_DELETE,
        'f1': ecodes.KEY_F1,
        'f2': ecodes.KEY_F2,
        'f3': ecodes.KEY_F3,
        'f4': ecodes.KEY_F4,
        'f5': ecodes.KEY_F5,
        'f6': ecodes.KEY_F6,
        'f7': ecodes.KEY_F7,
        'f8': ecodes.KEY_F8,
        'f9': ecodes.KEY_F9,
        'f10': ecodes.KEY_F10,
        'f11': ecodes.KEY_F11,
        'f12': ecodes.KEY_F12,
    }

    def __init__(
        self,
        modifiers: List[str],
        key: str,
        on_press: Callable[[], None],
        on_release: Callable[[], None]
    ):
        """
        初始化热键监听器

        Args:
            modifiers: 修饰键列表，如 ['ctrl', 'shift']
            key: 主键名称，如 'space', 'a', 'f1'
            on_press: 热键按下时的回调
            on_release: 热键释放时的回调
        """
        self.on_press_callback = on_press
        self.on_release_callback = on_release

        # 解析修饰键
        self.required_modifiers: Set[str] = set()
        for mod in modifiers:
            m = mod.lower()
            if m in ('ctrl', 'control'):
                self.required_modifiers.add('ctrl')
            elif m in ('alt', 'option'):
                self.required_modifiers.add('alt')
            elif m == 'shift':
                self.required_modifiers.add('shift')
            elif m in ('super', 'meta', 'cmd', 'command'):
                self.required_modifiers.add('super')

        # 解析主键
        self.target_key = self._parse_key(key)

        # 状态跟踪
        self._pressed_keys: Set[int] = set()
        self._hotkey_active = False
        self._running = False
        self._thread: Optional[threading.Thread] = None
        self._devices: List[InputDevice] = []

    def _parse_key(self, key: str) -> int:
        """解析键名为 evdev event code"""
        k = key.lower()

        # 检查特殊键
        if k in self.SPECIAL_KEYS:
            return self.SPECIAL_KEYS[k]

        # 检查字母键 (KEY_A - KEY_Z)
        if len(k) == 1 and k.isalpha():
            return getattr(ecodes, f'KEY_{k.upper()}')

        # 检查数字键 (KEY_1 - KEY_9)
        if len(k) == 1 and k.isdigit():
            return getattr(ecodes, f'KEY_{k}')

        # 检查其他命名键 (如 KEY_UP, KEY_DOWN 等)
        key_name = f'KEY_{k.upper()}'
        if hasattr(ecodes, key_name):
            return getattr(ecodes, key_name)

        raise ValueError(f"Unknown key: {key}")

    def _find_keyboard_devices(self) -> List[InputDevice]:
        """查找所有键盘设备"""
        devices = []
        for path in glob.glob('/dev/input/event*'):
            try:
                device = InputDevice(path)
                # 检查设备是否有 EV_KEY 能力（键盘特征）
                if ecodes.EV_KEY in device.capabilities():
                    # 进一步过滤，排除鼠标、触摸板等
                    capabilities = device.capabilities()[ecodes.EV_KEY]
                    # 键盘通常有字母键，鼠标没有
                    if ecodes.KEY_A in capabilities or ecodes.KEY_Q in capabilities:
                        devices.append(device)
            except (PermissionError, OSError):
                # 无权限访问设备或设备不存在
                continue
        return devices

    def _get_pressed_modifiers(self) -> Set[str]:
        """获取当前按下的修饰键集合"""
        mods = set()
        for keycode in self._pressed_keys:
            for name, keycodes in self.MODIFIER_KEYS.items():
                if keycode in keycodes:
                    if name in ('ctrl', 'control'):
                        mods.add('ctrl')
                    elif name in ('alt', 'option'):
                        mods.add('alt')
                    elif name == 'shift':
                        mods.add('shift')
                    elif name in ('super', 'meta', 'cmd', 'command'):
                        mods.add('super')
        return mods

    def _handle_key_event(self, event):
        """处理键盘事件"""
        if event.type != ecodes.EV_KEY:
            return

        keycode = event.code
        value = event.value  # 0: 释放, 1: 按下, 2: 自动重复

        # 跟踪按键状态
        if value == 1:  # 按下
            self._pressed_keys.add(keycode)
        elif value == 0:  # 释放
            self._pressed_keys.discard(keycode)
        else:  # 自动重复
            return

        # 检查是否是目标键
        if keycode == self.target_key:
            # 检查修饰键是否匹配
            current_mods = self._get_pressed_modifiers()

            if value == 1:  # 按下
                if current_mods == self.required_modifiers and not self._hotkey_active:
                    self._hotkey_active = True
                    try:
                        self.on_press_callback()
                    except Exception as e:
                        print(f"热键回调错误: {e}")

            elif value == 0:  # 释放
                if self._hotkey_active:
                    self._hotkey_active = False
                    try:
                        self.on_release_callback()
                    except Exception as e:
                        print(f"热键释放错误: {e}")

    def _event_loop(self):
        """事件循环 - 在独立线程中运行"""
        # 打开所有键盘设备
        self._devices = self._find_keyboard_devices()

        if not self._devices:
            print("警告: 未检测到键盘设备，请检查 udev 权限配置")
            print("运行: sudo usermod -aG input $USER")
            print("然后注销并重新登录")
            return

        # 将设备文件描述符设置为非阻塞模式
        device_fds = [device.fd for device in self._devices]

        print(f"热键监听已启动，检测到 {len(self._devices)} 个键盘设备")

        while self._running:
            # 使用 select 等待设备可读
            r, _, _ = select(device_fds, [], [], 0.1)

            for fd in r:
                try:
                    # 找到对应的设备
                    device = next((d for d in self._devices if d.fd == fd), None)
                    if device:
                        # 读取事件
                        for event in device.read():
                            self._handle_key_event(event)
                except (OSError, BlockingIOError):
                    # 设备可能被拔出
                    continue

    def start(self):
        """启动热键监听"""
        if self._running:
            return

        self._running = True
        self._thread = threading.Thread(target=self._event_loop, daemon=True)
        self._thread.start()

    def stop(self):
        """停止热键监听"""
        self._running = False
        if self._thread:
            self._thread.join(timeout=1.0)
            self._thread = None

        # 关闭所有设备
        for device in self._devices:
            try:
                device.close()
            except Exception:
                pass
        self._devices.clear()
