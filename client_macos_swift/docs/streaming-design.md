# 流式语音识别改造 设计与实现

> 分支：`feat/streaming`
> 目标范围：macOS Swift 客户端 + Python 服务端
> 状态：设计稿，待按里程碑实现

---

## 1. 背景与需求

### 1.1 现状

当前链路是**整段录音后一次性识别**：

1. 客户端按住热键开始录音，松开热键停止；
2. 整段 float32 PCM 通过 HTTP POST 发到服务端 `/recognize`；
3. 服务端跑 Paraformer 离线模型 → ct-punc → 可选 LLM 校对 → 返回最终文本；
4. 客户端通过剪贴板 + Cmd+V 或 AX 接口贴入用户当前输入框。

问题：
- 用户松开热键后才开始识别，**整段 ASR 耗时**完整暴露在感知延迟里；
- 录音中无任何文本反馈，体感上"卡了一下"。

### 1.2 目标

- **录音期间**就开始 ASR，文本以"预览"形式回显到 HUD 上；
- 松开热键后只剩"最后 flush + ct-punc + LLM 校对 + 上屏"几步，感知延迟显著降低；
- 体验上，用户在说话过程中就能看见识别结果在 HUD 中滚动。

### 1.3 非目标

- **不做**新旧链路兼容；客户端和服务端**必须同版本升级**。
- **不做**双模型方案（流式预览 + 离线最终）；最终文本就是流式累计输出后跑 ct-punc/LLM。
- **不做**输入法风格的内联未上屏文本（IMK/marked text），仅在 HUD 中回显。
- **不做**热词支持；若 streaming 模型不接受 hotword 参数，接受这一退步。

### 1.4 约束

- 模型固定使用 `paraformer-zh-streaming`；chunk 配置 `[0, 10, 5]`，对应每 600ms 喂一片，右侧 lookahead 300ms。
- 后端仍使用 `funasr-onnx`（已确认支持 streaming 模型加载）。
- 协议固定使用 WebSocket，不再保留 HTTP `/recognize`。

---

## 2. 总体设计

```
┌───────────────────── macOS Client ────────────────────┐         WebSocket          ┌──────────── Server ────────────┐
│                                                       │                            │                                │
│  Hotkey ─▶ AudioCaptureService                        │                            │  /recognize/stream             │
│                │ (16kHz f32, 600ms chunk tap)         │                            │      │                         │
│                ▼                                      │  ── binary frame ──▶       │      ▼                         │
│        StreamingASRClient (URLSessionWebSocketTask)   │                            │  StreamingSpeechRecognizer     │
│                │                                      │  ◀── {"type":"partial"}    │  (per-conn cache,              │
│                ▼                                      │                            │   paraformer-zh-streaming)     │
│        VoiceTyperController                           │                            │      │                         │
│                │                                      │  ── {"type":"finalize"} ─▶ │      ▼ on finalize             │
│         ┌──────┴───────┐                              │                            │   ct-punc → LLM (optional)     │
│         ▼              ▼                              │  ◀── {"type":"final"}      │      │                         │
│   RecordingHUD    TextInsertionService                │                            │      ▼ close                   │
│   (rolling)       (final only)                        │                            │                                │
└───────────────────────────────────────────────────────┘                            └────────────────────────────────┘
```

**关键设计选择**：

- 客户端到服务端是**单向音频流**，服务端到客户端是**partial / final / error 消息流**。
- 每个录音会话 = 一个 WebSocket 连接。会话结束（final 收到或错误）即关闭，下次录音重新建连。
- partial 文本**只用于 HUD 预览**，不参与上屏；final 文本是唯一可上屏的内容。
- ct-punc 与 LLM 均只在 finalize 阶段运行一次。

---

## 3. 协议规范

### 3.1 端点

```
ws://{host}:{port}/recognize/stream?llm_recorrect={true|false}
```

握手 header：
- `Authorization: Bearer <api_key>`（沿用现有鉴权；本地 127.0.0.1 仍可免鉴权）

### 3.2 客户端 → 服务端

| 时机 | 帧类型 | 载荷 |
|------|--------|------|
| 连接建立后 | text | `{"type":"start","hotwords":"词1 词2","sample_rate":16000}` |
| 录音过程中，每 600ms | binary | float32 PCM，**9600 samples = 38400 bytes** |
| 用户松开热键 | text | `{"type":"finalize"}` |

约定：
- 二进制帧长度必须严格 = 38400 bytes（最后一片可短于此，紧接其后的应是 `finalize`）。
- `hotwords` 字段保留传输，**服务端可忽略**（streaming 模型不一定支持，文档需注明）。

### 3.3 服务端 → 客户端

| 时机 | 帧类型 | 载荷 |
|------|--------|------|
| 每个 chunk 推完且有增量 | text | `{"type":"partial","text":"今天","seq":3}` |
| finalize 完成 | text | `{"type":"final","text":"今天天气不错。","asrElapsed":0.82,"llmElapsed":0.41}` |
| 任意错误 | text | `{"type":"error","code":"...","message":"..."}` |
| 完成或错误后 | close | normal close (1000)，或 4xxx 业务错误码 |

约定：
- `partial.text` 是**增量片段**，不是累计；客户端自行累加。
- partial 不带标点。final 带标点。
- 静音 chunk 可能不发 partial（或发空串，客户端可丢弃）。
- 任何 error 之后服务端关闭连接；客户端**不重连、不补偿、不部分上屏**，直接进入 `.error` 状态。

### 3.4 关闭码（自定义业务码）

- `1000` 正常关闭（已收到 final 或客户端主动断开）
- `4401` 鉴权失败
- `4503` 服务未就绪（模型未加载）
- `4500` 内部错误

---

## 4. 服务端实现

### 4.1 文件变更

| 文件 | 操作 |
|------|------|
| `voice_typer_server/recognizer.py` | 改写：用 `StreamingSpeechRecognizer` 替换 `SpeechRecognizer` |
| `voice_typer_server/app.py` | 删除 `RecognizeHandler` 与 `/recognize` 路由；新增 `StreamRecognizeHandler` 与 `/recognize/stream` 路由 |
| `voice_typer_server/cli.py` | 默认模型改为 `paraformer-zh-streaming`；新增 `--chunk-size 0,10,5` 选项 |
| `voice_typer_server/auth.py` | 抽出鉴权校验函数，供 WS handler 复用 |

### 4.2 `StreamingSpeechRecognizer`

```python
class StreamingSpeechRecognizer:
    def __init__(self,
                 model_name="paraformer-zh-streaming",
                 punc_model="ct-punc",
                 device="cpu",
                 chunk_size=(0, 10, 5),
                 encoder_chunk_look_back=4,
                 decoder_chunk_look_back=1,
                 intra_op_num_threads=4):
        ...

    def initialize(self): ...                # 加载 streaming ASR + ct-punc

    @property
    def is_ready(self) -> bool: ...

    def new_session(self) -> "Session":      # 工厂方法，每个 WS 连接一个 Session
        return Session(self)


class Session:
    def __init__(self, owner: StreamingSpeechRecognizer):
        self._owner = owner
        self._cache: dict = {}
        self._fragments: list[str] = []

    def feed(self, audio_chunk: np.ndarray) -> str:
        """喂入一个 600ms chunk，返回该 chunk 的文本增量（可能为空）"""
        result = self._owner._asr(
            audio_chunk,
            cache=self._cache,
            is_final=False,
            chunk_size=self._owner.chunk_size,
            encoder_chunk_look_back=self._owner.encoder_chunk_look_back,
            decoder_chunk_look_back=self._owner.decoder_chunk_look_back,
        )
        fragment = extract_text(result)
        if fragment:
            self._fragments.append(fragment)
        return fragment

    def finalize(self, tail_chunk: np.ndarray | None) -> str:
        """flush 尾巴，跑 ct-punc，返回最终文本"""
        if tail_chunk is not None and len(tail_chunk) > 0:
            result = self._owner._asr(
                tail_chunk, cache=self._cache, is_final=True, ...
            )
            fragment = extract_text(result)
            if fragment:
                self._fragments.append(fragment)
        full_text = "".join(self._fragments)
        if self._owner._punc and full_text:
            full_text = self._owner._punc(full_text)
        return full_text
```

要点：
- `cache` dict 由 funasr 内部填充，外部只需保证同一会话复用、跨会话不污染。
- `Session` 对象生命周期 = WS 连接生命周期；连接 close 时丢弃。
- `extract_text` 复用现有 `_extract_asr_text` / `_extract_punc_text` 逻辑。

### 4.3 `StreamRecognizeHandler`

```python
class StreamRecognizeHandler(tornado.websocket.WebSocketHandler):
    def check_origin(self, origin): return True

    def prepare(self):
        if not check_api_key(self.request, self.settings):
            self.set_status(401)
            self.finish()

    def open(self):
        recognizer = self.settings["recognizer"]
        if not recognizer or not recognizer.is_ready:
            self.close(code=4503, reason="not ready"); return
        self.llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        self.session = None
        self.hotwords = ""
        self.seq = 0
        self.tail_buffer = bytearray()       # 不足 9600 的尾巴

    async def on_message(self, message):
        try:
            if isinstance(message, (bytes, bytearray)):
                if self.session is None: return
                chunk = np.frombuffer(message, dtype=np.float32)
                loop = asyncio.get_event_loop()
                fragment = await loop.run_in_executor(
                    self.settings["executor"], self.session.feed, chunk
                )
                if fragment:
                    await self.write_message({"type":"partial","text":fragment,"seq":self.seq})
                    self.seq += 1
            else:
                msg = json.loads(message)
                if msg.get("type") == "start":
                    self.hotwords = msg.get("hotwords", "")  # 流式忽略，仅记录
                    self.session = self.settings["recognizer"].new_session()
                elif msg.get("type") == "finalize":
                    await self._do_finalize()
        except Exception as exc:
            logger.exception("WS handler error")
            await self._send_error("internal", str(exc))
            self.close(code=4500)

    async def _do_finalize(self):
        loop = asyncio.get_event_loop()
        t0 = time.time()
        text = await loop.run_in_executor(
            self.settings["executor"], self.session.finalize, None
        )
        asr_elapsed = round(time.time() - t0, 3)

        llm_elapsed = None
        if self.llm_recorrect and self.settings["llm_client"] and text.strip():
            t1 = time.time()
            try:
                text = await self.settings["llm_client"].correct_text(text)
                llm_elapsed = round(time.time() - t1, 3)
            except Exception as exc:
                logger.warning(f"LLM correction failed: {exc}")

        payload = {"type":"final","text":text,"asrElapsed":asr_elapsed}
        if llm_elapsed is not None: payload["llmElapsed"] = llm_elapsed
        await self.write_message(payload)
        self.close(code=1000)

    def on_close(self):
        self.session = None
```

### 4.4 并发说明

- 现有 `ThreadPoolExecutor(max_workers=1)` 保留：多个 WS 连接并发存在时，模型推理串行排队。
- 本地单用户场景不会有并发，**不做优化**。
- README 中标注："当前流式实现假设单客户端使用，多并发会导致 partial 延迟堆积。"

---

## 5. 客户端实现

### 5.1 文件变更

| 文件 | 操作 |
|------|------|
| `Services/AudioCaptureService.swift` | 改造：新增 `onChunk` 回调，按 9600 samples 切片输出 |
| `Services/ASRClient.swift` | 删除 |
| `Services/StreamingASRClient.swift` | 新增 |
| `Core/VoiceTyperController.swift` | 改写：流式状态机 |
| `UI/RecordingHUDController.swift` | 扩展：滚动预览文本 + 视觉优化 |
| `Core/AppConfig.swift` | 删除 `llmRecorrect` 之外不必要的字段（如有）；保留协议必要字段 |
| `Core/AppConstants.swift` | 删除 `minimumRecordingDuration`（流式无意义） |

### 5.2 `AudioCaptureService`

```swift
@MainActor
final class AudioCaptureService {
    var onChunk: ((Data) -> Void)?           // 每 600ms 一片，38400 bytes
    var onTailChunk: ((Data) -> Void)?       // 录音停止时不足 600ms 的尾巴

    private let chunkSamples = 9600
    private var ringBuffer = Data()          // float32 字节累积

    func start() throws { ... }              // 启动 AVAudioEngine，inputNode tap
    func stop() { ... }                      // 停止，把 ringBuffer 残留发到 onTailChunk
}
```

要点：
- AVAudioEngine 的 tap callback buffer 大小由系统决定（典型 ~470ms），自己用 `ringBuffer` 累积。
- 每凑齐 `chunkSamples * 4 = 38400` bytes 就 `onChunk` 发出，余量留在 buffer。
- `stop()` 调用时把残留 bytes 通过 `onTailChunk` 发一次（即使长度为 0 也调用一次，便于控制器统一处理）。
- 采样格式：16kHz 单声道 float32（与现有一致）；若硬件原生采样率不同，沿用现有重采样逻辑。

### 5.3 `StreamingASRClient`

```swift
@MainActor
final class StreamingASRClient {
    var onPartial: ((String) -> Void)?
    var onFinal: ((String, Double?, Double?) -> Void)?  // text, asrElapsed, llmElapsed
    var onError: ((String) -> Void)?
    var onClose: (() -> Void)?

    private var task: URLSessionWebSocketTask?

    func connect(server: ServerConfig, hotwords: [String], llmRecorrect: Bool) async throws {
        var components = URLComponents()
        components.scheme = "ws"
        components.host = server.host
        components.port = server.port
        components.path = "/recognize/stream"
        components.queryItems = [URLQueryItem(name:"llm_recorrect", value: llmRecorrect ? "true":"false")]

        var request = URLRequest(url: components.url!)
        if !server.apiKey.isEmpty {
            request.setValue("Bearer \(server.apiKey)", forHTTPHeaderField: "Authorization")
        }
        task = URLSession.shared.webSocketTask(with: request)
        task?.resume()

        let startMsg = ["type":"start", "hotwords": hotwords.joined(separator:" "),
                        "sample_rate": 16000] as [String: Any]
        try await sendJSON(startMsg)
        Task { await receiveLoop() }
    }

    func sendAudio(_ chunk: Data) {
        task?.send(.data(chunk)) { error in
            if let error = error { AppLog.network.error("send audio: \(error.localizedDescription)") }
        }
    }

    func finalize() async throws {
        try await sendJSON(["type":"finalize"])
    }

    func close() { task?.cancel(with: .normalClosure, reason: nil); task = nil }

    private func receiveLoop() async { /* receive 循环：解析 partial/final/error，dispatch 到主线程 */ }
}
```

要点：
- 用系统原生 `URLSessionWebSocketTask`，无外部依赖。
- 音频帧无需 ACK，发完即走；错误日志即可。
- final 收到后由控制器主动 `close()`。

### 5.4 `VoiceTyperController` 状态机

```
.idle
  └── hotkey press
        ├── try asrClient.connect(...)                  ── 失败 ─▶ .error("无法连接服务")
        ├── audioCapture.onChunk = asrClient.sendAudio
        ├── audioCapture.onTailChunk = { tail in
        │       asrClient.sendAudio(tail)
        │       Task { try? await asrClient.finalize() }
        │   }
        ├── asrClient.onPartial = { hud.appendPreview($0) }
        ├── asrClient.onFinal   = { text, _, _ in
        │       textInsertion.insert(text)
        │       hud.clearPreview()
        │       onStateChange(.idle)
        │       asrClient.close()
        │   }
        ├── asrClient.onError   = { onStateChange(.error($0)) }
        ├── audioCapture.start()
        └── state = .recording

.recording
  └── hotkey release
        ├── audioCapture.stop()    // 触发 onTailChunk → sendAudio(tail) → finalize()
        └── state = .recognizing

.recognizing
  └── onFinal 触发上述清理逻辑
```

要点：
- 录音过程中如果 WS 错误，立刻停止录音、HUD 显示错误。
- `.recognizing` 阶段 HUD 保留 partial 累计文本可见，便于用户知道在等什么；final 一到立即清空 + 上屏。
- 删除现有的 `minimumRecordingDuration` 短录音判断（流式下无意义）。

### 5.5 HUD 视觉

需求：
- 单行右对齐展示累计 partial 文本，超长从左侧自然溢出（裁切）。
- 字号小于状态文字，颜色淡一档（如 `secondaryLabelColor`）。
- final 到达 → 立即清空文本，状态行回到 `.idle` 样式。

视觉优化（在现有半透明 NSPanel 基础上微调）：
- 圆角加大到 14pt（现有为 10pt 左右）。
- 背景透明度 0.78，加 1pt 高光描边 `NSColor.white.withAlphaComponent(0.08)`。
- 状态文字与预览文字垂直双行布局：上行状态（`录音中...`），下行预览（`今天天气不错`）。
- 录音中状态前加一个呼吸动画的红点（pulse opacity 0.5↔1.0，周期 1.2s）。
- panel 宽度从 240 提到 320，容纳更多预览文字。

`RecordingHUDController` 新增 API：

```swift
func showPreview(_ accumulated: String)
func clearPreview()
```

`accumulated` 由控制器累计后整体下发；HUD 不负责拼接逻辑。

---

## 6. 配置与文档

### 6.1 客户端配置

- `AppConfig.server`：字段保持不变（host/port/apiKey/timeout/llmRecorrect）。
- `timeout` 含义变更：WS 握手超时（5s 内未完成 open 视为失败）。

### 6.2 服务端 CLI

- `--model` 默认值改为 `paraformer-zh-streaming`。
- 新增 `--chunk-size`（默认 `0,10,5`），仅文档暴露，普通用户不需要动。

### 6.3 README 变更

- 客户端 README：说明流式预览行为、HUD 预览文本仅为参考、最终以上屏文本为准。
- 服务端 README：标注模型变更、移除 `/recognize` HTTP 接口、新增 `/recognize/stream` WS 接口、当前并发为单客户端假设、热词在流式中可能不生效。

---

## 7. 测试与验收

### 7.1 单元 / 模块测试

- `AudioCaptureService`：模拟系统 buffer 输入，验证恰好按 9600 samples 切片，残余通过 tail 输出。
- `StreamingASRClient`：模拟 WS server，验证消息序列化/反序列化、收到 final 后回调一次。
- 服务端 `Session.feed` / `Session.finalize`：用本地短 wav 切片喂入，对比累计文本与离线模型差异 < 2% CER。

### 7.2 端到端验收

| 场景 | 期望 |
|------|------|
| 短句 "你好" | HUD 在 1s 内出现首个 partial，松开后 1s 内上屏 |
| 长句 30s 连续说话 | partial 持续滚动，无明显卡顿 |
| 录音中网络断开 | HUD 显示错误，不写入用户输入框 |
| 服务未就绪 | 连接被 4503 关闭，HUD 显示"服务未就绪" |
| 连续快速按住-松开-按住 | 两次会话独立，不串扰 |
| LLM 校对开启 | final 到达延迟增加 0.5–2s，文本带标点且经校对 |
| 50 句日常听写 | CER 退化 < 2 个百分点（对比合并前主干） |

### 7.3 合并主干前的硬性条件

- 上述端到端场景全部通过。
- 连续 30 分钟正常使用无 crash、无错位上屏。
- 松开热键到上屏 ≤ 主干同等条件下的延迟。

---

## 8. 里程碑

| # | 任务 | 估时 | 输出 |
|---|------|------|------|
| M0 | 服务端 spike：跑通 `paraformer-zh-streaming` chunked 推理 | 0.5d | 验证脚本 + 日志 |
| M1 | `StreamingSpeechRecognizer` + WS handler，wscat 联调通过 | 1.5d | 服务端可独立运行 |
| M2 | `AudioCaptureService` 切片改造 + 单测 | 0.5d | chunk 严格 9600 samples |
| M3 | `StreamingASRClient` + 控制器状态机改写 | 1d | 客户端可发音频收 partial |
| M4 | HUD 视觉优化 + 预览文本展示 | 0.5d | HUD 滚动文本可用 |
| M5 | LLM 校对接回、final 上屏链路打通 | 0.5d | 端到端走通 |
| M6 | 异常路径回归 + 50 句准确率基线 | 1d | 验收报告 |

合计约 5–6 天。

---

## 9. 待决事项

- WS 握手超时具体值（暂定 5s）。
- HUD 预览文本最大长度策略：是否设上限（如 200 字符）防止内存累积。
- 服务端 LLM 校对失败时是否回退原始 ASR 文本（建议：回退，记录 warning）。

---

## 10. 不在本期范围

- 多客户端并发优化。
- IMK 输入法形式的内联预览。
- 流式 LLM 校对（按 chunk 边送边改）。
- 流式热词支持。
- Windows / Go 客户端的流式改造。
