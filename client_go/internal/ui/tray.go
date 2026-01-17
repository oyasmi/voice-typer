package ui

import (
	"fmt"

	"fyne.io/fyne/v2/app"
	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/driver/desktop"
)

// TrayApp 系统托盘应用
type TrayApp struct {
	app     fyne.App
	menu    *fyne.Menu
	onQuit  func()
	onToggle func()

	// 菜单项
	statusItem *fyne.MenuItem
	toggleItem *fyne.MenuItem
}

// NewTrayApp 创建托盘应用
func NewTrayApp(appName string, onToggle, onQuit func()) *TrayApp {
	a := app.NewWithID("com.voicetyper.app")

	// 设置应用图标（可选）
	// 图标文件应与可执行文件在同一目录
	// if resource, err := fyne.LoadResourceFromPath("icon.png"); err == nil {
	// 	a.SetIcon(resource)
	// }

	tray := &TrayApp{
		app:      a,
		onToggle: onToggle,
		onQuit:   onQuit,
	}

	tray.setupMenu()

	return tray
}

// setupMenu 设置托盘菜单
func (t *TrayApp) setupMenu() {
	t.statusItem = fyne.NewMenuItem("Status: Initializing...", nil)
	t.statusItem.Disabled = true

	t.toggleItem = fyne.NewMenuItem("Enable Voice Input", func() {
		if t.onToggle != nil {
			t.onToggle()
		}
	})

	quitItem := fyne.NewMenuItem("Quit", func() {
		if t.onQuit != nil {
			t.onQuit()
		}
		t.app.Quit()
	})

	t.menu = fyne.NewMenu("VoiceTyper",
		t.statusItem,
		fyne.NewMenuItemSeparator(),
		t.toggleItem,
		fyne.NewMenuItemSeparator(),
		quitItem,
	)
}

// Run 运行托盘应用（阻塞）
func (t *TrayApp) Run() {
	if desk, ok := t.app.(desktop.App); ok {
		desk.SetSystemTrayMenu(t.menu)
	}

	t.app.Run()
}

// UpdateStatus 更新状态文本
func (t *TrayApp) UpdateStatus(status string) {
	fyne.Do(func() {
		t.statusItem.Label = fmt.Sprintf("Status: %s", status)
		t.menu.Refresh()
	})
}

// SetToggleState 设置启用/禁用状态
func (t *TrayApp) SetToggleState(enabled bool, hotkey string) {
	fyne.Do(func() {
		if enabled {
			t.toggleItem.Label = fmt.Sprintf("Disable (%s)", hotkey)
		} else {
			t.toggleItem.Label = fmt.Sprintf("Enable (%s)", hotkey)
		}
		t.menu.Refresh()
	})
}

// Quit 退出应用
func (t *TrayApp) Quit() {
	fyne.Do(func() {
		t.app.Quit()
	})
}
