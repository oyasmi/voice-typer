# VoiceTyper - Agent Instructions

## 项目定位

VoiceTyper 的主体是**前后端一体的本地桌面应用**：识别引擎直接运行在应用进程内，不依赖独立
服务端，也不经过网络传输音频。

- `macos/`：macOS 主力实现，Swift + AppKit/SwiftUI + ONNX Runtime，仅支持 Apple Silicon。
- `windows/`：Windows 主力实现，.NET 10 + WinForms + ONNX Runtime，支持 x64/arm64。
- `client-server/`：旧客户端—服务端架构，保留用于 Linux、远程部署和共享服务端场景。修改该目录
  时还必须遵守 `client-server/AGENTS.md`。

Linux 一体化版本尚未实现。不要把 `client-server/client_linux/` 描述成新的本地一体化实现。

## 核心架构约定

- 客户端录音统一为 **16kHz、float32、单声道 PCM**。
- 主流程为 `Idle → Recording → Recognizing → Inserting`；短于 300ms 的录音直接丢弃。
- macOS 与 Windows 的本地 ASR 会话保持相同语义：录音中返回可回溯修正的完整预览，松开热键后
  返回最终文本。
- 默认模型是 SenseVoice-Small，自带标点与 ITN；不要额外叠加标点模型。
- 文本插入使用剪贴板加系统级输入模拟。必须尽可能恢复用户原剪贴板，并明确处理权限失败。
- LLM 纠错默认关闭，只发送识别文本、不发送音频；密钥必须存入 Keychain/DPAPI，不得写入 YAML
  或日志。
- 模型文件与构建产物不得提交。模型由首次启动下载器获取，并校验固定 sha256。

Windows 的识别管线是 macOS Swift 实现的 C# 对应版本。修改 fbank、LFR/CMVN、CTC 解码、文本后处理
或模型输入输出契约时，必须同步评估两个平台，并继续共用
`macos/Tests/VoiceTyperTests/Fixtures/` 下的金标准夹具。

## 常用命令

### macOS

```bash
cd macos
ruby scripts/generate_xcodeproj.rb  # 新增、删除或移动 Swift 文件后必须执行
./build_xcode.sh
xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper \
  -destination 'platform=macOS' test
```

需要真实模型的测试在模型不存在时会跳过。可运行 `macos/scripts/fetch_model.sh` 预置模型。

### Windows

```bat
cd windows
dotnet restore
dotnet run
dotnet test
build.bat
```

Windows 实现尚未完成真实 Windows 硬件验证。涉及发布、性能或平台 API 的结论必须区分“代码审查
通过”和“真机验证通过”，不得把估算写成实测。

## 修改要求

- 代码注释、用户文档和开发文档使用中文；标识符遵循各语言惯例。
- Swift UI 更新必须在主线程；ASR 推理继续通过串行队列执行。
- C# 开启 nullable，异步 API 接受并传播 `CancellationToken`；UI 更新必须回到 WinForms UI 线程。
- Python 代码使用 4 空格、类型提示、`snake_case` 函数/变量和 `PascalCase` 类。
- 不静默吞掉异常。沿用平台现有日志设施，日志不得包含 API Key、完整敏感配置或不必要的用户文本。
- 优先补充或更新自动化测试；涉及热键、权限、麦克风、剪贴板和真实推理的改动，还要给出手工验证步骤。
- 修改目录结构、命令、配置字段、平台支持范围或发布方式时，同步更新根 README、对应平台 README
  与设计文档中的链接和说明。

## 文档边界

- 根 `README.md` 描述当前一体化产品与仓库入口。
- `macos/README.md`、`windows/README.md` 面向各平台用户和开发者。
- `macos/DESIGN.md`、`windows/DESIGN.md` 记录架构决策、验证证据与待办风险。
- `client-server/README.md` 和 `client-server/PROTOCOL.md` 只描述旧客户端—服务端架构；协议文件不
  约束 `macos/` 或 `windows/` 的进程内接口。
