package api

// RecognizeRequest 识别请求（用于构建multipart表单）
type RecognizeRequest struct {
	Audio        []byte // 音频数据（S16格式，16kHz，单声道）
	Hotwords     string // 热词字符串（空格分隔）
	LLMRecorrect bool   // 是否启用LLM修正
}

// RecognizeResponse 识别响应
type RecognizeResponse struct {
	Text string `json:"text"`
}

// HealthResponse 健康检查响应
type HealthResponse struct {
	Ready bool `json:"ready"`
}
