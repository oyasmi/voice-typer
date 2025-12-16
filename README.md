# VoiceTyper - macOS 本地语音输入工具

基于 FunASR 的离线语音识别，所有处理均在本地完成，保护隐私。

## 功能

- 🎤 按住热键语音输入，松开自动识别
- 🔒 完全本地离线，无需联网
- ⚙️ 支持自定义热键、词库
- 🚀 支持 Apple Silicon GPU 加速

## 系统要求

- macOS 14.0 (Sonoma) 或更高版本
- Python 3.9+（推荐 3.12）
- Apple Silicon (M1/M2/M3/M4) 推荐，Intel 也支持

## 安装

### 1. 安装应用
从 Releases 下载 VoiceTyper-x.x.x-macOS.zip，解压后将 VoiceTyper.app 拖到「应用程序」文件夹。

### 2. 授权
首次运行需要在「系统设置 → 隐私与安全性」中授权：

辅助功能：用于监听热键和模拟键盘输入
麦克风：用于录音
使用方法
启动应用后，菜单栏会出现 🎤 图标
按住 Ctrl+Tab（默认热键）开始说话
松开后自动识别并输入文字
配置
配置目录：~/.config/voice_typer/

配置文件
编辑 ~/.config/voice_typer/config.yaml：

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
- FunASR - 阿里达摩院开源的语音识别工具包
- rumps - macOS 菜单栏应用框架
- pynput - 键盘鼠标监听库
- 