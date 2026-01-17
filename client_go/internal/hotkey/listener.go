package hotkey

import (
	"fmt"
	"sync"

	hook "github.com/robotn/gohook"
)

// Listener 热键监听器
type Listener struct {
	keyCombo []string // 热键组合，如 ["ctrl", "shift", "f1"]

	onPress   func()
	onRelease func()

	mutex   sync.Mutex
	running bool
	pressed bool // 当前热键是否被按下
}

// NewListener 创建热键监听器
func NewListener(modifiers *Modifiers, keyName string, onPress, onRelease func()) *Listener {
	// 构建热键组合字符串数组
	keyCombo := buildKeyCombo(modifiers, keyName)

	return &Listener{
		keyCombo:  keyCombo,
		onPress:   onPress,
		onRelease: onRelease,
		running:   false,
		pressed:   false,
	}
}

// buildKeyCombo 构建热键组合字符串数组
func buildKeyCombo(modifiers *Modifiers, keyName string) []string {
	combo := []string{}

	if modifiers.Ctrl {
		combo = append(combo, "ctrl")
	}
	if modifiers.Alt {
		combo = append(combo, "alt")
	}
	if modifiers.Shift {
		combo = append(combo, "shift")
	}
	if modifiers.Win {
		combo = append(combo, "win")
	}

	// 添加主键
	combo = append(combo, keyName)

	return combo
}

// Start 启动监听
func (l *Listener) Start() error {
	l.mutex.Lock()
	if l.running {
		l.mutex.Unlock()
		return fmt.Errorf("listener already running")
	}
	l.running = true
	l.mutex.Unlock()

	// 注册KeyDown事件 - hook.Register 会自动处理修饰键组合
	hook.Register(hook.KeyDown, l.keyCombo, func(e hook.Event) {
		l.mutex.Lock()
		defer l.mutex.Unlock()

		if !l.running {
			return
		}

		// 热键被按下
		if !l.pressed {
			l.pressed = true
			if l.onPress != nil {
				go l.onPress() // 异步调用，避免阻塞事件循环
			}
		}
	})

	// 注册KeyUp事件
	hook.Register(hook.KeyUp, l.keyCombo, func(e hook.Event) {
		l.mutex.Lock()
		defer l.mutex.Unlock()

		if !l.running {
			return
		}

		// 热键被释放
		if l.pressed {
			l.pressed = false
			if l.onRelease != nil {
				go l.onRelease()
			}
		}
	})

	// 启动事件循环
	go func() {
		s := hook.Start()
		<-hook.Process(s)
	}()

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
	hook.End()

	return nil
}

// IsRunning 检查是否在运行
func (l *Listener) IsRunning() bool {
	l.mutex.Lock()
	defer l.mutex.Unlock()
	return l.running
}
