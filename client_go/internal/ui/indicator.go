package ui

import (
	"fmt"
	"image/color"
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

	stopChan chan bool
}

// NewIndicator 创建提示窗口
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
	bg := canvas.NewRectangle(color.RGBA{
		R: uint8(i.opacity * 30),
		G: uint8(i.opacity * 30),
		B: uint8(i.opacity * 30),
		A: uint8(i.opacity * 255),
	})

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
	ticker := time.NewTicker(100 * time.Millisecond)

	go func() {
		for {
			select {
			case <-ticker.C:
				if i.visible {
					elapsed := time.Since(i.startTime).Seconds()
					i.timeLabel.SetText(fmt.Sprintf("%.1fs", elapsed))
				}
			case <-i.stopChan:
				ticker.Stop()
				return
			}
		}
	}()
}

// stopTimeUpdate 停止时间更新
func (i *Indicator) stopTimeUpdate() {
	select {
	case i.stopChan <- true:
	default:
	}
}

// Close 关闭提示窗口
func (i *Indicator) Close() {
	i.Hide()
	i.window.Close()
}
