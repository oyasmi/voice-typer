// Package platform provides Windows-specific platform utilities.
package platform

import (
	"os"
	"path/filepath"
)

// GetConfigDir returns the Windows config directory path.
func GetConfigDir() (string, error) {
	appData := os.Getenv("APPDATA")
	if appData == "" {
		return "", os.ErrNotExist
	}
	configDir := filepath.Join(appData, "voice-typer")
	return configDir, nil
}
