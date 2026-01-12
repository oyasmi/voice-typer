package main

import (
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/yourusername/voice-typer/internal/config"
	"github.com/yourusername/voice-typer/internal/controller"
	"github.com/yourusername/voice-typer/internal/ui"
)

func main() {
	// 1. 加载配置
	cfg, err := config.Load("")
	if err != nil {
		log.Fatalf("Failed to load config: %v", err)
	}

	// 创建默认词库文件
	if err := config.CreateDefaultHotwordsFile(); err != nil {
		log.Printf("Warning: failed to create default hotwords file: %v", err)
	}

	// 2. 初始化控制器
	ctrl, err := controller.NewController(cfg)
	if err != nil {
		log.Fatalf("Failed to create controller: %v", err)
	}

	// 3. 创建托盘应用
	tray := ui.NewTrayApp("VoiceTyper",
		func() { // onToggle
			if ctrl.IsEnabled() {
				if err := ctrl.Stop(); err != nil {
					log.Printf("Error stopping controller: %v", err)
				}
				tray.UpdateStatus("Disabled")
				tray.SetToggleState(false, getHotkeyString(cfg))
			} else {
				if err := ctrl.Start(); err != nil {
					log.Printf("Error starting controller: %v", err)
					ui.Notify("Start Error", err.Error())
				} else {
					tray.UpdateStatus("Enabled")
					tray.SetToggleState(true, getHotkeyString(cfg))
				}
			}
		},
		func() { // onQuit
			log.Println("Quitting...")
			if err := ctrl.Close(); err != nil {
				log.Printf("Error closing controller: %v", err)
			}
		},
	)

	// 4. 初始化控制器（带状态回调）
	err = ctrl.Initialize(func(status string) {
		tray.UpdateStatus(status)
		log.Printf("Status: %s", status)
	})

	if err != nil {
		log.Fatalf("Failed to initialize controller: %v", err)
		os.Exit(1)
	}

	// 5. 自动启用
	if err := ctrl.Start(); err != nil {
		log.Printf("Warning: auto-start failed: %v", err)
		ui.Notify("Start Error", err.Error())
	} else {
		tray.SetToggleState(true, getHotkeyString(cfg))
		ui.Notify("VoiceTyper", "Voice input enabled")
	}

	// 6. 设置信号处理
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)

	// 7. 启动应用（在goroutine中）
	go func() {
		tray.Run()
	}()

	// 8. 等待退出信号
	<-sigChan
	fmt.Println("\nShutting down...")

	// 清理资源
	if err := ctrl.Close(); err != nil {
		log.Printf("Error closing controller: %v", err)
	}

	tray.Quit()
	fmt.Println("Goodbye!")
}

// getHotkeyString 获取热键字符串用于显示
func getHotkeyString(cfg *config.Config) string {
	mods := ""
	for _, m := range cfg.Hotkey.Modifiers {
		mods += m + "+"
	}
	return mods + cfg.Hotkey.Key
}
