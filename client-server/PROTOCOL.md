# VoiceTyper Client ↔ Server 协议

`protocol_version = 2`

本目录的分体式桌面客户端与 ASR 服务端通过 HTTP / WebSocket 通信。本文是**唯一**的协议来源——服务端、
所有分体式客户端（`client_macos_swift/` VoiceTyperClient、`client_windows_native/`、`client_linux/`）
必须遵守，遇到行为冲突以本文为准。

> **不适用于根目录的 `macos/` 与 `windows/` 一体化 App**：它们把识别引擎内置进应用进程，
> 没有独立的服务端连接，因此不产生本文描述的网络交互。其本地会话在语义上镜像本协议的流式
> 部分（§4），但不是网络协议。详见 [macOS 设计文档](../macos/DESIGN.md) 与
> [Windows 设计文档](../windows/DESIGN.md)。

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
  "version": "1.5.0",
  "protocol_version": 2,
  "streaming": true,
  "llm_enabled": false,
  "asr_model":     "sensevoice-small",
  "offline_model": "sensevoice-small",
  "punc_model":    null,
  "device":        "cpu"
}
```

- `ready`：模型是否完成加载，客户端启用功能前必须为 `true`。
- `version`：服务端语义化版本，客户端可记录到日志辅助诊断。
- `protocol_version`：本文档版本号。后续不兼容变更会递增。
- `streaming`：当前服务端处于流式（WebSocket）还是兼容（HTTP）模式。
- `asr_model` / `offline_model` / `punc_model` / `device`：模型实测元信息。
  `punc_model` 为 `null` 表示不需要独立标点模型（SenseVoice 自带标点与 ITN）。
  默认的 SenseVoice 单模型流式下 `asr_model` 与 `offline_model` 相同（预览与终稿同源）；
  只有 `--offline-model paraformer-zh` 的双模型流式才会出现两者不同。

---

## 3. 非流式：`POST /recognize`

请求：
- `Content-Type: application/octet-stream`，body 为 16kHz / float32 / mono PCM。
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
| 连接后立即 | text | `{"type":"start","sample_rate":16000}` |
| 录音中 ~600ms 一帧 | binary | float32 mono PCM，建议 9600 samples = 38400 bytes |
| 松开热键 | text | `{"type":"finalize"}` |

### 4.2 Server → Client

| 类型 | 负载 | 含义 |
| --- | --- | --- |
| `partial` | `{"type":"partial","text":"今天天气","seq":N}` | **全量**预览文本；见 §4.3 |
| `warning` | `{"type":"warning","code":"feed_failed","message":"..."}` | 非致命，连接保留，仍可 finalize |
| `final` | `{"type":"final","text":"今天天气不错。","asrElapsed":0.82,"llmElapsed":0.31?}` | 最终结果。随后服务端发送 close(1000) |
| `error` | `{"type":"error","code":"...","message":"..."}` | 致命，服务端随即关闭连接 |

### 4.3 partial 是"全量文本" — 客户端只需替换

> **协议 v2 变更（服务端 1.5.0）**：此前 `partial.text` 是增量片段，客户端做字符串拼接。
> 现在是全量文本，客户端直接替换。旧客户端连新服务端会看到预览重复堆叠——
> 但**仅影响预览显示**，实际上屏的文本永远来自 `final` 帧，不受影响。

**核心约定**：每条 `partial.text` 是**当前完整的预览转写**，**不是**增量。

> 客户端实现示例（Swift）：
> ```swift
> self.accumulatedPreview = text   // 直接替换
> ```

之所以不能再用增量：默认的 SenseVoice 预览是**对已累积音频整段重跑**，后一次结果会
**修正**前一次的文字（"识别功能和并" → "识别功能合并"），增量语义无法表达这种回溯修改。

服务端责任（`server/voice_typer_server/recognizer.py`，`SenseVoiceSession.preview` /
`Session.preview`）：

- 无论底层是整段重跑还是逐块流式模型，向客户端发出的都是全量文本。
- 文本没有变化时不发 `partial`，客户端不必去重。
- `seq` 单调递增，仅用于诊断；客户端不需要靠它排序（WebSocket 本身保序）。

### 4.4 warning vs error

| 维度 | warning | error |
| --- | --- | --- |
| 连接 | 保留 | 服务端关闭 |
| 客户端处理 | HUD 闪烁提示，继续录音 | 终止会话，HUD 显示错误，回到 idle |
| 已知 code | `feed_failed`、`no_session`、`bad_frame`、`session_capped` | `bad_request`、`bad_state`、`internal` |

补充说明（服务端 1.5.1）：

- `bad_frame`：二进制帧长度不是 4 的倍数（不是合法的 float32 PCM），服务端丢弃这一帧，连接与已累积音频不受影响。
- `session_capped`：会话录音时长达到 §5.3 的服务端上限，此后音频不再录入，但仍可正常 `finalize`。
- `bad_request`：除原有的「JSON 解析失败」外，也用于 `start` 帧携带非 16000 的 `sample_rate`（此时服务端随即关闭连接，属于 error 语义）。

---

## 5. 客户端约束

### 5.1 短录音过滤（≤ 0.3s）

录音时长低于 **300ms** 的会话视为误触：

- 客户端**不应**触发 `finalize`，也不应进入 `.recognizing` 状态。
- 流式：直接关闭 WebSocket；非流式：不发出 HTTP 请求。
- 服务端不做强制校验，仅作客户端约定。

阈值常量在客户端代码中显式标注：

- Swift（macOS）：`VoiceTyperController.minimumRecordingDuration`
- C#（Windows）：`VoiceTyperController.MinimumRecordingDuration`
- Python（Linux）：`controller.py` 中按样本数判断（4800 samples @ 16kHz）

### 5.2 scheme

`ServerConfig.scheme` 取值 `http` 或 `https`：

- `http` ⇒ HTTP 用 `http://`，WS 用 `ws://`
- `https` ⇒ HTTP 用 `https://`，WS 用 `wss://`

客户端构造 URL 时**必须**经过 `ServerConfig.httpScheme` / `ServerConfig.wsScheme` 派生，禁止硬编码 `http://` 或 `ws://`。

### 5.3 服务端侧的会话时长上限（300s）

与 §5.1 不同，这一条服务端**会**强制校验：单个 WS 会话累计接收音频达到 300 秒后，
服务端不再接受新的音频帧，直到该会话 `finalize` 或断开：

- 达到上限时服务端发一次 `warning`（`code: "session_capped"`），此后每帧新音频被静默丢弃。
- 会话已累积的部分不受影响，`finalize` 仍会对前 300 秒音频产出正常的 `final`。
- 客户端无需为此改动：即使不处理这条 warning，超限只是让预览与最终文本停在 300 秒处，不会报错断连。

这道上限只保护服务端资源，不替代 §5.1 的客户端最短录音过滤。

---

## 6. 版本变更

| protocol_version | 主要变更 |
| --- | --- |
| 1 | 首版稳定协议：鉴权统一；partial 明确为增量；warning 帧；`/health` 含版本与模型；scheme 可选 `https/wss`；短录音 ≤0.3s 客户端过滤 |
| 2 | 服务端 1.5.0：`partial.text` 改为**全量文本**，客户端替换而非拼接（见 §4.3）；默认改用 SenseVoice 单模型同时产出预览与终稿，`punc_model` 可为 `null` |
| 2（追加） | 服务端 1.5.1（非破坏性）：新增 `bad_frame`/`session_capped` warning、`start.sample_rate` 校验、服务端侧 300s 会话上限（§5.3）；SenseVoice 预览改为滑动窗口重跑，长录音下预览与 finalize 延迟不再随时长增长 |
