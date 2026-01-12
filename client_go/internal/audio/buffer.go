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
