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

	onPress   func()
	onRelease func()

	mutex     sync.Mutex
	running   bool
	pressed   bool // 当前热键是否被按下
}

// NewListener 创建热键监听器
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
