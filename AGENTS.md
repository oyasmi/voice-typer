# VoiceTyper - Agent Instructions

## 项目概述
VoiceTyper 是一个基于 ONNX 模型的跨平台语音输入工具，采用 HTTP/JSON 客户端-服务端架构。
- **Server** (`server/`)：Python ASR 服务，基于 `onnxruntime`。
- **Clients**：macOS (`client_macos/`)、Linux (`client_linux/`)、Windows (`client_windows/`) 以及规划中的 Go 重写 (`client_go/`)。

## 安装与运行

### Server (Python)
推荐通过脚本安装与运行服务端（默认监听 `127.0.0.1:6008`）：
```bash
cd server
./scripts/voice_typer_server.sh setup  # 按提示建立环境并安装依赖
./scripts/voice_typer_server.sh run    # 启动服务
```
或直接通过 Python 包运行：`voice-typer-server --help`

### macOS Client
```bash
cd client_macos
make install && make run
```

### Linux Client (Wayland)
```bash
cd client_linux
make install && make install-udev && make run
```

### Windows Client
```bash
cd client_windows
pip install -r requirements.txt
python main.py
```

## 测试流程
1. 启动服务端：`cd server && ./scripts/voice_typer_server.sh run`
2. 启动客户端开发模式（`make run` 或 `python main.py`）。
3. 长按快捷键说话，松开后查看识别结果是否自动输入。
4. 查看日志（macOS/Linux 可使用 `make log`）。

## 代码规范与约定
- **Python**：使用 4 个空格缩进，函数/变量 `snake_case`，类 `PascalCase`，强制使用类型提示（`typing`）。
- **语言**：代码注释和文档使用**中文**。
- **错误处理**：避免静默忽略异常，统一使用 `logging`（格式：`%(asctime)s - %(levelname)s - %(message)s`）。
- **音频流**：客户端录音格式为 **16kHz float32** 单声道，随 HTTP POST 发至 `/recognize` 接口。
- **文本输入机制**：通过剪贴板 + 键盘模拟（macOS: pbcopy; Linux: wl-copy; Windows: pyperclip）。

> **注意**：
> - 服务端已移除 GPU OOM 隐患，建议使用 `cpu` 运行推理，可选配 LLM 纠错（详情参考 `server/README.md`）。
> - 添加或修改客户端时，确保与 `host` 及 `api_key` 鉴权相关逻辑一致。
