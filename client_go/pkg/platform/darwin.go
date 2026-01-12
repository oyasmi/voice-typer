//go:build darwin

package platform

// GetDefaultHotkeyModifiers 获取macOS默认热键修饰键
func GetDefaultHotkeyModifiers() []string {
	return []string{"cmd"}
}

// GetPasteShortcut 获取macOS粘贴快捷键
func GetPasteShortcut() (string, string) {
	return "cmd", "v" // Cmd+V
}
