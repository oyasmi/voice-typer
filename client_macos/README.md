# VoiceTyper macOS Client

macOS 菜单栏语音输入客户端，提供基于 FunASR 的离线语音识别功能。

## 功能特性

- 🎤 **菜单栏应用** - 简洁的菜单栏图标，显示实时工作状态
- ⌨️ **全局热键** - 按住热键录音，松开自动识别并插入文字
- 🌐 **Fn 键支持** - 支持绑定 macOS Fn（地球仪）键作为热键
- 🔒 **完全离线** - 本地处理音频，无需上传到云端
- 🔧 **高度可定制** - 自定义热键、透明度和识别服务地址
- 🎯 **用户词库** - 支持添加专业术语或常用词汇
- ⏱️ **短录音过滤** - 自动丢弃 0.3 秒以下的误触录音

## 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Python 3.9+ (推荐 3.12)
- Apple Silicon (M1/M2/M3/M4) 系列芯片效果最佳

## 安装步骤

1. **克隆仓库并安装依赖**

```bash
cd client_macos
pip install -r requirements.txt
```

2. **配置服务端**

确保 VoiceTyper 服务端已启动。默认连接地址为 `127.0.0.1:6008`。

3. **运行程序**

```bash
python main.py
```

## 使用方法

1. **开始录音**：按住热键（默认 `Ctrl + F2`，可配置为 Fn 键）
2. **说话**：对着麦克风说话
3. **结束并识别**：松开热键（录音不足 0.3 秒将被自动忽略）
4. **自动输入**：识别结果将自动插入到当前活跃的文本框

## 配置

配置文件路径：`~/.config/voice_typer/config.yaml`

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  llm_recorrect: true  # 开启 LLM 纠错项

hotkey:
  modifiers:
    - "ctrl"
  key: "f2"
  # 或使用 Fn(地球仪) 键:
  # modifiers: []
  # key: "fn"

ui:
  opacity: 0.85
  width: 240
  height: 70
```

## 构建发布版

使用 `PyInstaller` 构建 `.app` 文件：

```bash
./build.sh
```

构建完成后，在 `dist` 目录下可以找到 `VoiceTyper.app`。

## 权限说明

由于 macOS 的安全机制，首次使用需要授予以下权限：
- **隐私与安全性 → 辅助功能 (Accessibility)**: 用于监听全局热键和模拟键盘输入。
- **隐私与安全性 → 输入监控 (Input Monitoring)**: 用于监听 Fn 等按键事件。
- **隐私与安全性 → 麦克风 (Microphone)**: 用于采集音频。
