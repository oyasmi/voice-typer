package api

import (
	"bytes"
	"fmt"
	"mime/multipart"
	"net/http"
	"time"

	"github.com/go-resty/resty/v2"
)

// Client ASR API客户端
type Client struct {
	baseURL      string
	timeout      time.Duration
	apiKey       string
	llmRecorrect bool
	httpClient   *resty.Client
}

// NewClient 创建API客户端
func NewClient(host string, port int, timeout float64, apiKey string, llmRecorrect bool) *Client {
	baseURL := fmt.Sprintf("http://%s:%d", host, port)

	client := resty.New().
		SetBaseURL(baseURL).
		SetTimeout(time.Duration(timeout*float64(time.Second))).
		SetHeader("User-Agent", "VoiceTyper-Go/1.0")

	// 如果有API密钥且不是本地地址，添加认证头
	if apiKey != "" && host != "127.0.0.1" && host != "localhost" {
		client.SetHeader("Authorization", fmt.Sprintf("Bearer %s", apiKey))
	}

	return &Client{
		baseURL:      baseURL,
		timeout:      time.Duration(timeout * float64(time.Second)),
		apiKey:       apiKey,
		llmRecorrect: llmRecorrect,
		httpClient:   client,
	}
}

// HealthCheck 健康检查
func (c *Client) HealthCheck() (bool, error) {
	var resp HealthResponse

	res, err := c.httpClient.R().
		SetResult(&resp).
		Get("/health")

	if err != nil {
		return false, fmt.Errorf("health check request: %w", err)
	}

	if res.StatusCode() != http.StatusOK {
		return false, fmt.Errorf("health check failed: status %d", res.StatusCode())
	}

	return resp.Ready, nil
}

// Recognize 识别音频
func (c *Client) Recognize(audio []byte, hotwords string) (string, error) {
	if len(audio) == 0 {
		return "", fmt.Errorf("empty audio data")
	}

	// 构建multipart表单
	body := &bytes.Buffer{}
	writer := multipart.NewWriter(body)

	// 添加音频文件
	part, err := writer.CreateFormFile("audio", "audio.raw")
	if err != nil {
		return "", fmt.Errorf("create form file: %w", err)
	}
	if _, err := part.Write(audio); err != nil {
		return "", fmt.Errorf("write audio data: %w", err)
	}

	// 添加热词
	if hotwords != "" {
		if err := writer.WriteField("hotwords", hotwords); err != nil {
			return "", fmt.Errorf("write hotwords: %w", err)
		}
	}

	// 添加LLM修正参数
	llmValue := "false"
	if c.llmRecorrect {
		llmValue = "true"
	}
	if err := writer.WriteField("llm_recorrect", llmValue); err != nil {
		return "", fmt.Errorf("write llm_recorrect: %w", err)
	}

	// 关闭writer
	if err := writer.Close(); err != nil {
		return "", fmt.Errorf("close writer: %w", err)
	}

	// 发送请求
	var resp RecognizeResponse
	res, err := c.httpClient.R().
		SetHeader("Content-Type", writer.FormDataContentType()).
		SetBody(body.Bytes()).
		SetResult(&resp).
		Post("/recognize")

	if err != nil {
		return "", fmt.Errorf("recognize request: %w", err)
	}

	if res.StatusCode() != http.StatusOK {
		return "", fmt.Errorf("recognize failed: status %d, body: %s",
			res.StatusCode(), res.String())
	}

	return resp.Text, nil
}
