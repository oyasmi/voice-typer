package platform

import (
	"os"
	"path/filepath"
	"runtime"
)

// GetConfigDir 获取配置目录路径
func GetConfigDir() (string, error) {
	var configDir string

	if runtime.GOOS == "windows" {
		appData := os.Getenv("APPDATA")
		if appData == "" {
			return "", os.ErrNotExist
		}
		configDir = filepath.Join(appData, "voice-typer")
	} else {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		configDir = filepath.Join(home, ".config", "voice-typer")
	}

	return configDir, nil
}

// IsLinux 检测是否为Linux系统
func IsLinux() bool {
	return runtime.GOOS == "linux"
}

// IsDarwin 检测是否为macOS系统
func IsDarwin() bool {
	return runtime.GOOS == "darwin"
}

// IsWindows 检测是否为Windows系统
func IsWindows() bool {
	return runtime.GOOS == "windows"
}

// IsWayland 检测是否运行在Wayland环境下
func IsWayland() bool {
	if !IsLinux() {
		return false
	}
	return os.Getenv("WAYLAND_DISPLAY") != ""
}

// IsX11 检测是否运行在X11环境下
func IsX11() bool {
	if !IsLinux() {
		return false
	}
	return os.Getenv("DISPLAY") != ""
}
