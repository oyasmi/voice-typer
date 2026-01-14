# VoiceTyper Linux Client

Linux Wayland 语音输入客户端，使用 FunASR 进行离线语音识别。

## 特性

- 🎤 **离线语音识别** - 使用 FunASR 进行本地语音识别，无需网络连接
- ⌨️ **全局热键** - 按住 `Ctrl+Space` 开始录音，释放自动识别并插入文本
- 🖥️ **Wayland 原生支持** - 专为 Wayland + GNOME 环境设计，使用 evdev 和 gtk-layer-shell
- 📋 **智能文本插入** - 自动将识别结果插入到当前光标位置
- 🎯 **用户词库** - 支持自定义词汇，提高识别准确率
- 🔧 **可视化指示器** - 录音时显示浮动窗口，实时显示录音时长

## 系统要求

- **操作系统**: Linux (Wayland 会话)
- **桌面环境**: GNOME (推荐) 或其他支持 Wayland 的桌面环境
- **Python**: 3.9 或更高版本
- **依赖**: GTK4, gtk-layer-shell, AppIndicator

## 安装

### 1. 安装系统依赖

#### Ubuntu / Debian

```bash
# 基础依赖
sudo apt update
sudo apt install -y python3 python3-pip python3-dev build-essential

# GTK4 和 Layer Shell
sudo apt install -y gir1.2-gtk-4.0 libgtk-4-1 \
    gir1.2-gtklayershell-1.0 libgtk-layer-shell-1.0

# AppIndicator (系统托盘)
sudo apt install -y gir1.2-ayatanaappindicator3-0.1 libayatana-appindicator3-1

# Wayland 剪贴板工具
sudo apt install -y wl-clipboard

# 音频库
sudo apt install -y libportaudio2 portaudio19-dev
```

#### Fedora / RHEL

```bash
sudo dnf install python3 python3-devel python3-pip \
    gtk4 gtk4-layer-shell \
    ayatana-appindicator wl-clipboard \
    portaudio-devel
```

#### Arch Linux

```bash
sudo pacman -S python python-pip gtk4 gtk4-layer-shell \
    ayatana-appindicator wl-clipboard portaudio
```

### 2. 安装 GNOME AppIndicator 扩展（必需）

GNOME 默认不显示系统托盘图标，需要安装扩展：

1. 安装扩展（从 GNOME Shell Extensions 网站）：
   - 访问: https://extensions.gnome.org/extension/615/appindicator-support/
   - 或使用命令行：
     ```bash
     sudo apt install gnome-shell-extension-appindicator
     ```

2. 启用扩展：
   - 打开 "Extensions" (扩展) 应用
   - 启用 "AppIndicator and KStatusNotifierItem Support"

### 3. 安装 Python 依赖

```bash
cd client_linux
pip3 install -r requirements.txt
```

### 4. 配置设备权限

VoiceTyper 需要访问键盘设备和虚拟输入设备，需要配置 udev 规则：

```bash
# 安装 udev 规则
make install-udev

# 或手动安装：
sudo cp 99-voicetyper-input.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo usermod -aG input $USER
```

**重要**: 注销并重新登录以使组权限生效。

### 5. 启动 ASR 服务器

在另一个终端中启动语音识别服务器：

```bash
cd ../server
pip3 install -r requirements.txt
./run.sh
```

### 6. 运行 VoiceTyper

```bash
make run
# 或直接运行
python3 main.py
```

## 使用方法

1. **启动应用**: 运行 `python3 main.py` 或 `make run`
2. **系统托盘**: 应用会在系统托盘显示图标
3. **开始录音**: 按住 `Ctrl+Space` 开始录音
4. **停止录音**: 释放 `Ctrl+Space`，自动识别并插入文本
5. **查看状态**: 系统托盘图标会显示当前状态（就绪/录音/识别）

### 默认热键

- **录音**: `Ctrl + Space`
- **禁用/启用**: 通过系统托盘菜单

## 配置

配置文件位置: `~/.config/voice_typer/config.yaml`

```yaml
# 语音识别服务地址
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: ""  # API 密钥（远程服务器需要）
  llm_recorrect: false  # 是否启用 LLM 修正

# 热键配置
hotkey:
  modifiers:
    - "ctrl"  # 修饰键: ctrl, alt, shift, super
  key: "space"  # 主键

# 用户词库文件
hotword_files:
  - "hotwords.txt"

# UI 配置
ui:
  opacity: 0.85  # 录音指示器透明度
  width: 240     # 录音指示器宽度
  height: 70     # 录音指示器高度
```

### 自定义词库

编辑 `~/.config/voice_typer/hotwords.txt`，每行一个词：

```text
# 专业术语
FunASR
Python
GitHub

# 自定义词汇
你的名字
公司名称
...
```

## 故障排除

### 1. "未检测到键盘设备"

**原因**: 没有访问 `/dev/input/event*` 的权限

**解决**:
```bash
# 确认 udev 规则已安装
ls -l /etc/udev/rules.d/99-voicetyper-input.rules

# 确认用户在 input 组
groups $USER | grep input

# 如果没有，重新安装权限
make install-udev
# 注销并重新登录
```

### 2. "虚拟键盘模拟失败"

**原因**: 无法访问 `/dev/uinput`

**解决**:
```bash
# 检查 uinput 设备权限
ls -l /dev/uinput

# 确保用户在 input 组
groups $USER | grep input

# 重新安装 udev 规则
sudo cp 99-voicetyper-input.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
```

### 3. 系统托盘图标不显示

**原因**: GNOME 未启用 AppIndicator 扩展

**解决**:
1. 安装扩展: `sudo apt install gnome-shell-extension-appindicator`
2. 打开 "Extensions" 应用
3. 启用 "AppIndicator and KStatusNotifierItem Support"

### 4. 录音指示器不显示

**原因**: gtk-layer-shell 未安装或不支持

**解决**:
```bash
# 检查是否安装
dpkg -l | grep gtk-layer-shell

# 如果未安装
sudo apt install gir1.2-gtklayershell-1.0 libgtk-layer-shell-1.0
```

### 5. 文本插入失败

**原因**: wl-clipboard 未安装或不支持 Wayland

**解决**:
```bash
# 检查是否在 Wayland 会话
echo $XDG_SESSION_TYPE  # 应该输出 "wayland"

# 安装 wl-clipboard
sudo apt install wl-clipboard

# 测试
echo "test" | wl-copy
wl-paste  # 应该输出 "test"
```

### 6. 应用无法启动

**查看日志**:
```bash
# 查看最近 100 行日志
make log

# 实时查看日志
make log-f
```

## 已知问题

1. **GNOME 46+**: 需要安装 AppIndicator 扩展才能显示系统托盘图标
2. **多显示器**: 录音指示器可能不会在所有显示器上显示
3. **虚拟机**: 某些虚拟机环境可能无法正确访问输入设备
4. **文本插入**: 部分应用可能不接受模拟的 Ctrl+V，可手动粘贴

## 开发

### 检查依赖

```bash
make check-deps
```

### 清理缓存

```bash
make clean
```

### 项目结构

```
client_linux/
├── main.py                  # 入口程序
├── controller.py            # 核心控制器
├── config.py                # 配置管理
├── recorder.py              # 音频录制
├── asr_client.py            # ASR 服务客户端
├── hotkey_listener.py       # 全局热键 (evdev)
├── text_inserter.py         # 文本插入 (wl-copy + uinput)
├── indicator.py             # 录音指示器 (GTK4 + Layer Shell)
├── systray_ui.py            # 系统托盘 (AppIndicator)
├── requirements.txt         # Python 依赖
├── Makefile                 # 构建脚本
└── README.md                # 本文档
```

## 许可证

GPLv3

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [FunASR 文档](https://github.com/alibaba-damo-academy/FunASR)
- [evdev 文档](https://python-evdev.readthedocs.io/)
- [gtk-layer-shell](https://github.com/wmww/gtk-layer-shell)
