package controller

import (
	"fmt"
	"log"
	"sync"
	"time"

	"github.com/yourusername/voice-typer/internal/api"
	"github.com/yourusername/voice-typer/internal/audio"
	"github.com/yourusername/voice-typer/internal/config"
	"github.com/yourusername/voice-typer/internal/hotkey"
	"github.com/yourusername/voice-typer/internal/input"
	"github.com/yourusername/voice-typer/internal/ui"
)

// Controller 核心控制器
type Controller struct {
	config    *config.Config
	recorder  *audio.Recorder
	listener  *hotkey.Listener
	apiClient *api.Client
	inputMgr  input.Inserter

	// 状态
	enabled   bool
	recording bool
	mutex     sync.Mutex

	// 回调
	onStatusChange func(string)
}

// NewController 创建控制器
func NewController(cfg *config.Config) (*Controller, error) {
	c := &Controller{
		config:  cfg,
		enabled: false,
	}

	return c, nil
}

// Initialize 初始化所有模块
func (c *Controller) Initialize(onStatusChange func(string)) error {
	c.onStatusChange = onStatusChange
	c.updateStatus("Initializing...")

	// 1. 选择可用的ASR服务器
	c.updateStatus("Connecting to ASR service...")

	availableServer, idx := c.config.GetFirstAvailableServer(func(srv config.ServerConfig) bool {
		client := api.NewClient(srv.Host, srv.Port, srv.Timeout, srv.APIKey, srv.LLMRecorrect)
		ready, _ := client.HealthCheck()
		return ready
	})

	if availableServer == nil {
		return fmt.Errorf("no available ASR server found")
	}

	log.Printf("Using server[%d]: %s:%d", idx, availableServer.Host, availableServer.Port)

	// 2. 初始化API客户端
	c.apiClient = api.NewClient(
		availableServer.Host,
		availableServer.Port,
		availableServer.Timeout,
		availableServer.APIKey,
		availableServer.LLMRecorrect,
	)

	// 3. 初始化录音器
	c.updateStatus("Initializing audio recorder...")
	recorder, err := audio.NewRecorder()
	if err != nil {
		return fmt.Errorf("create recorder: %w", err)
	}
	c.recorder = recorder

	// 4. 解析热键
	c.updateStatus("Setting up hotkey...")
	mods, keyCode, err := hotkey.ParseHotkey(c.config.Hotkey.Modifiers, c.config.Hotkey.Key)
	if err != nil {
		return fmt.Errorf("parse hotkey: %w", err)
	}

	// 5. 初始化热键监听器
	c.listener = hotkey.NewListener(
		mods,
		keyCode,
		c.onHotkeyPress,
		c.onHotkeyRelease,
	)

	// 6. 初始化输入管理器
	c.updateStatus("Initializing input manager...")
	c.inputMgr = input.NewClipboardInserter()

	c.updateStatus("Ready")
	return nil
}

// Start 启动控制器
func (c *Controller) Start() error {
	c.mutex.Lock()
	defer c.mutex.Unlock()

	if c.enabled {
		return fmt.Errorf("controller already enabled")
	}

	if err := c.listener.Start(); err != nil {
		return fmt.Errorf("start listener: %w", err)
	}

	c.enabled = true
	c.updateStatus("Enabled")
	log.Println("Controller started")

	return nil
}

// Stop 停止控制器
func (c *Controller) Stop() error {
	c.mutex.Lock()
	defer c.mutex.Unlock()

	if !c.enabled {
		return nil
	}

	if err := c.listener.Stop(); err != nil {
		log.Printf("Warning: stop listener failed: %v", err)
	}

	c.enabled = false
	c.updateStatus("Disabled")
	log.Println("Controller stopped")

	return nil
}

// IsEnabled 检查是否已启用
func (c *Controller) IsEnabled() bool {
	c.mutex.Lock()
	defer c.mutex.Unlock()
	return c.enabled
}

// onHotkeyPress 热键按下处理
func (c *Controller) onHotkeyPress() {
	c.mutex.Lock()
	defer c.mutex.Unlock()

	if !c.enabled || c.recording {
		return
	}

	c.recording = true
	log.Println("Hotkey pressed - starting recording")

	// 启动录音
	if err := c.recorder.Start(); err != nil {
		log.Printf("Error starting recorder: %v", err)
		c.recording = false
		c.updateStatus("Recording error")
		return
	}

	c.updateStatus("Recording...")
}

// onHotkeyRelease 热键释放处理
func (c *Controller) onHotkeyRelease() {
	c.mutex.Lock()
	defer c.mutex.Unlock()

	if !c.enabled || !c.recording {
		return
	}

	c.recording = false
	log.Println("Hotkey released - stopping recording")

	// 停止录音
	audioData, err := c.recorder.Stop()
	if err != nil {
		log.Printf("Error stopping recorder: %v", err)
		c.updateStatus("Recording error")
		return
	}

	if len(audioData) == 0 {
		log.Println("No audio data recorded")
		c.updateStatus("No audio")
		return
	}

	// 启动识别goroutine
	go c.doRecognize(audioData)
}

// doRecognize 执行语音识别
func (c *Controller) doRecognize(audioData []byte) {
	c.updateStatus("Recognizing...")

	// 加载热词
	hotwords, _ := c.config.GetHotwordsString()

	// 调用API
	text, err := c.apiClient.Recognize(audioData, hotwords)
	if err != nil {
		log.Printf("Recognition failed: %v", err)
		c.updateStatus("Recognition failed")
		return
	}

	if text == "" {
		log.Println("No text recognized")
		c.updateStatus("No text")
		return
	}

	log.Printf("Recognized text: %s", text)
	c.updateStatus(fmt.Sprintf("Inserting: %d chars", len(text)))

	// 插入文本
	if err := c.inputMgr.Insert(text); err != nil {
		log.Printf("Insert failed: %v", err)
		c.updateStatus("Insert failed")
		return
	}

	c.updateStatus(fmt.Sprintf("Inserted: %d chars", len(text)))

	// 延迟后返回就绪状态
	time.Sleep(1500 * time.Millisecond)
	c.updateStatus("Ready")
}

// updateStatus 更新状态
func (c *Controller) updateStatus(status string) {
	if c.onStatusChange != nil {
		c.onStatusChange(status)
	}
}

// Close 关闭控制器，释放资源
func (c *Controller) Close() error {
	c.mutex.Lock()
	defer c.mutex.Unlock()

	if c.recorder != nil {
		return c.recorder.Close()
	}

	return nil
}
