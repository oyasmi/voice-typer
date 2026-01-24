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

	// 提前声明变量以用于闭包
	var ctrl *controller.Controller
	var tray *ui.TrayApp

	// 2. 定义回调
	onToggle := func() {
		if ctrl == nil {
			return
		}
		if ctrl.IsEnabled() {
			if err := ctrl.Stop(); err != nil {
				log.Printf("Error stopping controller: %v", err)
			}
			tray.UpdateStatus("Disabled")
			tray.SetToggleState(false, getHotkeyString(cfg))
		} else {
			if err := ctrl.Start(); err != nil {
				log.Printf("Error starting controller: %v", err)
				ui.ShowMessageBox("Start Error", err.Error())
			} else {
				tray.UpdateStatus("Enabled")
				tray.SetToggleState(true, getHotkeyString(cfg))
			}
		}
	}

	onQuit := func() {
		log.Println("Quitting...")
		if ctrl != nil {
			if err := ctrl.Close(); err != nil {
				log.Printf("Error closing controller: %v", err)
			}
		}
	}

	// 3. 定义初始化逻辑 (在托盘就绪后运行)
	onReady := func() {
		go func() {
			var err error
			ctrl, err = controller.NewController(cfg)
			if err != nil {
				log.Fatalf("Failed to create controller: %v", err)
			}

			// 初始化控制器
			// 状态变更时同时也更新统计数据
			err = ctrl.Initialize(func(status string) {
				tray.UpdateStatus(status)
				
				// 更新统计
				in, ch := ctrl.GetStats()
				tray.UpdateStats(in, ch)
				
				log.Printf("Status: %s", status)
			})

			if err != nil {
				log.Printf("Failed to initialize controller: %v", err)
				ui.ShowMessageBox("Init Error", fmt.Sprintf("Failed to initialize: %v", err))
				os.Exit(1)
			}

			// 自动启用
			if err := ctrl.Start(); err != nil {
				log.Printf("Warning: auto-start failed: %v", err)
				ui.ShowMessageBox("Start Error", err.Error())
			} else {
				tray.SetToggleState(true, getHotkeyString(cfg))
				// tray.UpdateStatus("Ready") // Initialize already sets Ready
			}
		}()
	}

	// 4. 创建托盘应用
	tray = ui.NewTrayApp("VoiceTyper", onToggle, onQuit, onReady)

	// 5. 设置信号处理
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)

	go func() {
		<-sigChan
		fmt.Println("\nShutting down...")
		tray.Quit() // 这会通过 channel 停止 systray 循环，触发 OnExit -> onQuit
	}()

	// 6. 运行应用 (阻塞)
	tray.Run()

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
