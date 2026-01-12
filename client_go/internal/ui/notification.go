package ui

import (
	"github.com/gen2brain/beeep"
)

// Notify 显示系统通知
func Notify(title, message string) error {
	return beeep.Notify(title, message, "")
}

// NotifyWithIcon 显示带图标的系统通知
func NotifyWithIcon(title, message, iconPath string) error {
	return beeep.Notify(title, message, iconPath)
}
