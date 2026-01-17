package hotkey

import (
	"fmt"
	"strings"
)

// Modifiers 修饰键类型
type Modifiers struct {
	Ctrl  bool
	Alt   bool
	Shift bool
	Win   bool // Windows 键
}

// ParseHotkey 解析热键配置
func ParseHotkey(modifiers []string, key string) (*Modifiers, string, error) {
	mods := &Modifiers{}

	// 解析修饰键
	for _, mod := range modifiers {
		mod = strings.ToLower(strings.TrimSpace(mod))
		switch mod {
		case "ctrl", "control":
			mods.Ctrl = true
		case "alt":
			mods.Alt = true
		case "shift":
			mods.Shift = true
		case "win", "windows":
			mods.Win = true
		default:
			return nil, "", fmt.Errorf("unknown modifier: %s", mod)
		}
	}

	// 解析主键
	key = strings.ToLower(strings.TrimSpace(key))

	// 验证键名是否有效
	validKeys := map[string]bool{
		// 字母键
		"a": true, "b": true, "c": true, "d": true, "e": true, "f": true, "g": true,
		"h": true, "i": true, "j": true, "k": true, "l": true, "m": true, "n": true,
		"o": true, "p": true, "q": true, "r": true, "s": true, "t": true, "u": true,
		"v": true, "w": true, "x": true, "y": true, "z": true,
		// 数字键
		"0": true, "1": true, "2": true, "3": true, "4": true,
		"5": true, "6": true, "7": true, "8": true, "9": true,
		// 功能键
		"space": true, "tab": true, "enter": true, "return": true,
		"f1": true, "f2": true, "f3": true, "f4": true, "f5": true,
		"f6": true, "f7": true, "f8": true, "f9": true, "f10": true,
		"f11": true, "f12": true,
	}

	if !validKeys[key] {
		return nil, "", fmt.Errorf("unsupported key: %s", key)
	}

	return mods, key, nil
}
