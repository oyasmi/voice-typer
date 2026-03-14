# VoiceTyper 服务器 (ASR + LLM)

这是 VoiceTyper 的服务端实现，现已整理为标准 Python package，可直接通过模块或命令行工具启动。

## Python 版本

- 最低支持：Python 3.9
- 推荐版本：Python 3.12

保留 Python 3.9 兼容性，是为了兼容当前 macOS 自带 Python 的直接使用场景。

## 安装与运行

### 方式 1：本地源码安装

```bash
cd server
pip install .
```

启动服务：

```bash
python -m voice_typer_server --host 127.0.0.1 --port 6008
```

或：

```bash
voice-typer-server --host 127.0.0.1 --port 6008
```

查看参数帮助：

```bash
python -m voice_typer_server --help
voice-typer-server --help
```

### 方式 2：使用辅助脚本（Linux/macOS）

脚本位于 [scripts/voice_typer_server.sh](/home/oyasmi/projects/voice-typer/server/scripts/voice_typer_server.sh)。

创建虚拟环境并安装：

```bash
./scripts/voice_typer_server.sh setup
```

本地开发态安装当前目录源码：

```bash
./scripts/voice_typer_server.sh setup --local
```

`--local` 会直接安装当前源码目录，不依赖 PyPI 包名解析。

运行服务：

```bash
./scripts/voice_typer_server.sh run
```

覆盖默认参数：

```bash
./scripts/voice_typer_server.sh run --host 0.0.0.0 --api-keys akey
```

脚本使用固定虚拟环境路径：

```text
~/.venvs/voice-typer
```

### 方式 3：Docker

构建镜像：

```bash
docker build -t voice-typer-server:latest .
```

启动容器：

```bash
docker run -d -p 6008:6008 --name voice-typer voice-typer-server:latest
```

## 常用参数

- `--host`：监听地址，默认 `127.0.0.1`
- `--port`：监听端口，默认 `6008`
- `--model`：ASR 模型，默认 `paraformer-zh`
- `--punc-model`：标点模型，默认 `ct-punc`，设为 `none` 可禁用
- `--device`：设备，当前支持 `cpu` / `cuda` / `cuda:N`
- `--onnx-threads`：ONNX Runtime 线程数，默认 `4`
- `--api-keys`：API 密钥列表，逗号分隔
- `--llm-base-url` / `--llm-api-key` / `--llm-model`：LLM 纠错配置

示例：

```bash
voice-typer-server \
  --host 0.0.0.0 \
  --device cpu \
  --onnx-threads 2 \
  --api-keys akey \
  --llm-base-url https://api.openai.com/v1 \
  --llm-api-key sk-xxx \
  --llm-model gpt-4o-mini
```

## 接口说明

- `GET /health`：健康检查
- `POST /recognize`：语音识别接口

推荐使用 `application/octet-stream` 直接上传 `float32` 音频字节，并通过请求头 `X-Hotwords` 和查询参数 `llm_recorrect=true|false` 传递附加参数；同时兼容旧版 `multipart/form-data` 上传方式。

## ONNX 部署说明

- 服务端仅保留 `funasr-onnx + onnxruntime` 路线
- 默认模型短名映射：
  - `paraformer-zh` → `damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx`
  - `ct-punc` → `damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx`
- 若模型目录仅包含 `model_quant.onnx`，会自动使用量化模型

## 代码结构

- `voice_typer_server/cli.py`：命令行参数解析
- `voice_typer_server/app.py`：服务装配与生命周期
- `voice_typer_server/recognizer.py`：FunASR ONNX 封装
- `voice_typer_server/llm_client.py`：OpenAI 兼容 LLM 客户端
- `voice_typer_server/auth.py`：API Key 鉴权
- `run.bat`：Windows 下的轻量入口，内部调用 `python -m voice_typer_server`

## 发布

发布链路说明见 [RELEASING.md](/home/oyasmi/projects/voice-typer/server/RELEASING.md)。
