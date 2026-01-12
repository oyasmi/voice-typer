//go:build linux

package platform

import (
	"os"
)

// GetDefaultHotkeyModifiers 获取Linux默认热键修饰键
func GetDefaultHotkeyModifiers() []string {
	return []string{"ctrl"}
}

// GetPasteShortcut 获取Linux粘贴快捷键
func GetPasteShortcut() (string, string) {
	return "ctrl", "v" // Ctrl+V
}

// CheckYdotool 检测ydotool是否可用（Wayland需要）
func CheckYdotool() bool {
	_, err := os.Stat("/usr/bin/ydotool")
	return err == nil
}
