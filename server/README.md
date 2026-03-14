# VoiceTyper 服务器 (ASR + LLM)

这是 VoiceTyper 应用程序的后端服务器组件。它提供了一个基于 Tornado 的 HTTP 接口，用于接收语音输入流并返回转换后的文本。

该服务集成了以下能力：
- **ASR (自动语音识别)**: 默认使用官方离线 ONNX 版 `paraformer-zh` 模型将语音转成文本。
- **PUNC (标点恢复)**: 默认使用官方离线 ONNX 版 `ct-punc` 模型为识别后的文本自动添加标点。
- **LLM (大语言模型)**: 可选配置，支持调用大模型对识别后文本进行二次纠错和润色（例如，使用预设的 Prompt 修复同音字或术语错误）。

## 部署与运行方式

我们提供了多种方式来启动该服务器，您可以根据您的使用场景选择最适合的一种。

### 方式 1: 直接使用 Python 运行 (适合开发/调试)

如果您希望手动管理 Python 的运行环境并直接启动服务。

1. **安装依赖:**
   在 `server` 目录下，使用您的 Python 环境 (建议 Python 3.10+) 执行：
   ```bash
   pip install -r requirements.txt
   ```
2. **启动服务:**
   ```bash
   python asr_server.py --host 0.0.0.0 --port 6008
   ```
   您可以通过 `python asr_server.py --help` 查看完整参数列表，例如配置 LLM 的 `--llm-base-url` 和 `--llm-api-key`。
   服务端现在只保留 ONNXRuntime 路线；对于官方可下载的离线 ONNX 模型，若目录同时存在 `model.onnx` 与 `model_quant.onnx`，会优先使用完整版 `model.onnx`，否则自动回退到量化模型。

### 方式 2: 使用 Shell 脚本运行 (推荐 Linux/macOS 用户)

我们提供了 `setup.sh` 和 `run.sh` 脚本来简化虚拟环境的创建和服务的启动流程。

1. **初始化环境与安装依赖:**
   该命令将在 `~/.venvs/voice-typer` 创建一个虚拟环境，并安装必要的依赖库。
   ```bash
   ./setup.sh --install-lib
   ```
   
2. **启动服务:**
   ```bash
   ./setup.sh --start-server
   ```
   运行后，脚本会提示您输入 `LLM API Key`。如果您希望开启 LLM 纠错功能，请粘贴您的 Key 并回车；如果不需要，直接按回车即可禁用。

   *注：`run.sh` 也可以被独立执行，它是底层的启动包装脚本，支持所有 `asr_server.py` 支持的类似 `--host`、`--port`、`--onnx-threads`、`--llm-*` 等长参数。*

### 方式 3: 使用 Docker 部署 (推荐生产/服务器环境)

使用 Docker 进行部署可以完全隔离环境，避免系统依赖冲突问题。

1. **构建 Docker 镜像:**
   在 `server` 目录下执行：
   ```bash
   docker build -t voice-typer-server:latest .
   ```

2. **运行 Docker 容器:**
   
   **最简运行 (不使用 LLM 纠错):**
   ```bash
   docker run -d -p 6008:6008 --name voice-typer voice-typer-server:latest
   ```

   **完整运行 (启用接口鉴权及 LLM 纠错):**
   ```bash
   docker run -d -p 6008:6008 \
     --name voice-typer \
     -e API_KEYS="my_secret_key_1" \
     -e LLM_BASE_URL="https://api.openai.com/v1" \
     -e LLM_API_KEY="sk-xxxxxx" \
     -e LLM_MODEL="gpt-4o-mini" \
     voice-typer-server:latest
   ```
   （更多可用环境变量映射参见 `Dockerfile`）

## 接口说明

服务启动后，默认在 `6008` 端口提供服务。

- `GET /health`：服务健康检查接口
- `POST /recognize`：语音识别核心接口。推荐使用 `application/octet-stream` 直接上传 `float32` 音频字节，并通过请求头 `X-Hotwords` 和查询参数 `llm_recorrect=true|false` 传递附加参数；同时兼容旧版 `multipart/form-data` 上传方式（字段名 `audio`，以及表单字段 `hotwords`、`llm_recorrect`）。

## ONNX 部署说明

- 服务端仅保留 `funasr-onnx + onnxruntime` 这一路线，不再依赖 PyTorch
- `--device` 当前支持 `cpu` / `cuda` / `cuda:N`
- `--onnx-threads N` 用于设置 ONNX Runtime 的 `intra_op_num_threads`
- 默认模型短名会映射到官方离线 ONNX 模型：
  - `paraformer-zh` → `damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx`
  - `ct-punc` → `damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx`
- 官方当前公开分发的这两套离线 ONNX 模型目录中只包含 `model_quant.onnx`，因此实际部署时会自动使用量化模型

## 源码文件说明

- `asr_server.py`: 服务器的主入口文件，包含基于 Tornado 框架的 HTTP 服务路由、参数解析以及服务生命周期管理。
- `auth.py`: 提供了鉴权中间件支持，主要实现基于 API Key 的访问控制拦截（`BaseAuthenticatedHandler`）。
- `llm_client.py`: 封装了与大语言模型 (如 OpenAI 兼容接口) 的通信客户端模块，负责在 ASR 识别后调用模型进行文本纠错和重排润色。
- `recognizer.py`: 封装基于 `funasr-onnx` 的离线 ONNX 识别与标点恢复逻辑。
- `requirements.txt`: Python 依赖库清单文件，列出了运行服务必需的最佳兼容依赖版本。
- `Dockerfile`: 用于构建独立镜像的脚本文件，以便支持该后端服务的容器化部署。
- `run.sh` / `setup.sh`: Linux 和 MacOS 系统下的依赖安装与服务快速启动便捷脚本。
- `run.bat`: Windows 系统环境下的服务端启动包装脚本。
