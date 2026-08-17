# VoiceTyper 客户端—服务端版本 - Agent Instructions

本目录是 VoiceTyper 保留维护的旧客户端—服务端实现，不是当前默认产品架构。目录内改动同时遵守
仓库根 `AGENTS.md`。

## 结构

- `server/`：Python ASR 服务，默认监听 `127.0.0.1:6008`。
- `client_macos_swift/`：Swift + AppKit 分体式客户端，默认 WebSocket 流式识别。
- `client_windows_native/`：.NET 8 + WinForms 分体式客户端，默认 WebSocket 流式识别。
- `client_linux/`：Python + GTK4 + evdev 的 Wayland 客户端，使用 HTTP 非流式识别。
- `PROTOCOL.md`：本目录所有客户端与服务端之间的唯一协议来源。

## 常用命令

```bash
cd client-server/server
./scripts/voice_typer_server.sh setup
./scripts/voice_typer_server.sh run

# Linux 客户端要求非流式模式
./scripts/voice_typer_server.sh run --no-streaming
```

```bash
cd client-server/client_linux
make install
make install-udev
make run
```

macOS 分体式客户端使用 `client-server/client_macos_swift/VoiceTyperClient.xcodeproj`；Windows
分体式客户端在 `client-server/client_windows_native/` 中运行 `dotnet restore && dotnet run`。

## 协议与兼容性

- 音频格式为 16kHz、float32、单声道 PCM。
- 原生客户端默认使用 WebSocket `/recognize/stream`；Linux 使用 HTTP `POST /recognize`。
- `partial.text` 是当前**完整预览文本**，客户端必须替换而不是拼接。
- `host`、`scheme`、`api_key`、流式开关及鉴权语义必须在服务端与三个客户端间保持一致。
- 改动路由、帧格式、字段、错误码、鉴权或会话限制时，在同一次改动中更新 `PROTOCOL.md`、服务端
  测试及所有受影响客户端。

## 测试

```bash
cd client-server/server
.venv-release/bin/python -m pytest -q
```

客户端目前以手工测试为主：启动匹配模式的服务端，启动客户端，验证按住说话、实时预览（原生
客户端）、松开上屏、短按丢弃、鉴权及断线恢复。

Python 代码使用 4 空格缩进、类型提示、`snake_case` 函数/变量和 `PascalCase` 类；避免静默忽略
异常，使用现有 `logging` 设施。代码注释和文档使用中文。
