# VoiceTyper Server

`voice-typer-server` 是 VoiceTyper 的语音识别服务端。它负责接收客户端上传的音频，完成识别、标点恢复，并可选调用 LLM 做二次纠错。

## 亮点

- 本地运行，默认不依赖云端 ASR
- 提供稳定的 HTTP 接口，客户端可直接接入
- 内置中文识别和标点恢复默认模型
- 支持热词
- 可选启用 API Key
- 可选接入 OpenAI 兼容 LLM 做纠错
- 支持 `python -m`、命令行和脚本三种启动方式

## 适合谁

如果你只是想把 VoiceTyper 跑起来，这个 README 已经够用。

如果你要改代码、打包发布或二次开发，文末有开发者入口。

## Python 版本

- 最低支持：Python 3.10
- 推荐版本：Python 3.12+

## 快速开始

最常见的用法是：

1. 安装服务端
2. 启动服务端
3. 让客户端连接 `127.0.0.1:6008`

## 安装与启动

### 推荐方式：使用脚本

适合 Linux 和 macOS 用户。

```bash
cd server
./scripts/voice_typer_server.sh setup
./scripts/voice_typer_server.sh run
```

脚本会：

- 创建虚拟环境 `~/.venvs/voice-typer`
- 安装 `voice-typer-server`
- 用一组默认参数启动服务

默认启动参数：

- `--host 127.0.0.1`
- `--port 6008`
- `--device cpu`

命令行覆盖示例：

```bash
./scripts/voice_typer_server.sh run --host 0.0.0.0 --onnx-threads 2
```

### 直接使用 Python 包

如果你已经安装了 `voice-typer-server`，可以直接运行：

```bash
python -m voice_typer_server --host 127.0.0.1 --port 6008
```

或：

```bash
voice-typer-server --host 127.0.0.1 --port 6008
```

查看帮助：

```bash
voice-typer-server --help
```

### Docker

如果你更喜欢容器方式：

```bash
docker build -t voice-typer-server:latest .
docker run -d -p 6008:6008 --name voice-typer voice-typer-server:latest
```

### Windows 服务

在 Windows 上可将 VoiceTyper Server 注册为系统服务，实现开机自启和后台运行。

#### 安装与注册

```bat
REM 1. 安装环境（自动安装 pywin32 依赖）
scripts\voice_typer_server.bat setup --local

REM 2. 注册为 Windows 服务（需管理员权限，默认开机自启）
scripts\voice_typer_server.bat install -- --host 127.0.0.1 --port 6008 --device cpu

REM 启用 LLM 校对（推荐，可显著提升识别准确率）
scripts\voice_typer_server.bat install -- --host 127.0.0.1 --port 6008 --device cpu ^
    --llm-base-url https://api.openai.com/v1 ^
    --llm-api-key sk-xxx ^
    --llm-model gpt-4o-mini

REM 手动启动模式（不随系统启动）
scripts\voice_typer_server.bat install --startup manual -- --host 127.0.0.1 --port 6008
```

#### 管理服务

```bat
REM 启动服务
scripts\voice_typer_server.bat start

REM 停止服务
scripts\voice_typer_server.bat stop

REM 卸载服务
scripts\voice_typer_server.bat uninstall
```

也可以通过 `services.msc`（服务管理器）图形化操作，服务名为 **VoiceTyper 语音识别服务**。

#### 服务日志

服务模式下日志写入文件：`%USERPROFILE%\.voice-typer\server.log`，最大 10MB，保留 3 个备份。

#### 注意事项

- 安装、卸载、启停服务均需要**管理员权限**
- 服务默认以 `LocalSystem` 账户运行。如果模型已缓存在当前用户目录下，首次启动可能需要重新下载
- 修改运行参数需先卸载再重新安装服务

## 常用启动参数

- `--host`：监听地址，默认 `127.0.0.1`
- `--port`：监听端口，默认 `6008`
- `--device`：`cpu` / `cuda` / `cuda:N`
- `--model`：ASR 模型，默认 `paraformer-zh`
- `--punc-model`：标点模型，默认 `ct-punc`，设为 `none` 可禁用
- `--onnx-threads`：ONNX Runtime 线程数，默认 `4`
- `--api-keys`：API Key 列表，逗号分隔
- `--llm-base-url`、`--llm-api-key`、`--llm-model`：启用 LLM 纠错

示例：

```bash
voice-typer-server \
  --host 0.0.0.0 \
  --device cpu \
  --onnx-threads 2 \
  --api-keys akey
```

## 常见使用场景

### 仅本机使用

这是默认场景：

```bash
voice-typer-server --host 127.0.0.1 --port 6008
```

此时本机客户端可直接访问，一般不需要额外配置鉴权。

### 局域网远程使用

如果客户端和服务端不在同一台机器上，建议启用 API Key：

```bash
voice-typer-server --host 0.0.0.0 --api-keys your_key
```

然后在客户端配置中填入：

- 服务端 IP
- 对应端口
- `api_key`

### 启用 LLM 纠错

```bash
voice-typer-server \
  --llm-base-url https://api.openai.com/v1 \
  --llm-api-key sk-xxx \
  --llm-model gpt-4o-mini
```

客户端再启用 `llm_recorrect` 即可。

## 接口

服务端默认暴露两个接口：

- `GET /health`
- `POST /recognize`

### `/health`

用于检查服务是否已启动、模型是否已就绪。

### `/recognize`

用于提交音频并获取识别结果。

推荐方式：

- `Content-Type: application/octet-stream`
- 请求体直接放 16kHz `float32` 原始音频字节

可选参数：

- 请求头 `X-Hotwords`
- 查询参数 `llm_recorrect=true|false`

同时也兼容旧版 `multipart/form-data` 上传。

示例：

```bash
curl -X POST "http://127.0.0.1:6008/recognize?llm_recorrect=false" \
     -H "Content-Type: application/octet-stream" \
     --data-binary @test.float32
```

带 API Key：

```bash
curl -X POST http://127.0.0.1:6008/recognize \
     -H "Authorization: Bearer your-api-key" \
     -F "audio=@test.wav"
```

## 模型与运行说明

- 服务端使用 `onnxruntime`
- 默认 ASR 模型短名：
  - `paraformer-zh`
- 默认标点模型短名：
  - `ct-punc`

短名会自动映射到官方离线 ONNX 模型。

如果模型目录中只有 `model_quant.onnx`，服务端会自动使用量化模型。

## 性能优化

### NVIDIA GPU 加速

使用 CUDA 加速识别：

```bash
voice-typer-server --device cuda
# 多卡指定：
voice-typer-server --device cuda:1
```

### 内存优化

关闭标点模型可降低部分资源占用：

```bash
voice-typer-server --punc-model none
```

## 常见问题

### 服务启动了，但客户端连不上

- 检查服务端实际监听地址
- 检查客户端配置中的 `host` 和 `port`
- 本机部署时，应优先使用 `127.0.0.1:6008`

### 远程调用返回 401

- 检查是否配置了 `--api-keys`
- 检查客户端是否正确带上 `Authorization: Bearer ...`

### 首次启动较慢

首次运行可能会下载模型，这是正常现象。

### Apple Silicon 为什么没有 MPS

当前服务端只支持：

- `cpu`
- `cuda`
- `cuda:N`

在 Apple Silicon 上建议直接使用 `cpu`。

## 开发者说明

如果你要修改代码或发布包，请查看：

- [RELEASING.md](./RELEASING.md)
- [CHANGELOG.md](./CHANGELOG.md)

主要代码位置：

- [voice_typer_server/cli.py](voice_typer_server/cli.py)
- [voice_typer_server/app.py](voice_typer_server/app.py)
- [voice_typer_server/recognizer.py](voice_typer_server/recognizer.py)
- [voice_typer_server/llm_client.py](voice_typer_server/llm_client.py)
- [voice_typer_server/auth.py](voice_typer_server/auth.py)
- [voice_typer_server/win_service.py](voice_typer_server/win_service.py) — Windows 服务包装（仅 Windows）
