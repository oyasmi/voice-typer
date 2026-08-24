# VoiceTyper Server

[← 返回分体式项目](../README.md) · [VoiceTyper 主项目](../../README.md)

`voice-typer-server` 是 VoiceTyper 的语音识别服务端：接收客户端上传的音频，跑 ASR 推理，把文本返回给客户端。客户端只负责录音和上屏，所有模型都跑在这里。

当前版本 **1.5.1**，协议版本 **2**（见 [`PROTOCOL.md`](../PROTOCOL.md)）。

**本文适合**：要部署、调参、排障或二次开发服务端的人。只想在 macOS 或 Windows 上使用 VoiceTyper，参见[主项目 README](../../README.md)。

---

## 目录

- [设计要点](#设计要点)
- [环境要求](#环境要求)
- [安装与启动](#安装与启动)
- [两种运行模式](#两种运行模式)
- [命令行参数完整清单](#命令行参数完整清单)
- [模型](#模型)
- [接口](#接口)
- [鉴权](#鉴权)
- [LLM 纠错](#llm-纠错)
- [并发与资源模型](#并发与资源模型)
- [部署形态](#部署形态)
- [性能调优](#性能调优)
- [故障排查](#故障排查)
- [开发者说明](#开发者说明)

---

## 设计要点

- **纯本地推理**：`onnxruntime` + `funasr-onnx`，模型文件落在本机磁盘，音频不出机器。唯一可能的外部调用是可选的 LLM 纠错，且只发送识别出的文本。
- **单模型双通道**：默认用一个 SenseVoice-Small 实例同时产出流式预览（`partial`）和最终文本（`final`）。预览是对已累积音频的整段重跑，所以会自我修正，松手时措辞不跳变。
- **标点与 ITN 内置**：SenseVoice 直接输出带标点的文本并做逆文本规整（「六十四兆」→「64兆」），不需要外挂 `ct-punc`。
- **单用户设计**：推理串行执行，一台服务端同一时刻只服务一个正在说话的人（见[并发与资源模型](#并发与资源模型)）。
- **模式二选一**：启动时决定跑流式还是非流式，两种模式注册**不同的路由**，不会同时提供。

---

## 环境要求

| 项 | 要求 |
| --- | --- |
| Python | 最低 **3.10**，推荐 3.12+ |
| 操作系统 | macOS / Linux / Windows |
| 设备 | CPU（默认）或 NVIDIA CUDA |
| 磁盘 | 默认模型约 240MB，首次运行自动下载 |
| 内存 | 进程常驻约 1GB 起步（含运行时开销；模型权重本身约 240MB，见[模型加载矩阵](#模型加载矩阵)） |

运行时依赖（`pip` 自动装）：`funasr-onnx`、`modelscope`、`onnxruntime>=1.24`、`tornado`、`numpy<2`。

> Windows 服务功能额外需要 `pywin32`：`pip install voice-typer-server[windows-service]`。

---

## 安装与启动

### 方式一：helper 脚本（macOS / Linux 推荐）

```bash
cd client-server/server
./scripts/voice_typer_server.sh setup     # 建 ~/.venvs/voice-typer 并安装
./scripts/voice_typer_server.sh run       # 启动
```

`setup` 会校验 Python ≥ 3.10、创建虚拟环境 `~/.venvs/voice-typer`、升级 pip/setuptools/wheel，再从 PyPI 安装 `voice-typer-server`。

`run` 以这组固定默认值启动，后面追加的参数会拼到命令行末尾（同名参数后写的生效）：

```
--host 127.0.0.1 --port 6008 --device cpu
```

```bash
# 覆盖示例
./scripts/voice_typer_server.sh run --host 0.0.0.0 --onnx-threads 2
```

从本地源码装（开发用）：

```bash
./scripts/voice_typer_server.sh setup --local          # 默认取 server/ 目录
./scripts/voice_typer_server.sh setup --local /path/to/server
```

### 方式二：直接用 Python 包

```bash
pip install voice-typer-server

voice-typer-server --host 127.0.0.1 --port 6008
python -m voice_typer_server --host 127.0.0.1 --port 6008   # 等价
voice-typer-server --help
voice-typer-server --version
```

### 方式三：Docker

镜像分两阶段构建：第一阶段预下载模型（只依赖 `MODEL_REPO`，改源码不会触发重下），第二阶段装包。所以镜像开箱即用，不会在首次启动时才去拉模型。

```bash
docker build -t voice-typer-server:latest .
docker run -d -p 6008:6008 --name voice-typer voice-typer-server:latest
```

容器通过环境变量传参，未设置的变量不会出现在命令行上：

| 环境变量 | 默认 | 对应参数 |
| --- | --- | --- |
| `HOST` | `0.0.0.0` | `--host` |
| `PORT` | `6008` | `--port` |
| `DEVICE` | `cpu` | `--device` |
| `ONNX_THREADS` | `4` | `--onnx-threads` |
| `MODEL` / `OFFLINE_MODEL` / `PUNC_MODEL` | 不设置 | 同名参数 |
| `API_KEYS` | 不设置 | `--api-keys` |
| `LLM_BASE_URL` / `LLM_API_KEY` / `LLM_MODEL` / `LLM_TEMPERATURE` / `LLM_MAX_TOKENS` | 不设置 | 同名参数 |

```bash
docker run -d -p 6008:6008 \
  -e API_KEYS=your_key \
  -e LLM_BASE_URL=https://api.openai.com/v1 \
  -e LLM_API_KEY=sk-xxx \
  --name voice-typer voice-typer-server:latest
```

镜像自带 `HEALTHCHECK`（每 30s 调 `scripts/healthcheck.py`），`docker ps` 的 health 状态即服务就绪状态。

> 容器默认 `HOST=0.0.0.0`。端口一旦映射出去就等于对外开放，务必同时设置 `API_KEYS`。

### 方式四：Windows 服务

把服务端注册成 Windows 服务，实现开机自启与后台常驻。

```bat
REM 1. 安装环境（--local 从当前源码装，并带上 pywin32）
scripts\voice_typer_server.bat setup --local

REM 2. 注册服务（需管理员权限，默认开机自启）
scripts\voice_typer_server.bat install -- --host 127.0.0.1 --port 6008 --device cpu

REM 带 LLM 纠错
scripts\voice_typer_server.bat install -- --host 127.0.0.1 --port 6008 --device cpu ^
    --llm-base-url https://api.openai.com/v1 ^
    --llm-api-key sk-xxx ^
    --llm-model gpt-4o-mini

REM 改为手动启动
scripts\voice_typer_server.bat install --startup manual -- --host 127.0.0.1 --port 6008
```

管理：

```bat
scripts\voice_typer_server.bat start
scripts\voice_typer_server.bat stop
scripts\voice_typer_server.bat uninstall
```

也可以在 `services.msc` 里操作，服务名为 **VoiceTyper 语音识别服务**。等价的 CLI 子命令是 `voice-typer-server service {install|uninstall|start|stop}`，`--` 之后的内容原样作为服务运行参数。

**注意事项**

- 安装 / 卸载 / 启停都需要**管理员权限**。
- 服务默认以 `LocalSystem` 运行。模型缓存在你的用户目录下时，服务首次启动会以另一个身份重新下载一份。
- 修改运行参数必须先 `uninstall` 再 `install`，没有原地改参数的路径。
- 服务模式的日志写文件：`%USERPROFILE%\.voice-typer\server.log`，10MB 滚动，保留 3 份。前台运行时日志走 stdout。

---

## 两种运行模式

启动时二选一，**不能同时提供**——两种模式注册的路由不同：

| | 流式（默认） | 非流式（`--no-streaming`） |
| --- | --- | --- |
| 传输 | WebSocket 长连接 | 单次 HTTP POST |
| 注册的路由 | `/health`、`/recognize/stream` | `/health`、`/recognize` |
| 录音时反馈 | 有，`partial` 全量预览文本 | 无 |
| 上屏文本来源 | 松手后整段复识别的 `final` | HTTP 响应 |
| 支持的客户端 | macOS Swift、Windows 原生 | 全部客户端（Linux 只支持这个） |

**谁需要 `--no-streaming`**：Linux 客户端。它是纯 HTTP 实现，连不上 WebSocket 端点。

```bash
voice-typer-server                 # 流式，给 macOS / Windows 客户端
voice-typer-server --no-streaming  # 非流式，给 Linux 客户端
```

> 流式模式下**没有** `POST /recognize` 这个路由，请求它会 404。想用 `curl` 测识别，得先用 `--no-streaming` 启动。

---

## 命令行参数完整清单

很多参数只在特定模式 / 特定模型下生效。被忽略的参数**不会静默丢弃**：启动日志里会打一条 `<参数> 已忽略：<原因>` 的 warning。

### 网络

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--host` | `127.0.0.1` | 监听地址。设为 `0.0.0.0` 且未配 `--api-keys` 时会打显眼的安全警告 |
| `--port` | `6008` | 监听端口 |

### 模式

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--streaming` | 开启 | 流式（WebSocket）。默认值，通常不必显式写 |
| `--no-streaming` | — | 非流式（HTTP）。与 `--streaming` 互斥 |

### 模型

| 参数 | 默认 | 生效条件 |
| --- | --- | --- |
| `--offline-model` | `sensevoice-small` | **仅流式**。产出最终文本的模型。非流式下写了会被忽略 |
| `--model` | 见右 | 流式下是**预览模型**（默认 `paraformer-zh-streaming`）；非流式下是**唯一模型**（默认 `sensevoice-small`）。SenseVoice 单模型流式下会被忽略 |
| `--punc-model` | `ct-punc` | **仅 paraformer**。SenseVoice 自带标点，会忽略此参数。`none` 表示禁用 |
| `--sensevoice-language` | `auto` | **仅 sensevoice-\* 模型**。可选 `auto`/`zh`/`en`/`yue`/`ja`/`ko` |
| `--chunk-size` | `0,10,5` | **仅 paraformer 双模型流式**。格式 `left,current,right`，单位 60ms 帧 |

默认配置（SenseVoice 单模型流式）下，`--model`、`--chunk-size`、`--punc-model` **全都不生效**。要让它们生效，得先用 `--offline-model paraformer-zh` 切回双模型。

### 性能

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--device` | `cpu` | `cpu` / `cuda` / `cuda:N` |
| `--onnx-threads` | `4` | ONNX Runtime intra-op 线程数 |

### 鉴权

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--api-keys` | 无 | 逗号分隔的多个 key。监听 `127.0.0.1` 时不会强制校验，见[鉴权](#鉴权) |

### LLM 纠错

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--llm-base-url` | 无 | OpenAI 兼容的 API 基址。与 `--llm-api-key` **同时**提供才会启用 |
| `--llm-api-key` | 无 | API 密钥 |
| `--llm-model` | `gpt-4o-mini` | 模型名 |
| `--llm-temperature` | `0.0` | 温度。纠错任务不需要发散 |
| `--llm-max-tokens` | `600` | 实际生效值取本值与「输入长度×2+128」的**较大者**，防止长听写被截断。因此调小它不会降低短句场景的实际上限 |
| `--llm-timeout` | `5.0` | 请求超时（秒）。直接决定松手到上屏的最长额外等待 |

### 其他

`--version` 打印版本后退出。`service` 子命令见 [Windows 服务](#方式四windows-服务)。

### 示例

```bash
# 本机、默认配置
voice-typer-server

# 局域网 + 鉴权
voice-typer-server --host 0.0.0.0 --api-keys akey,bkey

# 给 Linux 客户端
voice-typer-server --no-streaming

# GPU + 更高线程数
voice-typer-server --device cuda --onnx-threads 8

# 切回 paraformer 双模型流式（此时 --model / --chunk-size / --punc-model 才生效）
voice-typer-server --offline-model paraformer-zh --model paraformer-zh-streaming --chunk-size 0,10,5
```

---

## 模型

短名会映射到 ModelScope 仓库，首次使用自动下载并缓存到 `~/.cache/modelscope`。如果模型目录里只有 `model_quant.onnx`，服务端会自动使用量化版本。

### 可选的最终识别模型

| 短名 | 仓库 | 体积 | 说明 |
| --- | --- | --- | --- |
| `sensevoice-small`（默认） | `iic/SenseVoiceSmall-onnx` | 240MB | 官方 int8 导出。自带标点与 ITN，覆盖中 / 英 / 粤 / 日 / 韩 |
| `sensevoice-small-fp32` | `manyeyes/sensevoice-small-onnx` | 893MB | 社区 fp32 导出。实测质量与 int8 持平，速度慢约 1.6 倍 |
| `paraformer-zh` | `damo/speech_paraformer-large...onnx` | 227MB + ct-punc 1.0GB | 旧默认值。需外挂 `ct-punc` 才有标点，数字保持「六十四兆」这样的口语形式 |

流式预览模型（仅双模型模式用）：`paraformer-zh-streaming`。标点模型：`ct-punc`。

```bash
voice-typer-server --offline-model paraformer-zh          # 流式
voice-typer-server --no-streaming --model paraformer-zh   # 非流式
```

### 模型加载矩阵

| 配置 | 加载的模型 | 模型权重占用 |
| --- | --- | --- |
| 流式 + SenseVoice（默认） | 1 个：`sensevoice-small` | ~240MB |
| 非流式 + SenseVoice | 1 个：`sensevoice-small` | ~240MB |
| 流式 + paraformer | 3 个：`paraformer-zh-streaming` + `paraformer-zh` + `ct-punc` | ~1.5GB |
| 非流式 + paraformer | 2 个：`paraformer-zh` + `ct-punc` | ~1.2GB |

### 为什么默认不用独立的流式模型

SenseVoice 的 RTF 约 0.01，重跑 15 秒音频约 165ms，远小于客户端 600ms 的送帧周期。所以预览直接复用同一个模型对已累积音频整段重跑，比再加载一个流式模型划算：

- 少一个模型（约 227MB）和一整套流式 cache 逻辑；
- 预览会**自我修正**（`识别功能和并` → `识别功能合并`）并自带标点；
- 预览和最终文本出自同一个模型，松手瞬间措辞不跳变。

代价是 `partial` 必须是**全量文本**而非增量——客户端收到后直接替换，见 [`PROTOCOL.md`](../PROTOCOL.md) §4.3。

> 长录音下预览不会越来越慢：1.5.1 起 SenseVoice 预览改为滑动窗口重跑，预览与 finalize 延迟不随录音时长增长。

### 自测选型

```bash
# 内置 TTS 语料（macOS）
python scripts/bench_asr.py

# 自己的录音：16k 单声道 wav，同名 .txt 存参考文本才会算 CER
python scripts/bench_asr.py --audio-dir ~/my_recordings
```

强烈建议用**你自己的录音**测。TTS 语料太干净，测不出真实口音和噪声下的差距。

---

## 接口

完整的帧定义、字段语义和版本变更以 [`PROTOCOL.md`](../PROTOCOL.md) 为准，本节是实现侧的补充。

### `GET /health`

两种模式下都注册。

```json
{
  "status": "ok",
  "ready": true,
  "version": "1.5.1",
  "protocol_version": 2,
  "streaming": true,
  "llm_enabled": false,
  "asr_model": "sensevoice-small",
  "offline_model": "sensevoice-small",
  "punc_model": null,
  "device": "cpu"
}
```

| 字段 | 含义 |
| --- | --- |
| `ready` | 模型是否加载完成。为 `false` 时识别请求会返回 503 / WS 直接被关闭（code 4503） |
| `streaming` | 当前是流式还是非流式模式。客户端据此判断自己的配置是否匹配 |
| `llm_enabled` | 是否配好了 LLM。为 `false` 时客户端传 `llm_recorrect=true` 也不会有纠错 |
| `asr_model` | 流式下是预览模型，非流式下是唯一模型 |
| `offline_model` | 流式下产出最终文本的模型；非流式下为 `null` |
| `punc_model` | `null` 表示不需要独立标点模型（SenseVoice 自带） |

默认单模型流式下 `asr_model` 与 `offline_model` 相同，这是预览与终稿同源的标志。

### `WS /recognize/stream`（流式模式）

时序：

```
客户端                                          服务端
  │  ── connect ?llm_recorrect=true|false ──▶     │
  │  ── {"type":"start","sample_rate":16000} ─▶   │
  │                                               │
  │  ── binary float32 PCM (~600ms/帧) ────────▶  │  累积音频
  │  ◀────── {"type":"partial","text":..,"seq":N} │  整段重跑（后台任务）
  │  ── binary ───────────────────────────────▶   │
  │  ◀────── {"type":"partial", ...}              │
  │                                               │
  │  ── {"type":"finalize"} ───────────────────▶  │  停预览 → 整段复识别 →（可选）LLM
  │  ◀────── {"type":"final","text":..,"asrElapsed":0.82}
  │  ◀────── close(1000)                          │
```

实现上的几个要点：

- **预览不阻塞收音**。音频帧只做累积，推理丢到后台任务。已有预览在跑时新的预览请求直接跳过，积压音频留到下一次一并处理——慢机器上预览自然变稀疏，而不会让推理队列无限堆积。
- **文本没变化就不发 `partial`**，客户端不必去重。`seq` 单调递增，仅用于诊断。
- **finalize 会取消在途预览**，`final` 之后不会再冒出过期的 `partial`。
- **浏览器连不上**。`check_origin` 恒返回 `False`：原生客户端建立 WS 时不发 `Origin` 头，带 `Origin` 的连接基本来自网页，一律拒绝，防止任意页面连上本机端口白嫖识别算力。
- **心跳**：`websocket_ping_interval=10`。客户端崩溃或网络半开时，一个 ping 周期内收不到 pong 就回收连接及其缓冲音频。
- **单帧上限** 1MB（客户端每帧 38400 字节，冗余充足）。

会话上限与非致命提示：

| code | 类型 | 触发 | 服务端行为 |
| --- | --- | --- | --- |
| `no_session` | warning | 没发 `start` 就发音频 | 丢弃该帧，连接保留 |
| `bad_frame` | warning | 二进制帧长度不是 4 的倍数 | 丢弃该帧，已累积音频不受影响 |
| `feed_failed` | warning | 单次预览推理抛异常 | 保留连接，finalize 仍能对累积音频兜底 |
| `session_capped` | warning | 单会话累计音频达 **300 秒** | 停止录入新音频，已累积部分仍可正常 finalize |
| `bad_request` | error | JSON 解析失败，或 `start` 里 `sample_rate ≠ 16000` | 关闭连接 |
| `bad_state` | error | 未 `start` 就 `finalize` | 关闭连接 |
| `internal` | error | 未预期异常 | 关闭连接 |

### `POST /recognize`（非流式模式）

推荐用裸 PCM：

```bash
curl -X POST "http://127.0.0.1:6008/recognize?llm_recorrect=false" \
     -H "Content-Type: application/octet-stream" \
     --data-binary @test.float32
```

- Body：16kHz / float32 / mono 原始 PCM。
- Query：`llm_recorrect=true|false`（默认 `false`）。

也兼容旧版 `multipart/form-data`（字段名 `audio`）：

```bash
curl -X POST http://127.0.0.1:6008/recognize \
     -H "Authorization: Bearer your-api-key" \
     -F "audio=@test.wav"
```

响应：

```json
{ "text": "你好世界。", "duration": 1.23, "elapsed": 0.42, "llmElapsed": 0.31 }
```

`llmElapsed` 只在实际调用了 LLM 时出现。

错误码：

| 状态码 | 场景 |
| --- | --- |
| 400 | 缺 `audio` 字段 / 音频不是合法 float32 / 音频为空 |
| 401 | 鉴权失败 |
| 413 | 音频超过 64MB |
| 503 | 模型尚未加载完成 |

---

## 鉴权

HTTP 与 WebSocket 走**同一套**规则（`auth.py:authorize_request`）：

| `--api-keys` | `--host` | 结果 |
| --- | --- | --- |
| 未配置 | 任意 | 全部放行 |
| 已配置 | `127.0.0.1` | 放行，**不校验** Bearer |
| 已配置 | 其他（含 `0.0.0.0`） | 必须带 `Authorization: Bearer <key>`，否则 401 |

判断依据是**服务端的监听地址**，不是请求来源 IP。逻辑是：只监听 loopback 时外部本来就连不进来，本机进程视为受信，客户端不必配 key。

Key 比对用 `hmac.compare_digest`（常数时间），多个 key 任一匹配即通过。鉴权失败会记一条含来源 IP 的 warning。

> 监听非 loopback 地址却没配 `--api-keys` 时，启动日志会打一段醒目的警告：任何能访问该端口的人都能免费用你的识别算力。

---

## LLM 纠错

在 ASR 之后加一道文本纠正，处理同音字、口语词、标点等问题。**只发送识别出的文本，不发送音频。**

启用条件：`--llm-base-url` 和 `--llm-api-key` **同时**提供。少一个就是未启用，`/health` 的 `llm_enabled` 会是 `false`。

```bash
voice-typer-server \
  --llm-base-url https://api.openai.com/v1 \
  --llm-api-key sk-xxx \
  --llm-model gpt-4o-mini
```

客户端侧还要打开 `llm_recorrect`（配置文件或设置界面），服务端才会对该请求走纠错。两端都开才生效。

实现要点：

- 提示词在 `voice_typer_server/prompts/correction.md`，随包分发，可以改。除 system prompt 外还带了 few-shot 示例——对小模型而言，例子比文字禁令更能约束输出格式。
- **纠错失败不影响上屏**：超时或报错都会记 warning 并回退到原始 ASR 文本。
- 纠错耗时算在松手到上屏的等待里，上限由 `--llm-timeout`（默认 5s）决定。想要低延迟就把它调小。
- 想完全本地？把 `--llm-base-url` 指向本机的 Ollama / vLLM 等 OpenAI 兼容端点即可，此时依然是全程离线。

---

## 并发与资源模型

**服务端按单用户设计。**

ONNX 推理和 funasr 的 fbank 前端都带可变状态，必须串行执行，因此推理跑在 `max_workers=1` 的线程池里：

| 配置 | executor | 效果 |
| --- | --- | --- |
| 流式 + SenseVoice（默认） | 预览与 finalize **共用同一个** worker | 全局串行：任何一次预览或复识别都会让其他会话排队 |
| 流式 + paraformer | 预览与 finalize 各占一个 worker | 预览不会排在别的会话漫长的离线 finalize 后面 |
| 非流式 | 单个 worker | 请求逐个处理 |

所以「局域网远程使用」应理解为**换个位置访问自己的那台服务端**，而不是一台服务端供多人同时使用。多人同时说话时，彼此的预览和 finalize 会互相排队，表现为预览卡顿、松手后等待变长。

其他资源限制：

- HTTP 请求体上限 64MB（约 17 分钟音频），超出返回 413。
- 单个 WS 会话音频上限 300 秒，超出后停止录入但仍可 finalize。
- WS 单帧上限 1MB。

---

## 部署形态

| 场景 | 命令 | 要点 |
| --- | --- | --- |
| 只在本机用（默认） | `voice-typer-server` | 不需要 key，客户端连 `127.0.0.1:6008` |
| 局域网内自己的另一台机器 | `voice-typer-server --host 0.0.0.0 --api-keys your_key` | 客户端填服务端 IP、端口和同一个 key |
| 公网暴露 | 不建议直连 | 前面放 nginx / Caddy 做 TLS，客户端把 `scheme` 设为 `https`（仅 macOS 客户端支持） |
| 服务器有 GPU | `--device cuda` | 见[性能调优](#性能调优) |

客户端侧需要填的就三项：`host`、`port`、`api_key`。

> HTTPS / WSS 目前只有 macOS 客户端支持（`server.scheme` 配置项）。Windows 与 Linux 客户端硬编码明文 `http` / `ws`。

---

## 性能调优

### GPU 加速

```bash
voice-typer-server --device cuda      # 默认 0 号卡
voice-typer-server --device cuda:1    # 指定卡
```

需要自行装好匹配的 `onnxruntime-gpu` 与 CUDA 运行时。

### CPU 线程数

```bash
voice-typer-server --onnx-threads 8
```

`--onnx-threads` 是 ONNX Runtime 的 intra-op 线程数，默认 4。核多的机器可以调高；和别的服务抢 CPU 时调低。注意它不提高并发能力——推理本身仍是串行的（见[并发与资源模型](#并发与资源模型)）。

### 内存

默认单模型配置下流式与非流式内存占用相当（约 240MB 模型 + 运行时开销）。显式切到 `--offline-model paraformer-zh` 才会额外加载流式预览模型和 `ct-punc`（约 1.2GB）。内存紧张时：

```bash
# 首选：回到 SenseVoice 单模型
voice-typer-server

# 或者：用 paraformer 但砍掉标点模型（省约 1.0GB，代价是没有标点）
voice-typer-server --offline-model paraformer-zh --punc-model none
```

### 延迟构成

松手到上屏的时间 =「整段复识别」+「LLM 纠错（若启用）」+ 网络往返。前者可以在日志里看 `离线复识别耗时`，后者由 `--llm-timeout` 封顶。嫌慢先关 LLM 纠错。

---

## 故障排查

### 服务起来了，客户端连不上

1. 看启动日志最后一行打印的实际监听地址。
2. 核对客户端配置的 `host` / `port`。
3. 本机部署优先用 `127.0.0.1:6008`，别写 `localhost`（可能解析到 IPv6）。
4. 确认客户端的流式开关和服务端模式一致——服务端跑流式时 `POST /recognize` 是 404，跑非流式时 WS 端点不存在。

### 返回 401

配了 `--api-keys` 且监听地址不是 `127.0.0.1` 时才会校验。检查客户端是否填了 key，以及是否与服务端某一个 key 完全一致。

### 首次启动很慢

在下载模型（约 240MB）。日志会停在「初始化模型...」，下完会打印 `初始化完成，耗时 Ns`。用 Docker 镜像可以跳过这一步（模型已烘进镜像）。

### 参数好像没生效

看启动日志有没有 `<参数> 已忽略：<原因>`。默认的 SenseVoice 单模型流式会忽略 `--model`、`--chunk-size`、`--punc-model`，这是预期行为，不是 bug。

### 识别结果没有标点

用了 `--offline-model paraformer-zh` 且 `--punc-model none`。要么去掉 `--punc-model none`，要么换回 SenseVoice（自带标点）。

### 浏览器 / 网页连不上 WebSocket

设计如此。`check_origin` 拒绝一切带 `Origin` 头的连接。要加 Web 客户端得改 `app.py` 里的 `check_origin` 放行同源。

### Apple Silicon 上没有 MPS

当前只支持 `cpu` / `cuda` / `cuda:N`。Apple Silicon 上用 `cpu` 即可——SenseVoice 的 RTF 约 0.01，M 系 CPU 上完全够用。

### 想看更详细的日志

日志默认 INFO 级走 stdout（Windows 服务模式写文件）。预览文本、复识别文本等在 DEBUG 级，目前需要改 `configure_logging()` 的 level 才能看到。

---

## 开发者说明

### 代码结构

| 文件 | 职责 |
| --- | --- |
| [`cli.py`](voice_typer_server/cli.py) | 参数解析、`service` 子命令分发 |
| [`app.py`](voice_typer_server/app.py) | Tornado 应用：`resolve_runtime_plan` 决策 + 三个 handler + 启动装配 |
| [`recognizer.py`](voice_typer_server/recognizer.py) | `SenseVoiceRecognizer` / `SpeechRecognizer` / `StreamingSpeechRecognizer` 三个 ONNX 封装 |
| [`llm_client.py`](voice_typer_server/llm_client.py) | OpenAI 兼容客户端，提示词来自 `prompts/correction.md` |
| [`auth.py`](voice_typer_server/auth.py) | `authorize_request` + HTTP 鉴权基类 |
| [`win_service.py`](voice_typer_server/win_service.py) | Windows 服务包装（仅 Windows，需 pywin32） |

`resolve_runtime_plan()` 是纯函数：只输出「哪个参数在哪种模式下生效」的决策，不加载模型也不打日志，因此可以脱离 ONNX / funasr 单独跑测试。改参数生效规则时优先改它。

### 脚本

| 脚本 | 用途 |
| --- | --- |
| `scripts/voice_typer_server.sh` | macOS / Linux 的 setup + run |
| `scripts/voice_typer_server.bat` | Windows 的 setup + 服务管理 |
| `scripts/bench_asr.py` | 模型对比基准，支持自带录音算 CER |
| `scripts/healthcheck.py` | Docker HEALTHCHECK 用 |
| `scripts/spike_streaming.py` | 流式链路的实验脚本 |

### 测试与发布

```bash
pip install -e ".[dev]"
pytest
```

发布流程见 [RELEASING.md](./RELEASING.md)，版本历史见 [CHANGELOG.md](./CHANGELOG.md)。版本号单一来源是 `voice_typer_server/__init__.py` 的 `__version__`。

改动线上行为（帧格式、字段、错误码）时，必须同步更新 [`PROTOCOL.md`](../PROTOCOL.md) —— 它是客户端与服务端的唯一契约来源。
