# VoiceTyper - Windows 版本

基于 FunASR 的本地语音输入工具 Windows 客户端。

## 功能特点

- 🎤 **全局热键录音**：按住热键即可录音，释放后自动识别
- 📝 **自动输入**：识别结果直接输入到当前光标位置
- 🔧 **灵活配置**：支持自定义热键、服务器地址、词库等
- 💾 **用户词库**：支持自定义热词，提高专业术语识别率
- 🪟 **系统托盘**：后台运行，不影响日常使用
- 🎨 **录音提示**：录音时显示浮动窗口，实时显示录音时长

## 系统要求

- Windows 10/11
- Python 3.8+ (仅开发/打包时需要)
- 运行打包后的 exe 文件无需 Python 环境

## 快速开始

### 方式一：使用预编译版本（推荐）

1. 下载 `VoiceTyper.exe`
2. 双击运行，程序将在系统托盘显示图标
3. 按住默认热键 `Ctrl+Space` 开始录音
4. 释放热键，等待识别完成并自动输入

### 方式二：从源码运行

```bash
# 安装依赖
pip install -r requirements.txt

# 运行程序
python main.py
```

### 方式三：自行打包

```bash
# 运行打包脚本
build.bat

# 打包完成后，可执行文件位于 dist/VoiceTyper.exe
```

## 配置说明

首次运行时，程序会在 `%APPDATA%\VoiceTyper\` 目录下自动创建配置文件：

### config.yaml

```yaml
# 语音识别服务地址
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: null  # 连接远程服务器时需要

# 热键配置
hotkey:
  modifiers:
    - "ctrl"
  key: "space"

# 用户词库文件
hotword_files:
  - "hotwords.txt"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
```

### hotwords.txt

```text
# 自定义词库，每行一个词
Python
GitHub
Windows
ChatGPT
```

## 热键说明

支持的修饰键：
- `ctrl` - Ctrl 键
- `alt` - Alt 键
- `shift` - Shift 键
- `win` - Windows 键

支持的按键：
- 字母：`a-z`
- 数字：`0-9`
- 功能键：`f1-f12`
- 特殊键：`space`, `tab`, `enter` 等

示例配置：
```yaml
hotkey:
  modifiers: ["ctrl", "shift"]
  key: "r"
```

## 使用技巧

1. **录音时长**：建议每次录音 1-10 秒，过长可能影响识别准确率
2. **环境噪音**：在安静环境下录音效果最佳
3. **清晰发音**：语速适中，发音清晰
4. **自定义词库**：将专业术语、人名、公司名等添加到词库可提高识别率

## 系统托盘菜单

右键点击托盘图标可以：
- ✅ 启用/禁用语音输入
- 📄 打开配置文件
- 📚 打开词库文件
- 📁 打开配置目录
- ℹ️ 查看关于信息
- ❌ 退出程序

## 托盘图标状态

- 🟢 绿色：就绪状态，可以录音
- 🔴 红色：正在录音
- 🟡 黄色：正在识别/处理
- ⚪ 灰色：已禁用

## 故障排除

### 热键无响应

1. 检查其他程序是否占用了相同热键
2. 尝试更改热键配置
3. 以管理员权限运行程序

### 无法识别

1. 确认 ASR 服务已启动并运行在配置的地址
2. 检查防火墙是否阻止了连接
3. 查看控制台输出的错误信息

### 输入乱码

1. 确认目标程序支持 Unicode 输入
2. 检查系统语言设置
3. 尝试使用管理员权限运行

### 录音无声音

1. 检查系统麦克风权限
2. 确认默认录音设备设置正确
3. 测试麦克风是否正常工作

## 文件说明

```
VoiceTyper-Windows/
├── main.py              # 主程序入口
├── config.py            # 配置管理
├── controller.py        # 核心控制器
├── recorder.py          # 录音模块
├── asr_client.py        # ASR 客户端
├── text_inserter.py     # 文本输入模块
├── indicator.py         # 录音提示窗口
├── requirements.txt     # Python 依赖
├── build.bat           # 打包脚本
└── README.md           # 说明文档
```

## 许可证

本项目采用与 macOS 版本相同的许可证。

## 致谢

- FunASR: 阿里巴巴达摩院开源的语音识别框架
- 各开源库的开发者们

## 更新日志

### v1.1.0
- ✨ Windows 平台首次发布
- 🎤 支持全局热键录音
- 📝 自动文本输入
- 🎨 录音状态提示窗口
- 🔧 灵活的配置系统
- 💾 自定义词库支持