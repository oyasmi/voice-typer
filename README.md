# VoiceTyper - macOS 本地语音输入工具

基于 FunASR 的本地语音识别，所有处理均在本地完成，保护隐私。

## 功能特点

- 🎤 按住热键即可语音输入，松开自动识别
- ⚡ 语音识别完成后统一输入文本
- 🔒 完全本地离线，无需联网
- ⚙️ 支持自定义热键、模型、热词等

## 系统要求

- macOS Sequoia 15.0+
- Python 3.12+
- Apple Silicon (M1/M2/M3) 推荐，Intel 也支持

## 安装

### 1. 创建虚拟环境

```bash
python3.12 -m venv venv
source venv/bin/activate
```
### 2. 安装依赖
pip install -r requirements.txt
### 3. 首次运行（下载模型）
首次运行会自动下载模型（约 500MB），请确保网络通畅：

python main.py
权限设置
应用需要以下 macOS 权限：

辅助功能权限（必需）
用于监听全局热键和模拟键盘输入。

打开「系统设置 → 隐私与安全性 → 辅助功能」
点击 + 添加 Terminal.app（或您使用的终端）
如果使用 PyCharm 等 IDE，也需要添加该 IDE
麦克风权限（必需）
首次录音时系统会自动请求。

使用方法
启动应用后，菜单栏会出现 🎤 图标
点击菜单中的「启用语音输入」
在任意应用中，按住 Ctrl+空格（默认）开始说话
松开按键，识别结果自动输入到光标位置
配置文件
配置文件位于：~/.config/voice_input/config.yaml

## 常见问题
Q: 热键没反应？
确保已授予辅助功能权限，并在菜单中启用了语音输入。

Q: 识别不准确？
确保麦克风正常工作
添加专业术语到 hotwords 配置
尝试更换模型（如 SenseVoiceSmall）
Q: 中文标点不正确？
确保配置了 punc_model: "ct-punc"。

Q: Apple Silicon 上运行慢？
确保 device 设置为 "mps" 以使用 GPU 加速。

## 开发

```bash
# 单独测试录音
python recorder.py

# 单独测试识别
python recognizer.py

# 单独测试文本输入
python text_inserter.py

# 单独测试提示窗口
python indicator.py

# 测试控制器（无菜单栏）
python controller.py
```

## 致谢
FunASR - 阿里达摩院开源的语音识别工具包
rumps - macOS 菜单栏应用框架
pynput - 键盘鼠标监听库