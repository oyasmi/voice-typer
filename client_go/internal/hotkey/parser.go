package hotkey

import (
	"fmt"
	"strings"

	"github.com/go-vgo/robotgo"
)

// Modifiers 修饰键类型
type Modifiers struct {
	Ctrl  bool
	Alt   bool
	Shift bool
	Cmd   bool // macOS Command / Windows Windows键
}

// KeyCode 键码定义
type KeyCode int

// 常用键码（robotgo的键码）
const (
	KeySpace KeyCode = 49
	KeyTab   KeyCode = 48
	KeyEnter KeyCode = 36

	// F键
	KeyF1  KeyCode = 122
	KeyF2  KeyCode = 120
	KeyF3  KeyCode = 99
	KeyF4  KeyCode = 118
	KeyF5  KeyCode = 96
	KeyF6  KeyCode = 97
	KeyF7  KeyCode = 98
	KeyF8  KeyCode = 100
	KeyF9  KeyCode = 101
	KeyF10 KeyCode = 109
	KeyF11 KeyCode = 103
	KeyF12 KeyCode = 111
)

// ParseHotkey 解析热键配置
func ParseHotkey(modifiers []string, key string) (*Modifiers, KeyCode, error) {
	mods := &Modifiers{}

	// 解析修饰键
	for _, mod := range modifiers {
		mod = strings.ToLower(strings.TrimSpace(mod))
		switch mod {
		case "ctrl", "control":
			mods.Ctrl = true
		case "alt", "option":
			mods.Alt = true
		case "shift":
			mods.Shift = true
		case "cmd", "command":
			mods.Cmd = true
		default:
			return nil, 0, fmt.Errorf("unknown modifier: %s", mod)
		}
	}

	// 解析主键
	key = strings.ToLower(strings.TrimSpace(key))
	var keyCode KeyCode

	switch key {
	case "space":
		keyCode = KeySpace
	case "tab":
		keyCode = KeyTab
	case "enter", "return":
		keyCode = KeyEnter
	case "f1":
		keyCode = KeyF1
	case "f2":
		keyCode = KeyF2
	case "f3":
		keyCode = KeyF3
	case "f4":
		keyCode = KeyF4
	case "f5":
		keyCode = KeyF5
	case "f6":
		keyCode = KeyF6
	case "f7":
		keyCode = KeyF7
	case "f8":
		keyCode = KeyF8
	case "f9":
		keyCode = KeyF9
	case "f10":
		keyCode = KeyF10
	case "f11":
		keyCode = KeyF11
	case "f12":
		keyCode = KeyF12
	default:
		// 尝试作为字符键
		if len(key) == 1 {
			char := key[0]
			if (char >= 'a' && char <= 'z') || (char >= '0' && char <= '9') {
				// 字符键的键码需要通过robotgo获取
				keyCode = KeyCode(robotgo.Keycode[key])
				if keyCode == 0 {
					return nil, 0, fmt.Errorf("unsupported key: %s", key)
				}
			} else {
				return nil, 0, fmt.Errorf("unsupported key: %s", key)
			}
		} else {
			return nil, 0, fmt.Errorf("unsupported key: %s", key)
		}
	}

	return mods, keyCode, nil
}

// Match 检查当前按键事件是否匹配热键
func Match(event robotgo.Event, mods *Modifiers, keyCode KeyCode) bool {
	// 检查修饰键
	if mods.Ctrl && !isCtrlPressed(event) {
		return false
	}
	if mods.Alt && !isAltPressed(event) {
		return false
	}
	if mods.Shift && !isShiftPressed(event) {
		return false
	}
	if mods.Cmd && !isCmdPressed(event) {
		return false
	}

	// 检查主键
	return event.Keycode == uint16(keyCode)
}

// 辅助函数：检查修饰键状态
// 注意：这些是简化实现，可能需要根据实际测试调整
func isCtrlPressed(event robotgo.Event) bool {
	// robotgo的Rawcode可能包含修饰键信息
	// 具体实现依赖平台
	return event.Ctrl
}

func isAltPressed(event robotgo.Event) bool {
	return event.Alt
}

func isShiftPressed(event robotgo.Event) bool {
	return event.Shift
}

func isCmdPressed(event robotgo.Event) bool {
	// macOS的Command键在robotgo中可能是Meta或Cmd
	return event.Meta || event.Cmd
}
