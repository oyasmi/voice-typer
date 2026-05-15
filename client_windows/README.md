# VoiceTyper Windows 客户端

VoiceTyper 的 Windows 客户端，基于 Python 实现，提供全局热键录音、实时状态指示器和离线语音识别功能。

## 功能特点

- **全局热键**: 默认使用 `Ctrl + F2` 触发录音，释放即可识别。
- **状态反馈**: 屏幕中央显示录音状态指示器。
- **离线识别**: 连接到本地部署的 FunASR 服务进行高精度语音识别。
- **LLM 修正**: 支持通过大模型修正识别结果（需要服务端支持）。
- **用户词库**: 支持自定义热词，提高专业术语识别率。
- **托盘管理**: 系统托盘图标管理，支持配置修改。

## 环境要求

- Windows 10/11
- Python 3.10+

## 安装步骤

### 从 Release 安装

从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper.exe`，双击即可运行。

### 从源码安装

1. **安装依赖**

```bash
pip install -r requirements.txt
```

2. **配置服务端**

确保本地或远程已启动 VoiceTyper 服务端（FunASR）。
客户端默认连接 `127.0.0.1:6008`。

> **重要**：Windows 客户端使用 HTTP 非流式协议，服务端须以 `--no-streaming` 模式启动：
> ```bat
> scripts\voice_typer_server.bat run --no-streaming
> ```
> 若使用默认（流式）模式启动服务端，客户端将无法识别。详见 [服务端文档](../server/README.md)。

## 使用方法

1. **启动客户端**

```bash
python main.py
```

2. **开始录音**

- 按住 `Ctrl + F2`（默认热键）开始说话。
- 屏幕会出现"正在听..."的指示器。
- 松开按键结束录音，识别结果将自动插入到当前光标位置（录音不足 0.3 秒将被忽略）。

3. **托盘菜单**

- 右键点击任务栏托盘图标。
- 可以打开配置文件、词库文件或退出程序。

## 配置

配置文件位置: `%APPDATA%\voice_typer\config.yaml`

运行客户端一次后会自动生成默认配置文件，您也可以手动创建或修改：

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  llm_recorrect: true  # 启用 LLM 智能纠错

hotkey:
  modifiers: ["ctrl"]    # ctrl, alt, shift, win_l, win_r
  key: "f2"             # 默认 Ctrl + F2

hotword_files:
  - "hotwords.txt"
```

### 自定义词库

编辑 `%APPDATA%\voice_typer\hotwords.txt`，每行一个词：

```text
# 专业术语
FunASR
Python
GitHub

# 自定义词汇
你的名字
公司名称
```

## 打包发布

使用 PyInstaller 打包为可执行文件：

```bash
pyinstaller voicetyper.spec
```

打包完成后，`dist` 目录下会生成 `VoiceTyper.exe`。

## 常见问题

- **录音没反应**: 检查麦克风权限是否开启。
- **无法连接服务**: 确保服务端已启动，且防火墙允许端口通信。
- **热键冲突**: 修改配置文件中的热键设置。

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [服务端文档](../server/README.md)
