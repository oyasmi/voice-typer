package config

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// Config 应用配置
type Config struct {
	Servers      []ServerConfig `yaml:"servers"`
	Hotkey       HotkeyConfig   `yaml:"hotkey"`
	UI           UIConfig       `yaml:"ui"`
	HotwordFiles []string       `yaml:"hotword_files"`
	Input        InputConfig    `yaml:"input"`
}

// ServerConfig 服务器配置
type ServerConfig struct {
	Name         string  `yaml:"name"`
	Host         string  `yaml:"host"`
	Port         int     `yaml:"port"`
	Timeout      float64 `yaml:"timeout"`
	APIKey       string  `yaml:"api_key"`
	LLMRecorrect bool    `yaml:"llm_recorrect"`
}

// HotkeyConfig 热键配置
type HotkeyConfig struct {
	Modifiers []string `yaml:"modifiers"`
	Key       string   `yaml:"key"`
}

// UIConfig UI配置
type UIConfig struct {
	Opacity float64 `yaml:"opacity"`
	Width   int     `yaml:"width"`
	Height  int     `yaml:"height"`
}

// InputConfig 输入配置
type InputConfig struct {
	Method string `yaml:"method"`
}

// GetConfigDir 获取配置目录路径 (Windows: %APPDATA%\voice-typer)
func GetConfigDir() (string, error) {
	appData := os.Getenv("APPDATA")
	if appData == "" {
		return "", fmt.Errorf("APPDATA environment variable not set")
	}
	configDir := filepath.Join(appData, "voice-typer")
	return configDir, nil
}

// GetConfigPath 获取配置文件路径
func GetConfigPath() (string, error) {
	dir, err := GetConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "config.yaml"), nil
}

// EnsureConfigDir 确保配置目录存在
func EnsureConfigDir() error {
	dir, err := GetConfigDir()
	if err != nil {
		return err
	}

	if _, err := os.Stat(dir); os.IsNotExist(err) {
		if err := os.MkdirAll(dir, 0755); err != nil {
			return fmt.Errorf("create config dir: %w", err)
		}
	}

	return nil
}

// Load 加载配置文件
func Load(path string) (*Config, error) {
	if path == "" {
		var err error
		path, err = GetConfigPath()
		if err != nil {
			return nil, err
		}
	}

	// 如果配置文件不存在，创建默认配置
	if _, err := os.Stat(path); os.IsNotExist(err) {
		if err := EnsureConfigDir(); err != nil {
			return nil, err
		}

		defaultConfig := GetDefaultConfig()
		if err := Save(defaultConfig, path); err != nil {
			return nil, fmt.Errorf("create default config: %w", err)
		}

		fmt.Printf("Created default config at: %s\n", path)
		return defaultConfig, nil
	}

	// 读取配置文件
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read config file: %w", err)
	}

	// 解析YAML
	var config Config
	if err := yaml.Unmarshal(data, &config); err != nil {
		return nil, fmt.Errorf("parse config: %w", err)
	}

	// 验证配置
	if err := config.Validate(); err != nil {
		return nil, fmt.Errorf("invalid config: %w", err)
	}

	// 处理词库文件相对路径
	if err := config.ResolveHotwordPaths(); err != nil {
		return nil, err
	}

	return &config, nil
}

// Save 保存配置到文件
func Save(config *Config, path string) error {
	data, err := yaml.Marshal(config)
	if err != nil {
		return fmt.Errorf("marshal config: %w", err)
	}

	if err := os.WriteFile(path, data, 0644); err != nil {
		return fmt.Errorf("write config file: %w", err)
	}

	return nil
}

// Validate 验证配置有效性
func (c *Config) Validate() error {
	if len(c.Servers) == 0 {
		return fmt.Errorf("at least one server required")
	}

	for i, srv := range c.Servers {
		if srv.Host == "" {
			return fmt.Errorf("server[%d]: host required", i)
		}
		if srv.Port <= 0 || srv.Port > 65535 {
			return fmt.Errorf("server[%d]: invalid port %d", i, srv.Port)
		}
		if srv.Timeout <= 0 {
			srv.Timeout = 30.0 // 默认超时
		}
	}

	if c.Hotkey.Key == "" {
		return fmt.Errorf("hotkey.key required")
	}

	if c.UI.Opacity < 0 || c.UI.Opacity > 1 {
		return fmt.Errorf("ui.opacity must be between 0 and 1")
	}

	if c.UI.Width <= 0 {
		c.UI.Width = 240
	}
	if c.UI.Height <= 0 {
		c.UI.Height = 70
	}

	return nil
}

// ResolveHotwordPaths 解析词库文件路径
func (c *Config) ResolveHotwordPaths() error {
	configDir, err := GetConfigDir()
	if err != nil {
		return err
	}

	for i, path := range c.HotwordFiles {
		if !filepath.IsAbs(path) {
			c.HotwordFiles[i] = filepath.Join(configDir, path)
		}

		// 检查文件是否存在（仅警告）
		if _, err := os.Stat(c.HotwordFiles[i]); os.IsNotExist(err) {
			fmt.Printf("Warning: hotword file not found: %s\n", c.HotwordFiles[i])
		}
	}

	return nil
}

// GetFirstAvailableServer 获取第一个可用的服务器配置
func (c *Config) GetFirstAvailableServer(checkFunc func(ServerConfig) bool) (*ServerConfig, int) {
	for i, srv := range c.Servers {
		if checkFunc(srv) {
			return &srv, i
		}
	}
	return nil, -1
}

// LoadHotwords 加载所有词库文件内容
func (c *Config) LoadHotwords() ([]string, error) {
	var allWords []string

	for _, path := range c.HotwordFiles {
		if _, err := os.Stat(path); os.IsNotExist(err) {
			continue // 跳过不存在的文件
		}

		data, err := os.ReadFile(path)
		if err != nil {
			return nil, fmt.Errorf("read hotword file %s: %w", path, err)
		}

		lines := strings.Split(string(data), "\n")
		for _, line := range lines {
			line = strings.TrimSpace(line)
			// 跳过空行和注释
			if line == "" || strings.HasPrefix(line, "#") {
				continue
			}
			allWords = append(allWords, line)
		}
	}

	return allWords, nil
}

// GetHotwordsString 获取词库字符串（空格分隔）
func (c *Config) GetHotwordsString() (string, error) {
	words, err := c.LoadHotwords()
	if err != nil {
		return "", err
	}
	return strings.Join(words, " "), nil
}
