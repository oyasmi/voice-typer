# VoiceTyper Client ↔ Server 协议

`protocol_version = 1`

本仓库的桌面客户端与 ASR 服务端通过 HTTP / WebSocket 通信。本文是**唯一**的协议来源——服务端、所有平台客户端必须遵守，遇到行为冲突以本文为准。

---

## 1. 鉴权

服务端 HTTP 与 WebSocket 走**同一套**规则（`server/voice_typer_server/auth.py:authorize_request`）：

| 配置 | listen 地址 | 鉴权要求 |
| --- | --- | --- |
| `--api-keys` 未配置 | 任意 | 放行 |
| `--api-keys` 已配置 | `127.0.0.1` | 放行（loopback 自然受信） |
| `--api-keys` 已配置 | 其他（含 `0.0.0.0`） | 要求 `Authorization: Bearer <key>`，否则 401 |

> 历史版本中 HTTP / WS 鉴权语义不一致（HTTP 凭 `remote_ip` 放行 localhost、WS 还要求 listen 地址也是 127.0.0.1），现已统一为上表。客户端无需关心差异。

---

## 2. 健康检查：`GET /health`

```json
{
  "status": "ok",
  "ready": true,
  "version": "1.2.0",
  "protocol_version": 1,
  "streaming": true,
  "llm_enabled": false,
  "asr_model":     "paraformer-zh-streaming",
  "offline_model": "paraformer-zh",
  "punc_model":    "ct-punc",
  "device":        "cpu"
}
```

- `ready`：模型是否完成加载，客户端启用功能前必须为 `true`。
- `version`：服务端语义化版本，客户端可记录到日志辅助诊断。
- `protocol_version`：本文档版本号。后续不兼容变更会递增。
- `streaming`：当前服务端处于流式（WebSocket）还是兼容（HTTP）模式。
- `asr_model` / `offline_model` / `punc_model` / `device`：模型实测元信息。

---

## 3. 非流式：`POST /recognize`

请求：
- `Content-Type: application/octet-stream`，body 为 16kHz / float32 / mono PCM。
- 可选 `X-Hotwords` 头（URL encoded 空格分隔）。
- query 可选 `llm_recorrect=true`。

响应：
```json
{ "text": "你好世界。", "duration": 1.23, "elapsed": 0.42, "llmElapsed": 0.31 }
```

`llmElapsed` 仅在启用 LLM 修正且确实调用时返回。

---

## 4. 流式：`WS /recognize/stream`

握手 query：`?llm_recorrect=true|false`

### 4.1 Client → Server

| 时机 | 类型 | 负载 |
| --- | --- | --- |
| 连接后立即 | text | `{"type":"start","hotwords":"词1 词2","sample_rate":16000}` |
| 录音中 ~600ms 一帧 | binary | float32 mono PCM，建议 9600 samples = 38400 bytes |
| 松开热键 | text | `{"type":"finalize"}` |

### 4.2 Server → Client

| 类型 | 负载 | 含义 |
| --- | --- | --- |
| `partial` | `{"type":"partial","text":"今天","seq":N}` | **增量**预览片段；见 §4.3 |
| `warning` | `{"type":"warning","code":"feed_failed","message":"..."}` | 非致命，连接保留，仍可 finalize |
| `final` | `{"type":"final","text":"今天天气不错。","asrElapsed":0.82,"llmElapsed":0.31?}` | 最终结果。随后服务端发送 close(1000) |
| `error` | `{"type":"error","code":"...","message":"..."}` | 致命，服务端随即关闭连接 |

### 4.3 partial 是"纯增量" — 客户端只需 append

**核心约定**：每条 `partial.text` 是**自上一次 partial 以来新增的字符串**，**不是**累计转写。

> 客户端实现示例（Swift）：
> ```swift
> self.accumulatedPreview += fragment   // 直接拼接即可
> ```

服务端责任（`server/voice_typer_server/recognizer.py:Session.feed`）：

- 模型偶发返回包含历史前缀的累计串时，服务端会做**差分**裁掉已发部分，再向客户端发送。
- 客户端**永远**只看到 delta；不需要做去重、prefix 检测之类的逻辑。

这样选择是因为流式数据通道只增长不回退，做最简单"append"语义对客户端最友好；任何"全量替换"或"乱序覆盖"都明确禁止。

### 4.4 warning vs error

| 维度 | warning | error |
| --- | --- | --- |
| 连接 | 保留 | 服务端关闭 |
| 客户端处理 | HUD 闪烁提示，继续录音 | 终止会话，HUD 显示错误，回到 idle |
| 已知 code | `feed_failed`、`no_session` | `bad_request`、`bad_state`、`internal` |

---

## 5. 客户端约束

### 5.1 短录音过滤（≤ 0.3s）

录音时长低于 **300ms** 的会话视为误触：

- 客户端**不应**触发 `finalize`，也不应进入 `.recognizing` 状态。
- 流式：直接关闭 WebSocket；非流式：不发出 HTTP 请求。
- 服务端不做强制校验，仅作客户端约定。

阈值常量在客户端代码中显式标注：

- Swift：`VoiceTyperController.minimumRecordingDuration`

### 5.2 scheme

`ServerConfig.scheme` 取值 `http` 或 `https`：

- `http` ⇒ HTTP 用 `http://`，WS 用 `ws://`
- `https` ⇒ HTTP 用 `https://`，WS 用 `wss://`

客户端构造 URL 时**必须**经过 `ServerConfig.httpScheme` / `ServerConfig.wsScheme` 派生，禁止硬编码 `http://` 或 `ws://`。

---

## 6. 版本变更

| protocol_version | 主要变更 |
| --- | --- |
| 1 | 首版稳定协议：鉴权统一；partial 明确为增量；warning 帧；`/health` 含版本与模型；scheme 可选 `https/wss`；短录音 ≤0.3s 客户端过滤 |
