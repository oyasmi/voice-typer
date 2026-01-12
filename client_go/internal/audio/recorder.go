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

	// 获取录音数据（S16格式，直接返回）
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
