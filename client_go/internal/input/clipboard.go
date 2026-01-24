package input

import (
	"fmt"
	"os/exec"
	"time"

	"github.com/go-vgo/robotgo"
	"github.com/yourusername/voice-typer/pkg/platform"
	"golang.design/x/clipboard"
)

// ClipboardInserter 剪贴板粘贴输入器
type ClipboardInserter struct {
	initialized bool
}

// NewClipboardInserter 创建剪贴板输入器
func NewClipboardInserter() *ClipboardInserter {
	// 初始化clipboard
	err := clipboard.Init()
	if err != nil {
		fmt.Printf("Warning: clipboard init failed: %v\n", err)
	}
	return &ClipboardInserter{
		initialized: err == nil,
	}
}

// Insert 通过剪贴板粘贴输入文本
func (c *ClipboardInserter) Insert(text string) error {
	if text == "" {
		return nil
	}

	if !c.initialized {
		return fmt.Errorf("clipboard not initialized")
	}

	// 备份当前剪贴板（可选，避免覆盖用户数据）
	originalClip := clipboard.Read(clipboard.FmtText)
	originalLen := len(originalClip)

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
	if originalLen > 0 {
		clipboard.Write(clipboard.FmtText, originalClip)
	}

	return nil
}

// simulatePaste 模拟粘贴快捷键
func (c *ClipboardInserter) simulatePaste() error {
	// 根据平台选择修饰键
	modifier, key := platform.GetPasteShortcut()

	// 使用robotgo模拟按键
	// 注意：在Wayland下可能需要ydotool
	return c.simulateKeyPress(modifier, key)
}

// simulateKeyPress 模拟按键组合
func (c *ClipboardInserter) simulateKeyPress(modifier, key string) error {
	// 直接使用robotgo模拟按键（Windows）
	return c.simulateKeyPressRobotgo(modifier, key)
}

// simulateKeyPressRobotgo 使用robotgo模拟按键
func (c *ClipboardInserter) simulateKeyPressRobotgo(modifier, key string) error {
	// robotgo.KeyTap会自动处理修饰键
	// 格式：KeyTap("key", "modifier")
	return robotgo.KeyTap(key, modifier)
}
