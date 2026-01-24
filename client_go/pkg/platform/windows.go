//go:build windows

package platform

import "os"

// GetDefaultHotkeyModifiers 获取Windows默认热键修饰键
func GetDefaultHotkeyModifiers() []string {
	return []string{"ctrl"}
}

// GetPasteShortcut 获取Windows粘贴快捷键
func GetPasteShortcut() (string, string) {
	return "ctrl", "v" // Ctrl+V
}

// GetConfigDir 获取配置目录
func GetConfigDir() (string, error) {
	return os.UserConfigDir()
}
