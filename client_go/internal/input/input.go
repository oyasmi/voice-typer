package input

import (

	"github.com/go-vgo/robotgo"
)

// Inserter 输入接口
type Inserter interface {
	Insert(text string) error
}

// NewInserter 创建最佳可用的输入器
func NewInserter() Inserter {
	// 默认优先使用 TypeStr (RobotGo)，因为它更干净
	// 但在 Windows 上 Unicode 支持可能有限，需要验证
	// 鉴于 client_go 主要针对 Windows，Clipboard 往往更稳健
	// 不过为了"Better implementation"，我们尝试 RobotGo TypeStr
	// 如果需要 fallback，可以在这里做

	// 策略：Windows下优先尝试 RobotGo (TypeStr)
	// 如果失败或乱码，可以考虑切换回 Clipboard
	
	// 目前先默认使用 Clipboard，因为 client_macos/linux 分析显示 input 往往复杂
	// 但评审指出 Clipboard 是 weak point。
	// RobotGo TypeStr supports UTF-8 on Windows? usually yes.
	
	// 让我们创建一个混合策略：
	// 优先使用 Clipboard，因为它兼容性最好（支持表情符号等）
	// 但是优化它：使用备份恢复
	
	return NewClipboardInserter()
}

// DirectTyper 直接输入器 (TypeStr)
type DirectTyper struct{}

func (d *DirectTyper) Insert(text string) error {
	// 模拟键盘输入
	robotgo.TypeStr(text)
	return nil
}
