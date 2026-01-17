package input

import (
	"fmt"
	"time"

	"github.com/go-vgo/robotgo"
)

// ClipboardInserter 剪贴板粘贴输入器
type ClipboardInserter struct{}

// NewClipboardInserter 创建剪贴板输入器
func NewClipboardInserter() *ClipboardInserter {
	return &ClipboardInserter{}
}

// Insert 通过剪贴板粘贴输入文本
func (c *ClipboardInserter) Insert(text string) error {
	if text == "" {
		return nil
	}

	// 备份当前剪贴板
	originalClip := robotgo.ReadAllText()

	// 写入文本到剪贴板
	err := robotgo.WriteAll(text)
	if err != nil {
		return fmt.Errorf("write clipboard: %w", err)
	}

	// 等待剪贴板就绪
	time.Sleep(50 * time.Millisecond)

	// 模拟 Ctrl+V 粘贴
	if err := robotgo.KeyTap("v", "ctrl"); err != nil {
		return fmt.Errorf("paste: %w", err)
	}

	// 等待粘贴完成
	time.Sleep(100 * time.Millisecond)

	// 恢复原剪贴板内容
	if originalClip != "" {
		_ = robotgo.WriteAll(originalClip)
	}

	return nil
}
