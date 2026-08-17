# Changelog

## 未发布

- 代码组织与部署侧的一批清理：容器镜像改为构建期烘入 SenseVoice 模型（首启不
  再现下 241MB），移除未使用的 `ffmpeg` / `libsndfile1` / `jieba` 依赖，新增
  `.dockerignore`（此前 `COPY . .` 会把本地 `.venv-release/`、`dist/`、
  `nohup.out` 一并打进镜像层）与容器 `HEALTHCHECK`。
- 新增免模型的测试骨架（`server/tests/`）：鉴权矩阵、后处理正则、运行时参数
  决策（`resolve_runtime_plan`）、WebSocket 协议状态机。
- `create_server` 里「哪个参数在哪种模式下生效」的判断抽成纯函数
  `resolve_runtime_plan`；顺带补全此前 `--offline-model`（非流式下）和
  `--sensevoice-language`（配 paraformer 时）被静默忽略却无提示的两处。
- 标点模型加载逻辑（两个 recognizer 间重复的 13 行）抽成 `_load_punc_model`；
  `Session` 更名为 `ParaformerStreamingSession`，与同文件的
  `SenseVoiceSession` 对称。
- 监听非 loopback 地址且未配置 `--api-keys` 时的警告更醒目（容器默认场景），
  不再淹没在启动横幅里。

## 1.5.1

服务端性能与稳定性修复，覆盖内部审查发现的六类问题。无破坏性变更，`protocol_version` 仍为 `2`。

### 预览改为滑动窗口，长录音不再拖慢自己

SenseVoice 预览此前对**全部**已累积音频整段重跑，单会话预览 CPU 随录音时长呈平方增长
（60s 录音约 30s 累计 CPU），且送帧间隔越拖越长。现在 `SenseVoiceSession` 维护一条
15 秒的滑动窗口：窗口左侧的音频只识别一次并固化成文本，预览只重跑窗口内的尾巴，
窗口滚动时在音频能量最低处下刀以减少接缝瑕疵。

- 预览成本从 O(录音时长²) 降到 O(录音时长 × 窗口)，且与录音总时长无关。
- finalize 排队等待的在途预览规模同步从"整段"降到"半个窗口"（约 100ms 量级）。
- `finalize()` 不再复用 preview 的拼接结果，永远对完整音频重新整段识别，
  上屏文本不受窗口化影响。
- 顺带修掉一个陈旧假设：原以为客户端 finalize 前不补发尾块、缓存能让 finalize
  常态命中；实测客户端总是先发尾块再 finalize，缓存实际上几乎不命中，因此这次
  一并移除。

### WebSocket 会话容量与健壮性

- 新增服务端侧 300 秒会话时长上限，超限后停止收音但保留会话，`finalize` 仍可用
  已累积部分产出结果（见 `PROTOCOL.md` §5.3，新 warning code `session_capped`）。
- 新增 `websocket_ping_interval`（10s）：客户端崩溃或网络半开时，一个 ping 周期
  内收不到 pong 就回收连接与其缓冲音频，不再需要等 TCP 超时。
- 畸形二进制帧（长度不是 4 的倍数）不再让整段录音判死刑，改为丢弃该帧并发
  `warning`（新 code `bad_frame`），连接与已累积音频不受影响。
- `start` 帧的 `sample_rate` 非 16000 时明确拒绝，不再静默按 16kHz 处理产出乱码。
- 修复 `open()` 提前返回（recognizer 未就绪）时 `on_close()` 访问未初始化实例属性
  导致的 AttributeError（相关状态改为类属性兜底）。
- 预览帧发送不再是裸调用：客户端中途断开时不再产生未捕获的 Task 异常。

### 其他

- WS 路径的 LLM 修正日志此前遗漏降级，与 HTTP 路径不一致，已补齐（两者均为
  INFO，打印修正前后文本，便于直接从 stdout 核对修正效果）。
- 极短音频（< 400 采样点 / 25ms）不再触发 funasr 前端的 `IndexError`，直接返回
  空字符串。
- 新增 `--llm-timeout`（默认 5s，原硬编码 8s），控制 LLM 纠错的最长等待。
- API Key 校验改为常量时间比较（`hmac.compare_digest`），避免理论上的计时攻击。
- Ctrl+C 处理迁移到 `asyncio`（POSIX 用 `add_signal_handler`，Windows 回退
  `signal.signal` + `call_soon_threadsafe`），加载模型阶段的 Ctrl+C 也不再抛出
  裸 traceback；`ServerContext.shutdown()` 补上此前遗漏的 `server.stop()`。

## 1.5.0

### 单模型流式：SenseVoice 自己产出预览，去掉 paraformer-zh-streaming

流式模式下不再加载独立的流式模型。预览改为**对已累积音频整段重跑 SenseVoice**——RTF 约 0.01，重跑 15s 音频只要约 165ms，远在 600ms 的送帧周期内。

- **少一个模型**：不再下载/加载 `paraformer-zh-streaming`（约 227MB），流式模式的模型总占用从约 468MB 降到 241MB。
- **预览质量大幅提升，且会自我修正**。同一段音频，旧流式模型给出 `把一百和五十的两个限制都给成六十十四兆赫兆`；现在预览逐步收敛并回溯改正错字（`识别功能和并` → `识别功能合并`，`初自安装` → `初次安装`），还带标点。
- **预览与终稿不再跳变**：两者出自同一个模型，松手瞬间文字不会整段变样。
- **finalize 常见情况下是零成本**：若松手前最后一次预览已覆盖全部音频，finalize 直接复用其结果（实测 166ms → 0.1ms）。
- 少了一整套流式 cache 状态机（`Session` 的 `_cache` / 增量差分逻辑仅在 paraformer 回退路径保留）。

想回到双模型流式：`--offline-model paraformer-zh`，此时 `--model` 与 `--chunk-size` 恢复生效。

### 协议 v2：partial 改为全量文本（**破坏性变更**）

`protocol_version` 由 `1` 升到 `2`。`partial.text` 从"增量片段"改为"当前完整预览文本"，客户端由拼接改为替换。

整段重跑会**修正**先前的文字，增量语义无法表达这种回溯修改，因此必须改。

- macOS / Windows 客户端已同步更新（各一行）。
- 旧客户端连新服务端只会看到预览重复堆叠——**影响范围仅限预览显示**，实际上屏文本永远来自 `final` 帧，不受影响。
- 文本无变化时不再发 `partial`。

### 其他

- WebSocket 预览改到后台任务执行。Tornado 会等 `on_message` 的协程结束才读下一条消息，若把逐渐变长的预览推理放在里面，音频会堵在接收缓冲区、预览越拖越远。
- 预览做合并（coalescing）：上一次还没跑完就跳过本次，积压音频在下一次一并处理。长录音下预览自然变稀疏，推理队列不会无限堆积。
- 单模型模式下预览与终稿共用一个 executor——它们是同一个 ONNX session 和同一份 fbank 前端（`WavFrontend` 持有可变状态），必须串行。
- 已知特性：预览耗时随录音长度线性增长。60s 以上的长录音，finalize 可能要排在一次在途预览之后（实测约 1.2s）；5–20s 的常规口述为 50–170ms。

### 默认识别模型改为 SenseVoice-Small

产出最终文本的模型从 `paraformer-zh + ct-punc` 换成 `sensevoice-small`（`iic/SenseVoiceSmall-onnx`，int8，241MB）。流式预览仍用 `paraformer-zh-streaming`——SenseVoice 不支持流式。

- **自带标点与 ITN**，不再需要独立的 `ct-punc` 模型。模型总占用从约 1.23GB 降到 241MB。
- **数字与英文术语明显更准**：口述「一二七点零点零点一的六零零八端口」现在上屏为 `127.0.0.1的6008端口`，此前是逐字的「一二七点零点零点一」。
- **启动更快**：模型加载从约 8.6s 降到约 1.0s。
- **速度基本持平**：59s 语料的推理总耗时 0.56s vs 0.53s（RTF 0.010 vs 0.009）。
- 新增 `--sensevoice-language`（`auto`/`zh`/`en`/`yue`/`ja`/`ko`，默认 `auto`）。
- 新增 `--offline-model sensevoice-small-fp32`（社区 fp32 导出，893MB）。实测质量与 int8 持平而速度慢约 1.6 倍，因此不作默认值。
- `--punc-model` 现在只对 paraformer 生效；用 SenseVoice 时会被忽略并记一条日志。
- 推理阶段固定 `dither=0`，同一段音频的识别结果不再随机抖动。
- 新增 `scripts/bench_asr.py`：三方模型对比基准，输出耗时、RTF 与 CER。

想回到旧行为：`--offline-model paraformer-zh`（流式）或 `--model paraformer-zh`（非流式）。

### 移除热词功能

热词在两条 ONNX 路径上**都不生效**，且是静默失败：普通 `funasr_onnx.Paraformer.__call__(wav, **kwargs)` 会把 `hotword=` 直接吞掉（只有 `ContextualParaformer` / `SeacoParaformer` 才实现），而原先的 `try/except TypeError` 探测永远不会触发。SenseVoice 同样没有 contextual biasing 通路。

与其保留一个看起来能用、实际无效的设置项，直接整体移除：

- 服务端不再接受 `X-Hotwords` 头、`hotwords` 查询参数与 WebSocket `start` 帧的 `hotwords` 字段。老客户端继续发送不会报错，只是被忽略。
- 配置中的 `hotword_files` 与 `hotwords.txt` 不再读取，新写回的配置也不再包含该字段；遗留字段会被静默忽略。
- 三个客户端同步移除相关配置、文件管理与设置界面（macOS/Windows 的「用户热词」设置页已删除）。
- 恢复该能力需要把识别模型换成支持 contextual biasing 的变体（如 SeaCo-Paraformer）。历史说明保留在根 `README.md`。

### 修复

- **Dockerfile 强制传入离线模型**：`ENV MODEL=paraformer-zh` 加上始终传 `--model "$MODEL"`，会把离线模型当成流式预览模型塞给流式识别器（CLI 默认是流式模式）。现在 `MODEL` / `OFFLINE_MODEL` / `PUNC_MODEL` 都改为不设置时不传，交由 CLI 按模式选默认值。

## 1.4.0

### 并发与可靠性

- **流式/离线双 executor**：流式 `feed` 预览与离线 `finalize`/HTTP 识别拆分为两个独立单 worker 线程池，避免并发会话时实时预览被漫长的离线复识别阻塞。
- **LLM 截断防护**：纠错请求检查 `finish_reason`，因 `max_tokens` 截断时返回原文而非丢弃后半段；并按输入长度动态放大 `max_tokens`。

### 安全与隐私

- **`check_origin`**：拒绝携带 `Origin` 头的连接（原生客户端不发 `Origin`），防止任意浏览器网页连上本机 `ws://127.0.0.1` 白嫖识别算力。
- **日志**：识别文本与 LLM 修正内容降至 `DEBUG` 级别，`INFO` 仅保留耗时，减少默认日志中的隐私泄露。

## 1.3.0

### 协议与鉴权统一

- 新增仓库根目录 [`PROTOCOL.md`](../PROTOCOL.md)，作为客户端↔服务端协议的唯一来源。
- **鉴权**：HTTP 与 WebSocket 走同一份 `authorize_request()`。新规则——未配置 `--api-keys` 放行；配置后 listen=`127.0.0.1` 放行；其他监听地址必须 `Authorization: Bearer <key>`。修复了 HTTP 在 `--host 0.0.0.0 --api-keys ...` 下仍允许本机无密钥访问的语义差。
- **partial 协议**：明确 `partial.text` 为**增量片段**。`Session.feed` 加防御性差分，即使底层模型返回累计串也会裁成 delta 再下发。
- **feed 异常上报**：流式 feed 单帧异常改为发 `{"type":"warning","code":"feed_failed"}` 帧，**连接保留**，finalize 仍可走离线模型兜底。新增 `no_session` warning。
- **`/health`** 新增 `version`、`protocol_version`、`asr_model`、`offline_model`、`punc_model`、`device` 字段，便于客户端日志与诊断。

## 1.2.0

### 流式双通道：partial 预览 + 离线复识别 final

流式模式（WebSocket）引入两通道架构，同时兼顾上屏速度与识别准确率：

- **partial（预览）**：录音期间继续使用流式模型（`paraformer-zh-streaming`）产出逐字预览，在客户端 HUD 实时显示，不直接写入目标程序。
- **final（最终结果）**：松开热键后，对录音期间累积的完整 PCM 执行一次离线整段识别（`paraformer-zh`），精度与 `--no-streaming` 模式一致；标点恢复、LLM 纠错均作用于该结果。

如果离线复识别出现异常，自动回退到流式碎片拼接。

#### 新增参数

- `--offline-model`：流式模式下用于复识别的离线模型，默认 `paraformer-zh`

#### 注意事项

- 流式模式启动时同时加载两个 ASR 模型，内存占用约增加 220MB
- 标点模型仅挂在离线识别器上，不再重复加载
- 对已有客户端透明，无需修改；macOS Swift 和 Windows 原生客户端均已支持

---

## 1.0.0

首个可发布版本。服务端从仓库内脚本入口重构为标准 Python package，并补齐发布链路。

### 代码变化

- 将原 `server/asr_server.py` 拆分为 `voice_typer_server/cli.py` 与 `voice_typer_server/app.py`
- 将 `auth.py`、`recognizer.py`、`llm_client.py` 迁移到 `voice_typer_server/` 包内
- 新增 `voice_typer_server/__main__.py`，支持 `python -m voice_typer_server`
- 新增 `console_scripts` 入口，支持 `voice-typer-server`
- 将 LLM prompt 改为 package resource，随 wheel/sdist 一起发布
- CLI 改为懒加载运行时依赖，使 `--help` 与 `--version` 不依赖完整运行环境

### 结构变化

- 新增 `server/pyproject.toml`，将服务端切换为标准打包结构
- 新增 `server/scripts/voice_typer_server.sh`
- 新增 `server/Makefile`
- 新增 `server/RELEASING.md`
- 新增 `server/requirements-release.txt`
- 删除旧入口与包装脚本：
  - `server/asr_server.py`
  - `server/run.sh`
  - `server/setup.sh`
- 删除旧的顶层模块文件，统一迁移到 `server/voice_typer_server/`

### 启动方式变化

- 旧方式：
  - `python asr_server.py`
  - `./run.sh`
  - `./setup.sh --start-server`
- 新方式：
  - `python -m voice_typer_server`
  - `voice-typer-server`
  - `./scripts/voice_typer_server.sh setup`
  - `./scripts/voice_typer_server.sh run`

### 文档变化

- 重写 `server/README.md`，改为 package/CLI 视角
- 更新根 `README.md` 中的服务端安装、接口与能力说明
- 新增发布流程文档 `server/RELEASING.md`

### 发布链路

- 新增 `make bootstrap-release`
- 新增 `make build`
- 新增 `make check`
- 新增 `make release-test`
- 新增 `make release`

### 其他调整

- 最低支持 Python 版本明确为 3.9，推荐版本为 3.12
- 版本号改为单点维护：`voice_typer_server/__init__.py`
- 更新 `.gitignore`，覆盖打包产物、发布虚拟环境与 PyPI 凭据文件
