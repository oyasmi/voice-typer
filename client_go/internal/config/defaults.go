package config

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/yourusername/voice-typer/pkg/platform"
)

// GetDefaultConfig 获取默认配置
func GetDefaultConfig() *Config {
	return &Config{
		Servers: []ServerConfig{
			{
				Name:         "local",
				Host:         "127.0.0.1",
				Port:         6008,
				Timeout:      30.0,
				APIKey:       "",
				LLMRecorrect: false,
			},
		},
		Hotkey: HotkeyConfig{
			Modifiers: []string{"cmd"},
			Key:       "space",
		},
		UI: UIConfig{
			Opacity: 0.85,
			Width:   240,
			Height:  70,
		},
		HotwordFiles: []string{"hotwords.txt"},
		Input: InputConfig{
			Method: "clipboard",
		},
	}
}

// CreateDefaultHotwordsFile 创建默认词库文件
func CreateDefaultHotwordsFile() error {
	configDir, err := platform.GetConfigDir()
	if err != nil {
		return err
	}

	hotwordsPath := filepath.Join(configDir, "hotwords.txt")

	// 如果文件已存在，不覆盖
	if _, err := os.Stat(hotwordsPath); err == nil {
		return nil
	}

	defaultContent := `# VoiceTyper Custom Hotwords
# One word per line, supports Chinese and English
# Lines starting with # are comments

# Technology terms example
FunASR
Python
GitHub
OpenAI
ChatGPT

# Add your custom words below...
`

	if err := os.WriteFile(hotwordsPath, []byte(defaultContent), 0644); err != nil {
		return fmt.Errorf("create hotwords file: %w", err)
	}

	return nil
}
