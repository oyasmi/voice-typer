# VoiceTyper 跨平台语音输入工具详细设计文档

## 一、项目概述

### 1.1 项目背景
基于已有的 macOS 平台 Python 实现，使用 Go 语言重新设计开发一个跨平台的语音输入桌面工具。

### 1.2 核心特性
- **小巧**：单文件可执行程序，体积 10-15MB
- **快速**：毫秒级启动，低延迟响应
- **健壮**：完善的错误处理，稳定运行
- **交互友好**：系统托盘图标，视觉提示，状态反馈

### 1.3 支持平台
- Windows 10/11 (x64)

## 二、技术栈选型

```go
// go.mod
module github.com/yourusername/voice-typer

go 1.24

require (
    github.com/getlantern/systray v1.2.2    // 系统托盘
    github.com/gen2brain/beeep v0.11.2      // 系统通知
    github.com/go-vgo/robotgo v1.0.0        // 文本输入
    github.com/gen2brain/malgo v0.11.24     // 音频录制
    github.com/go-resty/resty/v2 v2.17.1    // HTTP客户端
    gopkg.in/yaml.v3 v3.0.1                 // 配置文件解析
    golang.org/x/sys v0.38.0                // Win32 API调用
)
```

## 三、项目结构

```
voice-typer/
├── main.go                 # 程序入口
├── go.mod                  # 依赖管理
├── go.sum
├── README.md
├── LICENSE
│
├── internal/
│   ├── config/
│   │   ├── config.go       # 配置定义与加载
│   │   └── defaults.go     # 默认配置
│   │
│   ├── audio/
│   │   ├── recorder.go     # 音频录制核心逻辑
│   │   └── buffer.go       # 音频缓冲区管理
│   │
│   ├── hotkey/
│   │   ├── listener.go     # 热键监听
│   │   └── parser.go       # 热键字符串解析
│   │
│   ├── api/
│   │   ├── client.go       # ASR API客户端
│   │   └── types.go        # API请求响应类型
│   │
│   ├── input/
│   │   ├── input.go        # 文本输入接口定义
│   │   └── clipboard.go    # 剪贴板粘贴
│   │
│   ├── ui/
│   │   ├── tray.go         # 系统托盘界面 (systray)
│   │   ├── indicator.go    # 录音提示窗口 (Win32 API)
│   │   └── notification.go # 系统通知
│   │
│   └── controller/
│       └── controller.go   # 核心控制器
│
├── pkg/
│   └── platform/
│       ├── platform.go     # 平台检测
│       └── windows.go      # Windows特定实现
│
├── assets/
│   └── icon.ico           # Windows图标
│
├── configs/
│   ├── config.example.yaml # 配置文件示例
│   └── hotwords.example.txt # 词库示例
│
└── Makefile               # 构建脚本
```

## 四、核心模块详细设计

---

### 4.1 配置模块 (internal/config/)

#### 4.1.1 配置结构定义

**文件：config.go**

```go
package config

import (
    "fmt"
    "os"
    "path/filepath"
    "gopkg.in/yaml.v3"
)

// Config 应用配置
type Config struct {
    Servers      []ServerConfig `yaml:"servers"`       // 服务器列表
    Hotkey       HotkeyConfig   `yaml:"hotkey"`        // 热键配置
    UI           UIConfig       `yaml:"ui"`            // UI配置
    HotwordFiles []string       `yaml:"hotword_files"` // 词库文件路径
    Input        InputConfig    `yaml:"input"`         // 输入配置
}

// ServerConfig 服务器配置
type ServerConfig struct {
    Name         string  `yaml:"name"`          // 服务器名称（用于标识）
    Host         string  `yaml:"host"`          // 主机地址
    Port         int     `yaml:"port"`          // 端口
    Timeout      float64 `yaml:"timeout"`       // 超时时间（秒）
    APIKey       string  `yaml:"api_key"`       // API密钥（可选）
    LLMRecorrect bool    `yaml:"llm_recorrect"` // 是否启用LLM修正
}

// HotkeyConfig 热键配置
type HotkeyConfig struct {
    Modifiers []string `yaml:"modifiers"` // 修饰键列表：ctrl, alt, shift, cmd
    Key       string   `yaml:"key"`       // 主键：space, tab, a-z, f1-f12等
}

// UIConfig UI配置
type UIConfig struct {
    Opacity float64 `yaml:"opacity"` // 提示窗口透明度 0.0-1.0
    Width   int     `yaml:"width"`   // 提示窗口宽度
    Height  int     `yaml:"height"`  // 提示窗口高度
}

// InputConfig 输入配置
type InputConfig struct {
    Method         string `yaml:"method"`          // 输入方法：simulate（优先）, clipboard（备选）
    FallbackToClipboard bool `yaml:"fallback_to_clipboard"` // 当模拟失败时是否回退到剪贴板
}

// GetConfigDir 获取配置目录路径
// 返回：配置目录绝对路径
// 逻辑：
//   - Linux/macOS: $HOME/.config/voice-typer
//   - Windows: %APPDATA%\voice-typer
func GetConfigDir() (string, error) {
    var configDir string
    
    if runtime.GOOS == "windows" {
        appData := os.Getenv("APPDATA")
        if appData == "" {
            return "", fmt.Errorf("APPDATA environment variable not set")
        }
        configDir = filepath.Join(appData, "voice-typer")
    } else {
        home, err := os.UserHomeDir()
        if err != nil {
            return "", fmt.Errorf("get user home dir: %w", err)
        }
        configDir = filepath.Join(home, ".config", "voice-typer")
    }
    
    return configDir, nil
}

// GetConfigPath 获取配置文件路径
// 返回：config.yaml的绝对路径
func GetConfigPath() (string, error) {
    dir, err := GetConfigDir()
    if err != nil {
        return "", err
    }
    return filepath.Join(dir, "config.yaml"), nil
}

// EnsureConfigDir 确保配置目录存在
// 逻辑：如果目录不存在则创建，权限755
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
// 参数：
//   - path: 配置文件路径，如为空则使用默认路径
// 返回：配置对象和错误
// 逻辑：
//   1. 如果path为空，获取默认配置路径
//   2. 如果配置文件不存在，创建默认配置文件
//   3. 读取并解析YAML配置
//   4. 验证配置有效性
//   5. 处理相对路径（hotword_files）
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
// 逻辑：
//   - 至少有一个服务器配置
//   - 每个服务器配置必须有host和port
//   - 热键配置必须有key
//   - UI配置的数值必须在合理范围内
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
// 逻辑：
//   - 如果是绝对路径，直接使用
//   - 如果是相对路径，相对于配置目录
//   - 检查文件是否存在，不存在则警告但不报错
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
// 参数：
//   - checkFunc: 健康检查函数，接收ServerConfig，返回bool表示是否可用
// 返回：第一个可用的服务器配置和索引，如果都不可用返回nil
// 逻辑：
//   - 依次对每个服务器执行健康检查
//   - 返回第一个检查通过的服务器
//   - 如果都不可用，返回nil
func (c *Config) GetFirstAvailableServer(checkFunc func(ServerConfig) bool) (*ServerConfig, int) {
    for i, srv := range c.Servers {
        if checkFunc(srv) {
            return &srv, i
        }
    }
    return nil, -1
}

// LoadHotwords 加载所有词库文件内容
// 返回：所有词汇的字符串切片
// 逻辑：
//   - 遍历所有词库文件
//   - 读取每个文件，按行分割
//   - 过滤空行和注释行（#开头）
//   - 合并所有词汇
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
// 用于传递给ASR服务
func (c *Config) GetHotwordsString() (string, error) {
    words, err := c.LoadHotwords()
    if err != nil {
        return "", err
    }
    return strings.Join(words, " "), nil
}
```

**文件：defaults.go**

```go
package config

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
            Method:              "simulate",
            FallbackToClipboard: true,
        },
    }
}

// CreateDefaultHotwordsFile 创建默认词库文件
func CreateDefaultHotwordsFile() error {
    configDir, err := GetConfigDir()
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
```

---

### 4.2 音频录制模块 (internal/audio/)

#### 4.2.1 录音器核心

**文件：recorder.go**

```go
package audio

import (
    "fmt"
    "sync"
    "github.com/gen2brain/malgo"
)

const (
    SampleRate = 16000              // 采样率 16kHz
    Channels   = 1                  // 单声道
    Format     = malgo.FormatS16    // 16位有符号整数
)

// Recorder 音频录制器
type Recorder struct {
    ctx     *malgo.AllocatedContext // malgo上下文
    device  *malgo.Device            // 录音设备
    buffer  *Buffer                  // 音频缓冲区
    mutex   sync.Mutex               // 保护状态
    running bool                     // 是否正在录音
}

// NewRecorder 创建录音器
// 返回：录音器实例和错误
// 逻辑：
//   1. 初始化malgo上下文
//   2. 创建音频缓冲区
//   3. 不立即打开设备（延迟到Start时）
func NewRecorder() (*Recorder, error) {
    // 初始化malgo上下文
    ctx, err := malgo.InitContext(nil, malgo.ContextConfig{}, nil)
    if err != nil {
        return nil, fmt.Errorf("init malgo context: %w", err)
    }
    
    r := &Recorder{
        ctx:     ctx,
        buffer:  NewBuffer(),
        running: false,
    }
    
    return r, nil
}

// Start 开始录音
// 返回：错误
// 逻辑：
//   1. 检查是否已在录音
//   2. 清空缓冲区
//   3. 配置录音设备参数
//   4. 设置数据回调（将音频数据写入缓冲区）
//   5. 初始化并启动设备
func (r *Recorder) Start() error {
    r.mutex.Lock()
    defer r.mutex.Unlock()
    
    if r.running {
        return fmt.Errorf("recorder already running")
    }
    
    // 清空缓冲区
    r.buffer.Clear()
    
    // 配置录音设备
    deviceConfig := malgo.DefaultDeviceConfig(malgo.Capture)
    deviceConfig.Capture.Format = Format
    deviceConfig.Capture.Channels = Channels
    deviceConfig.SampleRate = SampleRate
    deviceConfig.Alsa.NoMMap = 1 // Linux ALSA兼容性
    
    // 数据回调：接收音频帧
    onRecvFrames := func(pOutputSample, pInputSamples []byte, framecount uint32) {
        // 将输入数据追加到缓冲区
        r.buffer.Append(pInputSamples)
    }
    
    // 初始化设备
    var err error
    deviceCallbacks := malgo.DeviceCallbacks{
        Data: onRecvFrames,
    }
    
    r.device, err = malgo.InitDevice(r.ctx.Context, deviceConfig, deviceCallbacks)
    if err != nil {
        return fmt.Errorf("init device: %w", err)
    }
    
    // 启动设备
    if err := r.device.Start(); err != nil {
        r.device.Uninit()
        return fmt.Errorf("start device: %w", err)
    }
    
    r.running = true
    return nil
}

// Stop 停止录音
// 返回：录制的音频数据（[]byte，S16格式）和错误
// 逻辑：
//   1. 检查是否在录音
//   2. 停止设备
//   3. 释放设备资源
//   4. 获取缓冲区数据
//   5. 返回音频数据
func (r *Recorder) Stop() ([]byte, error) {
    r.mutex.Lock()
    defer r.mutex.Unlock()
    
    if !r.running {
        return nil, fmt.Errorf("recorder not running")
    }
    
    // 停止设备
    if err := r.device.Stop(); err != nil {
        return nil, fmt.Errorf("stop device: %w", err)
    }
    
    // 释放设备
    r.device.Uninit()
    r.device = nil
    
    r.running = false
    
    // 获取录音数据
    data := r.buffer.GetData()
    return data, nil
}

// IsRecording 检查是否正在录音
func (r *Recorder) IsRecording() bool {
    r.mutex.Lock()
    defer r.mutex.Unlock()
    return r.running
}

// Close 关闭录音器，释放资源
func (r *Recorder) Close() error {
    r.mutex.Lock()
    defer r.mutex.Unlock()
    
    if r.running {
        if r.device != nil {
            r.device.Stop()
            r.device.Uninit()
        }
        r.running = false
    }
    
    if r.ctx != nil {
        _ = r.ctx.Uninit()
        r.ctx.Free()
    }
    
    return nil
}
```

**文件：buffer.go**

```go
package audio

import (
    "sync"
)

// Buffer 音频数据缓冲区
// 线程安全的字节缓冲区
type Buffer struct {
    data  []byte
    mutex sync.Mutex
}

// NewBuffer 创建缓冲区
func NewBuffer() *Buffer {
    return &Buffer{
        data: make([]byte, 0, 1024*1024), // 预分配1MB
    }
}

// Append 追加数据
func (b *Buffer) Append(data []byte) {
    b.mutex.Lock()
    defer b.mutex.Unlock()
    b.data = append(b.data, data...)
}

// GetData 获取所有数据的副本
func (b *Buffer) GetData() []byte {
    b.mutex.Lock()
    defer b.mutex.Unlock()
    
    result := make([]byte, len(b.data))
    copy(result, b.data)
    return result
}

// Clear 清空缓冲区
func (b *Buffer) Clear() {
    b.mutex.Lock()
    defer b.mutex.Unlock()
    b.data = b.data[:0]
}

// Len 获取当前数据长度
func (b *Buffer) Len() int {
    b.mutex.Lock()
    defer b.mutex.Unlock()
    return len(b.data)
}
```

---

### 4.3 热键监听模块 (internal/hotkey/)

#### 4.3.1 热键解析器

**文件：parser.go**

```go
package hotkey

import (
    "fmt"
    "strings"
    "github.com/go-vgo/robotgo"
)

// Modifiers 修饰键类型
type Modifiers struct {
    Ctrl  bool
    Alt   bool
    Shift bool
    Cmd   bool // macOS Command / Windows Windows键
}

// KeyCode 键码定义
type KeyCode int

// 常用键码（robotgo的键码）
const (
    KeySpace KeyCode = 49
    KeyTab   KeyCode = 48
    KeyEnter KeyCode = 36
    
    // F键
    KeyF1  KeyCode = 122
    KeyF2  KeyCode = 120
    KeyF3  KeyCode = 99
    KeyF4  KeyCode = 118
    KeyF5  KeyCode = 96
    KeyF6  KeyCode = 97
    KeyF7  KeyCode = 98
    KeyF8  KeyCode = 100
    KeyF9  KeyCode = 101
    KeyF10 KeyCode = 109
    KeyF11 KeyCode = 103
    KeyF12 KeyCode = 111
)

// ParseHotkey 解析热键配置
// 参数：
//   - modifiers: 修饰键列表 ["ctrl", "alt"]
//   - key: 主键字符串 "space", "a", "f1"
// 返回：解析后的修饰键和键码
// 逻辑：
//   1. 解析修饰键列表，支持：ctrl, alt/option, shift, cmd/command
//   2. 解析主键，支持：
//      - 特殊键：space, tab, enter, f1-f12
//      - 字母键：a-z
//      - 数字键：0-9
//   3. 返回Modifiers结构和KeyCode
func ParseHotkey(modifiers []string, key string) (*Modifiers, KeyCode, error) {
    mods := &Modifiers{}
    
    // 解析修饰键
    for _, mod := range modifiers {
        mod = strings.ToLower(strings.TrimSpace(mod))
        switch mod {
        case "ctrl", "control":
            mods.Ctrl = true
        case "alt", "option":
            mods.Alt = true
        case "shift":
            mods.Shift = true
        case "cmd", "command":
            mods.Cmd = true
        default:
            return nil, 0, fmt.Errorf("unknown modifier: %s", mod)
        }
    }
    
    // 解析主键
    key = strings.ToLower(strings.TrimSpace(key))
    var keyCode KeyCode
    
    switch key {
    case "space":
        keyCode = KeySpace
    case "tab":
        keyCode = KeyTab
    case "enter", "return":
        keyCode = KeyEnter
    case "f1":
        keyCode = KeyF1
    case "f2":
        keyCode = KeyF2
    case "f3":
        keyCode = KeyF3
    case "f4":
        keyCode = KeyF4
    case "f5":
        keyCode = KeyF5
    case "f6":
        keyCode = KeyF6
    case "f7":
        keyCode = KeyF7
    case "f8":
        keyCode = KeyF8
    case "f9":
        keyCode = KeyF9
    case "f10":
        keyCode = KeyF10
    case "f11":
        keyCode = KeyF11
    case "f12":
        keyCode = KeyF12
    default:
        // 尝试作为字符键
        if len(key) == 1 {
            char := key[0]
            if (char >= 'a' && char <= 'z') || (char >= '0' && char <= '9') {
                // 字符键的键码需要通过robotgo获取
                // 这里暂存字符，实际监听时匹配
                keyCode = KeyCode(char)
            } else {
                return nil, 0, fmt.Errorf("unsupported key: %s", key)
            }
        } else {
            return nil, 0, fmt.Errorf("unsupported key: %s", key)
        }
    }
    
    return mods, keyCode, nil
}

// Match 检查当前按键事件是否匹配热键
// 参数：
//   - event: robotgo事件
//   - mods: 期望的修饰键
//   - keyCode: 期望的键码
// 返回：是否匹配
func Match(event robotgo.Event, mods *Modifiers, keyCode KeyCode) bool {
    // 检查修饰键
    if mods.Ctrl && !isCtrlPressed(event) {
        return false
    }
    if mods.Alt && !isAltPressed(event) {
        return false
    }
    if mods.Shift && !isShiftPressed(event) {
        return false
    }
    if mods.Cmd && !isCmdPressed(event) {
        return false
    }
    
    // 检查主键
    return event.Keycode == uint16(keyCode)
}

// 辅助函数：检查修饰键状态
func isCtrlPressed(event robotgo.Event) bool {
    // robotgo的Rawcode可能包含修饰键信息
    // 具体实现依赖平台
    return (event.Rawcode & 0x1000) != 0 // 示例，需要根据实际调整
}

func isAltPressed(event robotgo.Event) bool {
    return (event.Rawcode & 0x2000) != 0
}

func isShiftPressed(event robotgo.Event) bool {
    return (event.Rawcode & 0x4000) != 0
}

func isCmdPressed(event robotgo.Event) bool {
    return (event.Rawcode & 0x8000) != 0
}
```

**注意**：robotgo的事件处理在不同平台上有差异，上述修饰键检查逻辑是示意性的，实际实现需要根据robotgo文档和测试调整。

#### 4.3.2 热键监听器

**文件：listener.go**

```go
package hotkey

import (
    "fmt"
    "sync"
    "github.com/go-vgo/robotgo"
)

// Listener 热键监听器
type Listener struct {
    modifiers *Modifiers
    keyCode   KeyCode
    
    onPress   func() // 按下回调
    onRelease func() // 释放回调
    
    mutex     sync.Mutex
    running   bool
    pressed   bool // 当前热键是否被按下
}

// NewListener 创建热键监听器
// 参数：
//   - modifiers: 修饰键配置
//   - keyCode: 键码
//   - onPress: 按下回调函数
//   - onRelease: 释放回调函数
func NewListener(modifiers *Modifiers, keyCode KeyCode, onPress, onRelease func()) *Listener {
    return &Listener{
        modifiers: modifiers,
        keyCode:   keyCode,
        onPress:   onPress,
        onRelease: onRelease,
        running:   false,
        pressed:   false,
    }
}

// Start 启动监听
// 返回：错误
// 逻辑：
//   1. 检查是否已在运行
//   2. 启动robotgo事件钩子
//   3. 监听KeyDown和KeyUp事件
//   4. 匹配热键组合，触发回调
func (l *Listener) Start() error {
    l.mutex.Lock()
    if l.running {
        l.mutex.Unlock()
        return fmt.Errorf("listener already running")
    }
    l.running = true
    l.mutex.Unlock()
    
    // 注册KeyDown事件
    robotgo.EventHook(robotgo.KeyDown, []string{}, func(e robotgo.Event) {
        l.mutex.Lock()
        defer l.mutex.Unlock()
        
        if !l.running {
            return
        }
        
        // 检查是否匹配热键
        if !l.pressed && Match(e, l.modifiers, l.keyCode) {
            l.pressed = true
            if l.onPress != nil {
                go l.onPress() // 异步调用，避免阻塞事件循环
            }
        }
    })
    
    // 注册KeyUp事件
    robotgo.EventHook(robotgo.KeyUp, []string{}, func(e robotgo.Event) {
        l.mutex.Lock()
        defer l.mutex.Unlock()
        
        if !l.running {
            return
        }
        
        // 检查是否是热键释放
        if l.pressed && e.Keycode == uint16(l.keyCode) {
            l.pressed = false
            if l.onRelease != nil {
                go l.onRelease()
            }
        }
    })
    
    // 启动事件循环（阻塞）
    go robotgo.EventStart()
    
    return nil
}

// Stop 停止监听
func (l *Listener) Stop() error {
    l.mutex.Lock()
    defer l.mutex.Unlock()
    
    if !l.running {
        return nil
    }
    
    l.running = false
    l.pressed = false
    
    // 停止事件循环
    robotgo.EventEnd()
    
    return nil
}

// IsRunning 检查是否在运行
func (l *Listener) IsRunning() bool {
    l.mutex.Lock()
    defer l.mutex.Unlock()
    return l.running
}
```

**提示**：
- `robotgo.EventHook` 在某些平台（特别是Wayland）可能需要特殊权限
- `robotgo.EventStart()` 是阻塞调用，需在goroutine中运行
- 修饰键的匹配逻辑需要平台特定调整

---

### 4.4 API客户端模块 (internal/api/)

#### 4.4.1 类型定义

**文件：types.go**

```go
package api

// RecognizeRequest 识别请求（用于构建multipart表单）
type RecognizeRequest struct {
    Audio        []byte // 音频数据（S16格式，16kHz，单声道）
    Hotwords     string // 热词字符串（空格分隔）
    LLMRecorrect bool   // 是否启用LLM修正
}

// RecognizeResponse 识别响应
type RecognizeResponse struct {
    Text string `json:"text"` // 识别结果文本
}

// HealthResponse 健康检查响应
type HealthResponse struct {
    Ready bool `json:"ready"` // 服务是否就绪
}
```

#### 4.4.2 客户端实现

**文件：client.go**

```go
package api

import (
    "bytes"
    "encoding/json"
    "fmt"
    "io"
    "mime/multipart"
    "net/http"
    "time"
    
    "github.com/go-resty/resty/v2"
)

// Client ASR API客户端
type Client struct {
    baseURL      string
    timeout      time.Duration
    apiKey       string
    llmRecorrect bool
    httpClient   *resty.Client
}

// NewClient 创建API客户端
// 参数：
//   - host: 服务器地址
//   - port: 端口
//   - timeout: 超时时间（秒）
//   - apiKey: API密钥（可选）
//   - llmRecorrect: 是否启用LLM修正
func NewClient(host string, port int, timeout float64, apiKey string, llmRecorrect bool) *Client {
    baseURL := fmt.Sprintf("http://%s:%d", host, port)
    
    client := resty.New().
        SetBaseURL(baseURL).
        SetTimeout(time.Duration(timeout * float64(time.Second))).
        SetHeader("User-Agent", "VoiceTyper/1.0")
    
    // 如果有API密钥且不是本地地址，添加认证头
    if apiKey != "" && host != "127.0.0.1" && host != "localhost" {
        client.SetHeader("Authorization", fmt.Sprintf("Bearer %s", apiKey))
    }
    
    return &Client{
        baseURL:      baseURL,
        timeout:      time.Duration(timeout * float64(time.Second)),
        apiKey:       apiKey,
        llmRecorrect: llmRecorrect,
        httpClient:   client,
    }
}

// HealthCheck 健康检查
// 返回：服务是否可用和错误
// 逻辑：
//   1. 发送GET请求到/health端点
//   2. 解析响应JSON
//   3. 返回ready字段
func (c *Client) HealthCheck() (bool, error) {
    var resp HealthResponse
    
    res, err := c.httpClient.R().
        SetResult(&resp).
        Get("/health")
    
    if err != nil {
        return false, fmt.Errorf("health check request: %w", err)
    }
    
    if res.StatusCode() != http.StatusOK {
        return false, fmt.Errorf("health check failed: status %d", res.StatusCode())
    }
    
    return resp.Ready, nil
}

// Recognize 识别音频
// 参数：
//   - audio: 音频数据（[]byte，S16格式）
//   - hotwords: 热词字符串
// 返回：识别文本和错误
// 逻辑：
//   1. 构建multipart/form-data请求体
//   2. 添加audio字段（文件）
//   3. 添加hotwords字段（文本）
//   4. 添加llm_recorrect字段（布尔值）
//   5. 发送POST请求到/recognize端点
//   6. 解析响应JSON
//   7. 返回text字段
func (c *Client) Recognize(audio []byte, hotwords string) (string, error) {
    if len(audio) == 0 {
        return "", fmt.Errorf("empty audio data")
    }
    
    // 构建multipart表单
    body := &bytes.Buffer{}
    writer := multipart.NewWriter(body)
    
    // 添加音频文件
    part, err := writer.CreateFormFile("audio", "audio.raw")
    if err != nil {
        return "", fmt.Errorf("create form file: %w", err)
    }
    if _, err := part.Write(audio); err != nil {
        return "", fmt.Errorf("write audio data: %w", err)
    }
    
    // 添加热词
    if hotwords != "" {
        if err := writer.WriteField("hotwords", hotwords); err != nil {
            return "", fmt.Errorf("write hotwords: %w", err)
        }
    }
    
    // 添加LLM修正参数
    llmValue := "false"
    if c.llmRecorrect {
        llmValue = "true"
    }
    if err := writer.WriteField("llm_recorrect", llmValue); err != nil {
        return "", fmt.Errorf("write llm_recorrect: %w", err)
    }
    
    // 关闭writer
    if err := writer.Close(); err != nil {
        return "", fmt.Errorf("close writer: %w", err)
    }
    
    // 发送请求
    var resp RecognizeResponse
    res, err := c.httpClient.R().
        SetHeader("Content-Type", writer.FormDataContentType()).
        SetBody(body.Bytes()).
        SetResult(&resp).
        Post("/recognize")
    
    if err != nil {
        return "", fmt.Errorf("recognize request: %w", err)
    }
    
    if res.StatusCode() != http.StatusOK {
        return "", fmt.Errorf("recognize failed: status %d, body: %s", 
            res.StatusCode(), res.String())
    }
    
    return resp.Text, nil
}
```

---

### 4.5 文本输入模块 (internal/input/)

#### 4.5.1 输入接口

**文件：input.go**

```go
package input

// Inserter 文本插入接口
type Inserter interface {
    // Insert 插入文本到当前光标位置
    Insert(text string) error
}

// InsertMethod 输入方法类型
type InsertMethod string

const (
    MethodSimulate InsertMethod = "simulate" // 模拟键盘输入（优先）
    MethodClipboard InsertMethod = "clipboard" // 剪贴板粘贴（备选）
)
```

#### 4.5.2 模拟输入实现（优先方案）

**文件：simulator.go**

```go
package input

import (
    "fmt"
    "github.com/go-vgo/robotgo"
)

// Simulator 文本模拟输入器
type Simulator struct{}

// NewSimulator 创建模拟输入器
func NewSimulator() *Simulator {
    return &Simulator{}
}

// Insert 通过模拟键盘输入文本
// 参数：
//   - text: 要输入的文本
// 返回：错误
// 逻辑：
//   1. 使用robotgo.TypeStr直接输入文本
//   2. robotgo会模拟逐字符输入
//   3. 支持中英文混合
// 注意：
//   - 某些应用可能不接受模拟输入（如终端、某些游戏）
//   - Wayland下可能需要特殊权限或工具（如ydotool）
func (s *Simulator) Insert(text string) error {
    if text == "" {
        return nil
    }
    
    // 使用robotgo模拟输入
    robotgo.TypeStr(text)
    
    return nil
}

// IsSupported 检查当前平台是否支持模拟输入
// 返回：是否支持
// 逻辑：
//   - X11: 支持
//   - macOS: 支持
//   - Windows: 支持
//   - Wayland: 部分支持（需要额外配置）
func (s *Simulator) IsSupported() bool {
    // 检测Wayland环境
    if runtime.GOOS == "linux" {
        waylandDisplay := os.Getenv("WAYLAND_DISPLAY")
        if waylandDisplay != "" {
            // Wayland环境，检查是否有ydotool
            _, err := exec.LookPath("ydotool")
            if err != nil {
                return false // ydotool不可用
            }
        }
    }
    
    return true
}
```

#### 4.5.3 剪贴板粘贴实现（备选方案）

**文件：clipboard.go**

```go
package input

import (
    "fmt"
    "time"
    "github.com/go-vgo/robotgo"
    "golang.design/x/clipboard"
)

// ClipboardInserter 剪贴板粘贴输入器
type ClipboardInserter struct{}

// NewClipboardInserter 创建剪贴板输入器
func NewClipboardInserter() *ClipboardInserter {
    // 初始化clipboard
    err := clipboard.Init()
    if err != nil {
        fmt.Printf("Warning: clipboard init failed: %v\n", err)
    }
    return &ClipboardInserter{}
}

// Insert 通过剪贴板粘贴输入文本
// 参数：
//   - text: 要输入的文本
// 返回：错误
// 逻辑：
//   1. 保存当前剪贴板内容（可选）
//   2. 将文本写入剪贴板
//   3. 等待短暂延迟确保剪贴板就绪
//   4. 模拟粘贴快捷键（Ctrl+V / Cmd+V）
//   5. 等待粘贴完成
//   6. 恢复原剪贴板内容（可选）
func (c *ClipboardInserter) Insert(text string) error {
    if text == "" {
        return nil
    }
    
    // 备份当前剪贴板（可选，避免覆盖用户数据）
    originalClip := clipboard.Read(clipboard.FmtText)
    
    // 写入文本到剪贴板
    clipboard.Write(clipboard.FmtText, []byte(text))
    
    // 等待剪贴板就绪
    time.Sleep(50 * time.Millisecond)
    
    // 模拟粘贴快捷键
    if err := c.simulatePaste(); err != nil {
        return fmt.Errorf("simulate paste: %w", err)
    }
    
    // 等待粘贴完成
    time.Sleep(100 * time.Millisecond)
    
    // 恢复原剪贴板内容（可选）
    if len(originalClip) > 0 {
        clipboard.Write(clipboard.FmtText, originalClip)
    }
    
    return nil
}

// simulatePaste 模拟粘贴快捷键
func (c *ClipboardInserter) simulatePaste() error {
    // 根据平台选择修饰键
    var modifier string
    if runtime.GOOS == "darwin" {
        modifier = "cmd"
    } else {
        modifier = "ctrl"
    }
    
    // 按下修饰键+V
    robotgo.KeyTap("v", modifier)
    
    return nil
}
```

#### 4.5.4 输入管理器（自动选择策略）

**文件：manager.go**

```go
package input

import (
    "fmt"
)

// Manager 输入管理器，根据配置和平台能力选择输入方法
type Manager struct {
    primary   Inserter
    fallback  Inserter
    useFallback bool
}

// NewManager 创建输入管理器
// 参数：
//   - method: 优先输入方法
//   - fallbackEnabled: 是否启用回退机制
// 返回：输入管理器实例
// 逻辑：
//   1. 根据method创建主输入器
//   2. 如果主输入器不支持且启用回退，创建备选输入器
//   3. 返回管理器
func NewManager(method InsertMethod, fallbackEnabled bool) (*Manager, error) {
    var primary, fallback Inserter
    useFallback := false
    
    // 创建主输入器
    switch method {
    case MethodSimulate:
        sim := NewSimulator()
        if sim.IsSupported() {
            primary = sim
        } else if fallbackEnabled {
            // 模拟输入不支持，使用剪贴板
            fmt.Println("Warning: Simulate input not supported, using clipboard fallback")
            primary = NewClipboardInserter()
            useFallback = true
        } else {
            return nil, fmt.Errorf("simulate input not supported and fallback disabled")
        }
    case MethodClipboard:
        primary = NewClipboardInserter()
    default:
        return nil, fmt.Errorf("unknown input method: %s", method)
    }
    
    // 创建备选输入器（如果主输入器是模拟且启用回退）
    if method == MethodSimulate && fallbackEnabled && !useFallback {
        fallback = NewClipboardInserter()
    }
    
    return &Manager{
        primary:     primary,
        fallback:    fallback,
        useFallback: useFallback,
    }, nil
}

// Insert 插入文本，自动尝试回退
// 逻辑：
//   1. 尝试使用主输入器
//   2. 如果失败且有备选输入器，使用备选
func (m *Manager) Insert(text string) error {
    err := m.primary.Insert(text)
    
    if err != nil && m.fallback != nil {
        fmt.Printf("Primary input failed: %v, trying fallback...\n", err)
        return m.fallback.Insert(text)
    }
    
    return err
}
```

---

### 4.6 UI模块 (internal/ui/)

#### 4.6.1 系统托盘

**文件：tray.go**

```go
package ui

import (
    "fmt"
    "fyne.io/fyne/v2"
    "fyne.io/fyne/v2/app"
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
// 参数：
//   - appName: 应用名称
//   - onToggle: 启用/禁用回调
//   - onQuit: 退出回调
func NewTrayApp(appName string, onToggle, onQuit func()) *TrayApp {
    a := app.NewWithID("com.voicetyper.app")
    
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
    
    configItem := fyne.NewMenuItem("Open Config", func() {
        // 打开配置文件
        // 实现见后续
    })
    
    aboutItem := fyne.NewMenuItem("About", func() {
        // 显示关于对话框
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
        configItem,
        aboutItem,
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
    t.statusItem.Label = fmt.Sprintf("Status: %s", status)
    t.menu.Refresh()
}

// SetToggleState 设置启用/禁用状态
// 参数：
//   - enabled: 是否已启用
//   - hotkey: 热键字符串（用于显示）
func (t *TrayApp) SetToggleState(enabled bool, hotkey string) {
    if enabled {
        t.toggleItem.Label = fmt.Sprintf("Disable (%s)", hotkey)
    } else {
        t.toggleItem.Label = fmt.Sprintf("Enable (%s)", hotkey)
    }
    t.menu.Refresh()
}
```

#### 4.6.2 录音提示窗口

**文件：indicator.go**

```go
package ui

import (
    "fmt"
    "time"
    "fyne.io/fyne/v2"
    "fyne.io/fyne/v2/app"
    "fyne.io/fyne/v2/canvas"
    "fyne.io/fyne/v2/container"
    "fyne.io/fyne/v2/widget"
)

// Indicator 录音提示窗口
type Indicator struct {
    window    fyne.Window
    label     *widget.Label
    timeLabel *widget.Label
    startTime time.Time
    visible   bool
    
    width   int
    height  int
    opacity float64
    
    updateTimer *time.Ticker
    stopChan    chan bool
}

// NewIndicator 创建提示窗口
// 参数：
//   - width: 窗口宽度
//   - height: 窗口高度
//   - opacity: 透明度 0.0-1.0
func NewIndicator(width, height int, opacity float64) *Indicator {
    a := app.New()
    w := a.NewWindow("Recording")
    
    // 设置窗口属性：无边框、置顶、固定大小
    w.SetFixedSize(true)
    w.Resize(fyne.NewSize(float32(width), float32(height)))
    
    ind := &Indicator{
        window:   w,
        width:    width,
        height:   height,
        opacity:  opacity,
        visible:  false,
        stopChan: make(chan bool),
    }
    
    ind.setupContent()
    
    return ind
}

// setupContent 设置窗口内容
func (i *Indicator) setupContent() {
    // 主标签：显示录音状态
    i.label = widget.NewLabel("🎤 Recording...")
    i.label.Alignment = fyne.TextAlignCenter
    i.label.TextStyle = fyne.TextStyle{Bold: true}
    
    // 时间标签：显示录音时长
    i.timeLabel = widget.NewLabel("0.0s")
    i.timeLabel.Alignment = fyne.TextAlignCenter
    
    // 背景（半透明深色）
    bg := canvas.NewRectangle(color.RGBA{30, 30, 30, uint8(i.opacity * 255)})
    
    content := container.NewStack(
        bg,
        container.NewVBox(
            widget.NewLabel(""), // 空白用于居中
            i.label,
            i.timeLabel,
        ),
    )
    
    i.window.SetContent(content)
}

// Show 显示提示窗口
// 逻辑：
//   1. 如果已显示，直接返回
//   2. 记录开始时间
//   3. 显示窗口（居中显示）
//   4. 启动时间更新循环
func (i *Indicator) Show() {
    if i.visible {
        return
    }
    
    i.visible = true
    i.startTime = time.Now()
    
    // 窗口居中显示（屏幕上方中央）
    i.window.CenterOnScreen()
    i.window.Show()
    
    // 启动时间更新
    i.startTimeUpdate()
}

// Hide 隐藏提示窗口
func (i *Indicator) Hide() {
    if !i.visible {
        return
    }
    
    i.visible = false
    i.window.Hide()
    
    // 停止时间更新
    i.stopTimeUpdate()
}

// SetStatus 更新状态文本
func (i *Indicator) SetStatus(status string) {
    i.label.SetText(status)
}

// startTimeUpdate 启动时间更新循环
func (i *Indicator) startTimeUpdate() {
    i.updateTimer = time.NewTicker(100 * time.Millisecond)
    
    go func() {
        for {
            select {
            case <-i.updateTimer.C:
                if i.visible {
                    elapsed := time.Since(i.startTime).Seconds()
                    i.timeLabel.SetText(fmt.Sprintf("%.1fs", elapsed))
                }
            case <-i.stopChan:
                return
            }
        }
    }()
}

// stopTimeUpdate 停止时间更新
func (i *Indicator) stopTimeUpdate() {
    if i.updateTimer != nil {
        i.updateTimer.Stop()
        i.stopChan <- true
    }
}

// Close 关闭提示窗口
func (i *Indicator) Close() {
    i.Hide()
    i.window.Close()
}
```

**提示**：Fyne的窗口置顶和透明度在不同平台上支持程度不同，可能需要平台特定调整。

#### 4.6.3 系统通知

**文件：notification.go**

```go
package ui

import (
    "github.com/gen2brain/beeep"
)

// Notify 显示系统通知
// 参数：
//   - title: 通知标题
//   - message: 通知内容
func Notify(title, message string) error {
    return beeep.Notify(title, message, "")
}

// NotifyWithIcon 显示带图标的系统通知
// 参数：
//   - title: 通知标题
//   - message: 通知内容
//   - iconPath: 图标文件路径
func NotifyWithIcon(title, message, iconPath string) error {
    return beeep.Notify(title, message, iconPath)
}
```

---

### 4.7 核心控制器模块 (internal/controller/)

**文件：controller.go**

这是整个应用的核心协调器，负责串联所有模块。

```go
package controller

import (
    "fmt"
    "sync"
    "time"
    
    "voice-typer/internal/audio"
    "voice-typer/internal/hotkey"
    "voice-typer/internal/api"
    "voice-typer/internal/input"
    "voice-typer/internal/ui"
    "voice-typer/internal/config"
)

// Controller 核心控制器
type Controller struct {
    config    *config.Config
    recorder  *audio.Recorder
    listener  *hotkey.Listener
    apiClient *api.Client
    inputMgr  *input.Manager
    indicator *ui.Indicator
    
    // 状态
    enabled   bool
    recording bool
    mutex     sync.Mutex
    
    // 回调
    onStatusChange func(string)
    
    // 词库
    hotwords string
}

// NewController 创建控制器
// 参数：
//   - cfg: 配置对象
// 返回：控制器实例和错误
func NewController(cfg *config.Config) (*Controller, error) {
    c := &Controller{
        config:  cfg,
        enabled: false,
        recording: false,
    }
    
    return c, nil
}

// Initialize 初始化所有模块
// 参数：
//   - onStatusChange: 状态变化回调函数
// 返回：错误
// 逻辑：
//   1. 连接ASR服务（选择第一个可用的服务器）
//   2. 初始化录音器
//   3. 初始化热键监听器
//   4. 初始化输入管理器
//   5. 初始化UI提示窗口
//   6. 加载词库
func (c *Controller) Initialize(onStatusChange func(string)) error {
    c.onStatusChange = onStatusChange
    c.updateStatus("Initializing...")
    
    // 1. 选择可用的ASR服务器
    c.updateStatus("Connecting to ASR service...")
    
    availableServer, idx := c.config.GetFirstAvailableServer(func(srv config.ServerConfig) bool {
        client := api.NewClient(srv.Host, srv.Port, srv.Timeout, srv.APIKey, srv.LLMRecorrect)
        ready, err := client.HealthCheck()
        if err != nil {
            fmt.Printf("Server %s:%d health check failed: %v\n", srv.Host, srv.Port, err)
            return false
        }
        return ready
    })
    
    if availableServer == nil {
        return fmt.Errorf("no available ASR server found")
    }
    
    fmt.Printf("Using ASR server: %s:%d (index %d)\n", 
        availableServer.Host, availableServer.Port, idx)
    
    c.apiClient = api.NewClient(
        availableServer.Host,
        availableServer.Port,
        availableServer.Timeout,
        availableServer.APIKey,
        availableServer.LLMRecorrect,
    )
    
    llmStatus := ""
    if availableServer.LLMRecorrect {
        llmStatus = " (LLM correction enabled)"
    }
    c.updateStatus(fmt.Sprintf("Connected to ASR%s", llmStatus))
    
    // 2. 初始化录音器
    c.updateStatus("Initializing audio recorder...")
    var err error
    c.recorder, err = audio.NewRecorder()
    if err != nil {
        return fmt.Errorf("init recorder: %w", err)
    }
    
    // 3. 初始化热键监听器
    c.updateStatus("Initializing hotkey listener...")
    mods, keyCode, err := hotkey.ParseHotkey(c.config.Hotkey.Modifiers, c.config.Hotkey.Key)
    if err != nil {
        return fmt.Errorf("parse hotkey: %w", err)
    }
    
    c.listener = hotkey.NewListener(mods, keyCode, c.onHotkeyPress, c.onHotkeyRelease)
    
    // 4. 初始化输入管理器
    c.updateStatus("Initializing input manager...")
    c.inputMgr, err = input.NewManager(
        input.InsertMethod(c.config.Input.Method),
        c.config.Input.FallbackToClipboard,
    )
    if err != nil {
        return fmt.Errorf("init input manager: %w", err)
    }
    
    // 5. 初始化UI提示窗口
    c.indicator = ui.NewIndicator(
        c.config.UI.Width,
        c.config.UI.Height,
        c.config.UI.Opacity,
    )
    
    // 6. 加载词库
    c.updateStatus("Loading hotwords...")
    c.hotwords, err = c.config.GetHotwordsString()
    if err != nil {
        fmt.Printf("Warning: load hotwords failed: %v\n", err)
        c.hotwords = ""
    }
    
    c.updateStatus("Initialized")
    return nil
}

// Enable 启用语音输入
// 逻辑：
//   1. 检查是否已启用
//   2. 启动热键监听
//   3. 更新状态
func (c *Controller) Enable() error {
    c.mutex.Lock()
    defer c.mutex.Unlock()
    
    if c.enabled {
        return nil
    }
    
    if err := c.listener.Start(); err != nil {
        return fmt.Errorf("start listener: %w", err)
    }
    
    c.enabled = true
    c.updateStatus("Ready")
    
    // 显示通知
    hotkeyStr := c.getHotkeyString()
    ui.Notify("VoiceTyper", fmt.Sprintf("Press %s to start voice input", hotkeyStr))
    
    return nil
}

// Disable 禁用语音输入
func (c *Controller) Disable() error {
    c.mutex.Lock()
    defer c.mutex.Unlock()
    
    if !c.enabled {
        return nil
    }
    
    // 如果正在录音，先停止
    if c.recording {
        c.recorder.Stop()
        c.indicator.Hide()
        c.recording = false
    }
    
    if err := c.listener.Stop(); err != nil {
        return fmt.Errorf("stop listener: %w", err)
    }
    
    c.enabled = false
    c.updateStatus("Disabled")
    
    return nil
}

// IsEnabled 检查是否已启用
func (c *Controller) IsEnabled() bool {
    c.mutex.Lock()
    defer c.mutex.Unlock()
    return c.enabled
}

// onHotkeyPress 热键按下回调
// 逻辑：
//   1. 检查是否已在录音
//   2. 显示提示窗口
//   3. 开始录音
//   4. 更新状态
func (c *Controller) onHotkeyPress() {
    c.mutex.Lock()
    if c.recording {
        c.mutex.Unlock()
        return
    }
    c.recording = true
    c.mutex.Unlock()
    
    // 显示提示
    c.indicator.Show()
    
    // 开始录音
    if err := c.recorder.Start(); err != nil {
        fmt.Printf("Failed to start recording: %v\n", err)
        c.indicator.Hide()
        c.mutex.Lock()
        c.recording = false
        c.mutex.Unlock()
        return
    }
    
    c.updateStatus("Recording...")
}

// onHotkeyRelease 热键释放回调
// 逻辑：
//   1. 检查是否在录音
//   2. 停止录音
//   3. 隐藏提示窗口
//   4. 异步处理音频（识别+输入）
func (c *Controller) onHotkeyRelease() {
    c.mutex.Lock()
    if !c.recording {
        c.mutex.Unlock()
        return
    }
    c.recording = false
    c.mutex.Unlock()
    
    // 停止录音
    audioData, err := c.recorder.Stop()
    if err != nil {
        fmt.Printf("Failed to stop recording: %v\n", err)
        c.indicator.Hide()
        c.updateStatus("Recording failed")
        time.AfterFunc(1*time.Second, func() {
            c.updateStatus("Ready")
        })
        return
    }
    
    // 隐藏提示
    c.indicator.Hide()
    
    // 异步处理音频
    go c.processAudio(audioData)
}

// processAudio 处理录音数据
// 参数：
//   - audioData: 音频数据
// 逻辑：
//   1. 检查音频长度
//   2. 调用ASR API识别
//   3. 如果识别成功，插入文本
//   4. 更新状态
func (c *Controller) processAudio(audioData []byte) {
    if len(audioData) == 0 {
        c.updateStatus("Empty audio")
        time.Sleep(1 * time.Second)
        c.updateStatus("Ready")
        return
    }
    
    c.updateStatus("Recognizing...")
    
    // 调用ASR API
    text, err := c.apiClient.Recognize(audioData, c.hotwords)
    if err != nil {
        fmt.Printf("Recognition failed: %v\n", err)
        c.updateStatus("Recognition failed")
        time.Sleep(1500 * time.Millisecond)
        c.updateStatus("Ready")
        return
    }
    
    if text == "" {
        c.updateStatus("No text recognized")
        time.Sleep(1 * time.Second)
        c.updateStatus("Ready")
        return
    }
    
    // 插入文本
    c.updateStatus("Inserting text...")
    if err := c.inputMgr.Insert(text); err != nil {
        fmt.Printf("Insert text failed: %v\n", err)
        c.updateStatus("Insert failed")
    } else {
        c.updateStatus(fmt.Sprintf("Inserted (%d chars)", len(text)))
    }
    
    time.Sleep(1500 * time.Millisecond)
    c.updateStatus("Ready")
}

// updateStatus 更新状态（触发回调）
func (c *Controller) updateStatus(status string) {
    if c.onStatusChange != nil {
        c.onStatusChange(status)
    }
}

// getHotkeyString 获取热键字符串表示
func (c *Controller) getHotkeyString() string {
    mods := c.config.Hotkey.Modifiers
    key := c.config.Hotkey.Key
    
    parts := make([]string, len(mods)+1)
    for i, mod := range mods {
        parts[i] = strings.ToUpper(mod)
    }
    parts[len(mods)] = strings.ToUpper(key)
    
    return strings.Join(parts, "+")
}

// Close 关闭控制器，释放所有资源
func (c *Controller) Close() error {
    c.Disable()
    
    if c.recorder != nil {
        c.recorder.Close()
    }
    
    if c.indicator != nil {
        c.indicator.Close()
    }
    
    return nil
}
```

---

### 4.8 平台检测模块 (pkg/platform/)

**文件：platform.go**

```go
package platform

import (
    "os"
    "runtime"
)

// Platform 平台类型
type Platform string

const (
    MacOS   Platform = "darwin"
    Windows Platform = "windows"
    Linux   Platform = "linux"
)

// GetPlatform 获取当前平台
func GetPlatform() Platform {
    return Platform(runtime.GOOS)
}

// IsWayland 检查Linux是否运行在Wayland下
func IsWayland() bool {
    if runtime.GOOS != "linux" {
        return false
    }
    return os.Getenv("WAYLAND_DISPLAY") != ""
}

// IsX11 检查Linux是否运行在X11下
func IsX11() bool {
    if runtime.GOOS != "linux" {
        return false
    }
    return os.Getenv("DISPLAY") != "" && !IsWayland()
}
```

平台特定实现文件（darwin.go, windows.go, linux.go）可用于处理各平台的特殊逻辑，当前主要逻辑已通过robotgo等跨平台库处理，这些文件可暂时留空或用于未来扩展。

---

### 4.9 主程序入口 (main.go)

```go
package main

import (
    "fmt"
    "os"
    "os/signal"
    "syscall"
    
    "voice-typer/internal/config"
    "voice-typer/internal/controller"
    "voice-typer/internal/ui"
)

const (
    AppName    = "VoiceTyper"
    AppVersion = "1.0.0"
)

func main() {
    fmt.Printf("===========================================\n")
    fmt.Printf("%s v%s\n", AppName, AppVersion)
    fmt.Printf("===========================================\n\n")
    
    // 确保配置目录存在
    if err := config.EnsureConfigDir(); err != nil {
        fmt.Printf("Failed to create config dir: %v\n", err)
        os.Exit(1)
    }
    
    // 创建默认词库文件
    if err := config.CreateDefaultHotwordsFile(); err != nil {
        fmt.Printf("Warning: failed to create default hotwords file: %v\n", err)
    }
    
    configDir, _ := config.GetConfigDir()
    fmt.Printf("Config directory: %s\n\n", configDir)
    
    // 加载配置
    cfg, err := config.Load("")
    if err != nil {
        fmt.Printf("Failed to load config: %v\n", err)
        os.Exit(1)
    }
    
    // 创建控制器
    ctrl, err := controller.NewController(cfg)
    if err != nil {
        fmt.Printf("Failed to create controller: %v\n", err)
        os.Exit(1)
    }
    defer ctrl.Close()
    
    // 创建托盘应用
    var trayApp *ui.TrayApp
    var enabled bool = false
    
    hotkeyStr := getHotkeyString(cfg)
    
    trayApp = ui.NewTrayApp(
        AppName,
        func() {
            // 切换启用/禁用
            if enabled {
                ctrl.Disable()
                enabled = false
                trayApp.SetToggleState(false, hotkeyStr)
            } else {
                if err := ctrl.Enable(); err != nil {
                    fmt.Printf("Failed to enable: %v\n", err)
                    return
                }
                enabled = true
                trayApp.SetToggleState(true, hotkeyStr)
            }
        },
        func() {
            // 退出
            fmt.Println("Exiting...")
            ctrl.Close()
        },
    )
    
    // 异步初始化控制器
    go func() {
        err := ctrl.Initialize(func(status string) {
            trayApp.UpdateStatus(status)
        })
        
        if err != nil {
            fmt.Printf("Initialization failed: %v\n", err)
            trayApp.UpdateStatus(fmt.Sprintf("Init failed: %v", err))
            return
        }
        
        // 自动启用
        if err := ctrl.Enable(); err != nil {
            fmt.Printf("Failed to enable: %v\n", err)
            return
        }
        enabled = true
        trayApp.SetToggleState(true, hotkeyStr)
        
        fmt.Println("Initialization complete")
        fmt.Printf("Press %s to start voice input\n", hotkeyStr)
    }()
    
    // 处理系统信号
    sigChan := make(chan os.Signal, 1)
    signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
    
    go func() {
        <-sigChan
        fmt.Println("\nReceived interrupt signal, exiting...")
        ctrl.Close()
        os.Exit(0)
    }()
    
    // 运行托盘应用（阻塞）
    trayApp.Run()
}

func getHotkeyString(cfg *config.Config) string {
    parts := make([]string, 0, len(cfg.Hotkey.Modifiers)+1)
    for _, mod := range cfg.Hotkey.Modifiers {
        parts = append(parts, strings.ToUpper(mod))
    }
    parts = append(parts, strings.ToUpper(cfg.Hotkey.Key))
    return strings.Join(parts, "+")
}
```

---

## 五、配置文件示例

**configs/config.example.yaml**

```yaml
# VoiceTyper Configuration File

# ASR servers list (will try in order and use the first available one)
servers:
  - name: "local"
    host: "127.0.0.1"
    port: 6008
    timeout: 30.0
    api_key: ""
    llm_recorrect: false
  
  - name: "remote"
    host: "192.168.1.100"
    port: 6008
    timeout: 60.0
    api_key: "your_api_key_here"
    llm_recorrect: true

# Hotkey configuration
# Supported modifiers: ctrl, alt/option, shift, cmd/command
# Supported keys: space, tab, a-z, 0-9, f1-f12, etc.
hotkey:
  modifiers:
    - "cmd"   # Use "ctrl" for Windows/Linux
  key: "space"

# Custom hotword files
# Supports multiple files, one word per line
# Supports absolute path or relative path (relative to config directory)
hotword_files:
  - "hotwords.txt"

# Input configuration
input:
  method: "simulate"           # "simulate" or "clipboard"
  fallback_to_clipboard: true  # Fallback to clipboard if simulate fails

# UI configuration
ui:
  opacity: 0.85  # Window opacity (0.0-1.0)
  width: 240     # Window width in pixels
  height: 70     # Window height in pixels
```

---

## 六、构建与打包

### 6.1 构建脚本

**build/build.sh**

```bash
#!/bin/bash

# VoiceTyper Build Script

set -e

VERSION="1.0.0"
APP_NAME="voice-typer"
BUILD_DIR="dist"

echo "Building VoiceTyper v${VERSION}..."

# Clean build directory
rm -rf ${BUILD_DIR}
mkdir -p ${BUILD_DIR}

# Build flags
LDFLAGS="-s -w -X main.AppVersion=${VERSION}"

# Build for current platform
echo "Building for current platform..."
go build -ldflags="${LDFLAGS}" -o ${BUILD_DIR}/${APP_NAME}

# Cross-compile for other platforms
echo "Building for macOS (amd64)..."
GOOS=darwin GOARCH=amd64 go build -ldflags="${LDFLAGS}" -o ${BUILD_DIR}/${APP_NAME}-darwin-amd64

echo "Building for macOS (arm64)..."
GOOS=darwin GOARCH=arm64 go build -ldflags="${LDFLAGS}" -o ${BUILD_DIR}/${APP_NAME}-darwin-arm64

echo "Building for Windows (amd64)..."
GOOS=windows GOARCH=amd64 go build -ldflags="${LDFLAGS}" -o ${BUILD_DIR}/${APP_NAME}-windows-amd64.exe

echo "Building for Linux (amd64)..."
GOOS=linux GOARCH=amd64 go build -ldflags="${LDFLAGS}" -o ${BUILD_DIR}/${APP_NAME}-linux-amd64

echo "Build complete! Binaries are in ${BUILD_DIR}/"
```

### 6.2 打包脚本

**build/package.sh**

```bash
#!/bin/bash

# Package script for different platforms

set -e

VERSION="1.0.0"
BUILD_DIR="dist"
PACKAGE_DIR="packages"

mkdir -p ${PACKAGE_DIR}

# macOS: Create .app bundle
echo "Packaging for macOS..."
# (Implementation details omitted, involves creating Info.plist, etc.)

# Windows: Create installer
echo "Packaging for Windows..."
# (Can use tools like NSIS or Inno Setup)

# Linux: Create .deb and .rpm packages
echo "Packaging for Linux..."
# (Use fpm or native packaging tools)

echo "Packaging complete!"
```

---

## 七、平台特殊处理

### 7.1 macOS

**权限配置**：
- 需要在Info.plist中声明麦克风权限：
  ```xml
  <key>NSMicrophoneUsageDescription</key>
  <string>VoiceTyper needs microphone access for voice input</string>
  ```

**辅助功能权限**：
- 热键监听和文本输入需要辅助功能权限
- 用户首次运行时系统会提示授权

### 7.2 Windows

**管理员权限**：
- 全局热键监听可能需要管理员权限
- 可在manifest中声明：
  ```xml
  <requestedExecutionLevel level="requireAdministrator" />
  ```

**防火墙**：
- 如连接远程ASR服务，可能触发防火墙提示

### 7.3 Linux

**X11 vs Wayland**：
- X11：robotgo原生支持
- Wayland：需要额外工具（如ydotool）
  - 安装：`sudo apt install ydotool`
  - 配置权限：添加用户到input/uinput组

**权限配置**：
```bash
# Add user to input groups
sudo usermod -aG input $USER
sudo usermod -aG uinput $USER

# Configure udev rules
echo 'KERNEL=="uinput", MODE="0660", GROUP="uinput"' | \
  sudo tee /etc/udev/rules.d/99-uinput.rules

# Reload rules
sudo udevadm control --reload-rules
```

**桌面文件**（~/.local/share/applications/voice-typer.desktop）：
```desktop
[Desktop Entry]
Name=VoiceTyper
Comment=Voice input tool
Exec=/path/to/voice-typer
Icon=/path/to/icon.png
Terminal=false
Type=Application
Categories=Utility;
```

---

## 八、错误处理与日志

### 8.1 错误处理原则

1. **所有外部调用都应有错误处理**：录音、网络请求、文件IO
2. **用户友好的错误提示**：通过托盘状态和系统通知
3. **不崩溃原则**：关键流程错误不应导致程序退出
4. **降级方案**：如模拟输入失败自动回退到剪贴板

### 8.2 日志系统（可选扩展）

可使用标准库`log`或第三方库如`logrus`、`zap`：

```go
// 简单日志封装
package logger

import (
    "log"
    "os"
    "path/filepath"
)

var (
    infoLogger  *log.Logger
    errorLogger *log.Logger
)

func Init() {
    configDir, _ := config.GetConfigDir()
    logFile := filepath.Join(configDir, "voice-typer.log")
    
    f, err := os.OpenFile(logFile, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
    if err != nil {
        log.Printf("Failed to open log file: %v", err)
        return
    }
    
    infoLogger = log.New(f, "INFO: ", log.Ldate|log.Ltime|log.Lshortfile)
    errorLogger = log.New(f, "ERROR: ", log.Ldate|log.Ltime|log.Lshortfile)
}

func Info(format string, v ...interface{}) {
    if infoLogger != nil {
        infoLogger.Printf(format, v...)
    }
}

func Error(format string, v ...interface{}) {
    if errorLogger != nil {
        errorLogger.Printf(format, v...)
    }
}
```

---

## 九、测试策略

### 9.1 单元测试

每个模块应有对应测试文件：
- `config/config_test.go`
- `audio/recorder_test.go`
- `hotkey/parser_test.go`
- `api/client_test.go`

### 9.2 集成测试

测试完整流程：
1. 配置加载
2. 服务器连接
3. 录音->识别->输入

### 9.3 平台兼容性测试

在三个平台上分别测试：
- 热键监听是否正常
- 录音是否正常
- 文本输入是否正常
- UI显示是否正常

---

## 十、未来扩展方向

1. **语音指令**：支持语音命令控制（如"删除"、"换行"）
2. **多语言支持**：UI多语言
3. **更多输入方法**：如直接粘贴到特定应用
4. **统计功能**：记录使用次数、识别准确率
5. **云同步**：配置和词库云端同步
6. **插件系统**：支持第三方扩展

---

## 十一、关键实现提示

### 11.1 robotgo事件循环注意事项

`robotgo.EventStart()` 是阻塞调用，必须在goroutine中运行，且整个程序生命周期中只能调用一次。停止监听使用 `robotgo.EventEnd()`。

### 11.2 音频格式

ASR服务接受的格式是：
- 采样率：16000 Hz
- 通道数：1（单声道）
- 格式：S16（16位有符号整数）
- 字节序：小端（Little Endian）

malgo录制的数据已是此格式，可直接发送。

### 11.3 热键匹配的跨平台差异

robotgo在不同平台上的键码和修饰键处理有差异，建议：
1. 使用Rawcode而非Keycode进行匹配
2. 针对不同平台测试并调整匹配逻辑
3. 修饰键状态可能需要通过额外的按键状态跟踪

### 11.4 文本输入的平台差异

- **macOS**：robotgo.TypeStr 支持良好
- **Windows**：robotgo.TypeStr 支持良好
- **Linux X11**：robotgo.TypeStr 支持良好
- **Linux Wayland**：robotgo不直接支持，需检测Wayland并调用ydotool

检测Wayland后的处理：
```go
if platform.IsWayland() {
    // 使用ydotool
    cmd := exec.Command("ydotool", "type", text)
    return cmd.Run()
}
```

### 11.5 Fyne窗口置顶

Fyne的窗口置顶在某些平台上支持有限，可能需要平台特定API：
- macOS: NSWindow.setLevel
- Windows: SetWindowPos with HWND_TOPMOST
- Linux: _NET_WM_STATE_ABOVE

### 11.6 配置文件热重载（可选）

可使用文件监听库如`fsnotify`实现配置文件变更自动重载，但需注意：
- 重载时需重新初始化相关模块
- 避免重载过程中的竞态条件

---

## 十二、总结

本设计文档详细定义了VoiceTyper跨平台语音输入工具的完整架构，包括：

1. **模块化设计**：7个核心模块，职责清晰
2. **跨平台支持**：通过成熟的Go库实现三大平台支持
3. **配置灵活**：支持多服务器、热键自定义、词库管理
4. **用户体验**：系统托盘、视觉提示、状态反馈
5. **健壮性**：完善的错误处理和降级方案
6. **可扩展性**：模块化设计便于后续功能扩展

### 实现优先级建议

**第一阶段（MVP）**：
1. config模块（配置加载）
2. audio模块（录音功能）
3. api模块（ASR通信）
4. input/simulator模块（文本输入）
5. 简单的命令行版本测试

**第二阶段（完善）**：
6. hotkey模块（热键监听）
7. controller模块（核心控制器）
8. input/manager模块（输入管理）

**第三阶段（UI）**：
9. ui/tray模块（系统托盘）
10. ui/indicator模块（录音提示）
11. ui/notification模块（系统通知）

**第四阶段（优化）**：
12. 平台特定优化
13. 错误处理完善
14. 性能优化
15. 打包与分发

每个函数的实现都应遵循文档中的签名和逻辑说明，特别注意平台差异和错误处理。