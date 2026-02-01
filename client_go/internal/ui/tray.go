package ui

import (
	"fmt"
	"os"
	"os/exec"


	"github.com/getlantern/systray"
	"github.com/yourusername/voice-typer/internal/config"
)

// TrayApp 系统托盘应用
type TrayApp struct {
	onToggle   func()
	onQuit     func()
	onAppReady func()

	// 菜单项
	mStatus *systray.MenuItem
	mStats  *systray.MenuItem
	mToggle *systray.MenuItem
	mConfig *systray.MenuItem
	mAbout  *systray.MenuItem
	mQuit   *systray.MenuItem
}

// NewTrayApp 创建托盘应用
func NewTrayApp(appName string, onToggle, onQuit, onAppReady func()) *TrayApp {
	return &TrayApp{
		onToggle:   onToggle,
		onQuit:     onQuit,
		onAppReady: onAppReady,
	}
}

// OnReady 系统托盘准备就绪回调
func (t *TrayApp) OnReady() {
	systray.SetTitle("VoiceTyper")
	systray.SetTooltip("Voice Typer")
	
	// 读取图标 (假设 assets/icon.ico 存在)
	// 在 systray 中通常需要 .ico (Windows) 或 .png (Mac)
	// 这里读取二进制数据
	if iconData, err := os.ReadFile("assets/icon.ico"); err == nil {
		systray.SetIcon(iconData)
	} else if iconData, err := os.ReadFile("assets/icon.png"); err == nil {
		systray.SetIcon(iconData)
	}

	t.mStatus = systray.AddMenuItem("Status: Initializing...", "Current status")
	t.mStatus.Disable()

	t.mStats = systray.AddMenuItem("Stats: 0 chars", "Usage statistics")
	t.mStats.Disable()

	systray.AddSeparator()

	t.mToggle = systray.AddMenuItem("Enable Voice Input", "Toggle voice input")
	
	systray.AddSeparator()
	
	t.mConfig = systray.AddMenuItem("Open Config", "Open configuration folder")
	t.mAbout = systray.AddMenuItem("About", "About VoiceTyper")
	
	systray.AddSeparator()
	
	t.mQuit = systray.AddMenuItem("Quit", "Quit the application")

	// 处理菜单事件
	go func() {
		for {
			select {
			case <-t.mToggle.ClickedCh:
				if t.onToggle != nil {
					t.onToggle()
				}
			case <-t.mConfig.ClickedCh:
				t.openConfig()
			case <-t.mAbout.ClickedCh:
				t.showAbout()
			case <-t.mQuit.ClickedCh:
				systray.Quit()
			}
		}
	}()

	if t.onAppReady != nil {
		t.onAppReady()
	}
}

// OnExit 系统托盘退出回调
func (t *TrayApp) OnExit() {
	if t.onQuit != nil {
		t.onQuit()
	}
}

// Run 运行托盘应用 (阻塞)
func (t *TrayApp) Run() {
	systray.Run(t.OnReady, t.OnExit)
}

// UpdateStatus 更新状态文本
func (t *TrayApp) UpdateStatus(status string) {
	t.mStatus.SetTitle(fmt.Sprintf("Status: %s", status))
	
	// 更新托盘图标/Tooltip (可选)
	systray.SetTooltip(fmt.Sprintf("VoiceTyper: %s", status))
}

// UpdateStats 更新统计
func (t *TrayApp) UpdateStats(inputCount, charCount int) {
	// 格式化：1.2k chars (123 times)
	charsStr := fmt.Sprintf("%d", charCount)
	if charCount > 10000 {
		charsStr = fmt.Sprintf("%.1fk", float64(charCount)/1000.0)
	}
	t.mStats.SetTitle(fmt.Sprintf("Stats: %s chars (%d times)", charsStr, inputCount))
}

// SetToggleState 设置启用/禁用状态
func (t *TrayApp) SetToggleState(enabled bool, hotkey string) {
	if enabled {
		t.mToggle.SetTitle(fmt.Sprintf("Disable (%s)", hotkey))
		t.mToggle.Check()
	} else {
		t.mToggle.SetTitle(fmt.Sprintf("Enable (%s)", hotkey))
		t.mToggle.Uncheck()
	}
}

// Quit 退出应用
func (t *TrayApp) Quit() {
	systray.Quit()
}

func (t *TrayApp) openConfig() {
	configDir, _ := config.GetConfigDir()
	// Windows Explorer
	exec.Command("explorer", configDir).Start()
}

func (t *TrayApp) showAbout() {
	// 使用 MessageBox (Win32)
	ShowMessageBox("VoiceTyper", "Voice Input Client v1.3.1\n\nGo Implementation for Windows")
}
