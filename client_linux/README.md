# VoiceTyper Linux Client

[← 返回主项目](../README.md) · [服务端文档](../server/README.md) · [线上协议](../PROTOCOL.md)

Linux Wayland 语音输入客户端，Python + GTK4 + evdev 实现。当前版本 **1.4.1**。

它负责监听热键、录音、把音频 POST 给服务端、把返回的文本粘贴到光标处。识别模型全部跑在[服务端](../server/README.md)。

**本文适合**：Linux 用户和开发者。

---

## ⚠️ 先读这段：非流式模式

Linux 客户端是**纯 HTTP 实现，不支持 WebSocket 流式识别**。服务端必须用 `--no-streaming` 启动：

```bash
voice-typer-server --no-streaming
```

原因是两种模式注册的路由不同——服务端跑在默认的流式模式下时根本没有 `POST /recognize` 这个端点，请求会 404。

这带来两个体验差异（相对 macOS / Windows 原生客户端）：

- **录音时没有实时预览**。浮窗只显示录音时长，文字要等松手识别完才一次性出现。
- **松手后的等待略长**。整段音频在松手后才开始上传和识别，而不是边说边传。

这不是配置问题，是客户端尚未实现流式路径。

---

## 目录

- [功能与限制](#功能与限制)
- [系统要求](#系统要求)
- [安装](#安装)
- [使用](#使用)
- [配置](#配置)
- [架构](#架构)
- [关键实现](#关键实现)
- [故障排查](#故障排查)
- [开发](#开发)

---

## 功能与限制

**支持**

- 按住热键录音，松开自动识别并粘贴
- Wayland 原生：evdev 监听按键、`wl-copy` 写剪贴板、uinput 模拟粘贴
- 录音浮窗显示实时时长
- 统计已输入次数与字数
- 与 macOS / Windows 客户端共用同一份配置文件格式

**不支持 / 已知限制**

- **不支持流式识别**（见上文）
- **不支持 HTTPS**：`asr_client.py` 硬编码 `http://`，配置里也没有 `scheme` 字段
- **没有 Esc 取消**：录音开始后只能松开热键让它走完
- **需要 `input` 组权限**：靠 udev 规则拿到 `/dev/input/event*` 和 `/dev/uinput` 的访问权
- **X11 会话下未测试**：`wl-copy` 在纯 X11 会话中不可用
- 只能按住说话，没有按一次开始、再按一次结束的切换模式
- 无系统托盘图标，只有录音时出现的浮窗

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 会话类型 | Wayland（`echo $XDG_SESSION_TYPE` 应输出 `wayland`） |
| 桌面环境 | GNOME 推荐，其他 Wayland 合成器可用但浮窗置顶行为可能不一致 |
| Python | 3.10+ |
| 系统库 | GTK4 + PyGObject introspection、wl-clipboard、PortAudio |
| 权限 | 用户需在 `input` 组 |
| 服务端 | [voice-typer-server](../server/README.md) 以 `--no-streaming` 运行 |

Python 依赖（`requirements.txt`）：`tornado`、`sounddevice`、`numpy<2`、`PyYAML`、`evdev`、`PyGObject`。

---

## 安装

### 1. 系统依赖

**Ubuntu / Debian**

```bash
sudo apt update
sudo apt install -y python3 python3-pip python3-dev build-essential
sudo apt install -y gir1.2-gtk-4.0 libgtk-4-1      # GTK4
sudo apt install -y wl-clipboard                    # Wayland 剪贴板
sudo apt install -y libportaudio2 portaudio19-dev   # 音频
```

**Fedora / RHEL**

```bash
sudo dnf install python3 python3-devel python3-pip \
    gtk4 wl-clipboard portaudio-devel
```

**Arch Linux**

```bash
sudo pacman -S python python-pip gtk4 wl-clipboard portaudio
```

> `PyGObject` 需要编译，所以 `python3-dev` / `python3-devel` 和 `build-essential` 不能省。

### 2. Python 依赖

```bash
cd client_linux
make install          # 等价于 pip3 install -r requirements.txt
```

### 3. 设备权限（必做）

客户端需要**读** `/dev/input/event*`（监听热键）和**写** `/dev/uinput`（模拟 Ctrl+V）。两者都通过 `input` 组授权：

```bash
make install-udev
```

这一步做三件事：把 `99-voicetyper-input.rules` 复制到 `/etc/udev/rules.d/`、`udevadm control --reload-rules`、把当前用户加进 `input` 组。

手动等价操作：

```bash
sudo cp 99-voicetyper-input.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo usermod -aG input $USER
```

**必须注销并重新登录**，组权限才会生效。`newgrp input` 只对当前 shell 生效，图形会话里启动的程序拿不到。

规则内容：

```
KERNEL=="event*", SUBSYSTEM=="input", GROUP="input", MODE="0660"
KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"
```

> 这会让 `input` 组的成员能读取**所有**输入设备的事件流——也就是能记录全局按键。这是全局热键在 Wayland 下的固有代价：Wayland 有意不提供全局按键监听 API，绕开它就只能下到 evdev 层。请自行评估这个权限范围是否可接受。

### 4. 启动服务端

```bash
curl -O -L https://github.com/oyasmi/voice-typer/raw/refs/heads/master/server/scripts/voice_typer_server.sh
bash ./voice_typer_server.sh setup
bash ./voice_typer_server.sh run --no-streaming

# 或已安装了 voice-typer-server
voice-typer-server --no-streaming
```

### 5. 运行客户端

```bash
make run
# 或
python3 main.py
```

### 6. 验证

```bash
make check-deps
```

会逐项检查 python3、pip3、wl-clipboard、GTK4 绑定，以及当前用户是否在 `input` 组。

---

## 使用

1. 运行 `make run`，等待日志打印启动完成。
2. **按住 `Ctrl + F2`**（默认热键）开始录音，浮窗出现并显示时长。
3. **松开**，客户端 POST 整段音频，识别完成后文本粘贴到当前光标位置。

录音不足 **0.3 秒**（4800 个 16kHz 采样点）会被丢弃，状态显示「录音过短」，不发出任何请求。

状态提示：`就绪` → `录音中...` → `识别中...` → `已输入 (N字)`，1.5 秒后回到 `就绪`。识别不出内容时显示 `未识别到文字`。

---

## 配置

路径：`~/.config/voice_typer/config.yaml`（遵循 XDG，设了 `XDG_CONFIG_HOME` 则用它）。首次运行自动生成。

```yaml
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0        # 秒
  api_key: ""          # 远程服务端且服务端配了 --api-keys 时必填
  llm_recorrect: true  # 客户端侧开关，服务端也得配好 LLM 才生效

hotkey:
  modifiers:
    - "ctrl"           # ctrl / alt / shift / super（别名：control、option、meta、cmd）
  key: "f2"            # 单个字母、数字，或 evdev 认识的键名（space、f1-f12、up…）

ui:
  opacity: 0.85        # 0.0–1.0，浮窗不透明度
  width: 240           # 浮窗宽度（像素）
  height: 70           # 浮窗高度
```

**没有 `streaming` 字段**——本客户端只有 HTTP 路径。**也没有 `scheme` 字段**——硬编码 `http://`。

配置项会做基本校验，非法值直接抛异常并附上说明：端口须在 1–65535，超时须为正数，不透明度须在 0.0–1.0，宽高须为正数。

主键解析顺序：先查内置特殊键表 → 单个字母映射 `KEY_A`–`KEY_Z` → 单个数字映射 `KEY_0`–`KEY_9` → 最后尝试 `KEY_<大写名>`。所以 evdev 认识的键名基本都能直接写。

---

## 架构

```
client_linux/
├── main.py                  入口：GTK 应用循环 + 后台异步初始化 + 信号处理
├── controller.py            核心控制器：热键回调 → 录音 → 识别 → 插入，统计
├── config.py                YAML 配置读写 + 校验 + XDG 路径
├── recorder.py              sounddevice 采集（16kHz / mono / float32）
├── asr_client.py            HTTP 客户端（tornado HTTPClient）
├── hotkey_listener.py       evdev 全局热键
├── text_inserter.py         wl-copy 写剪贴板 + uinput 模拟 Ctrl+V
├── indicator.py             GTK4 录音浮窗
├── 99-voicetyper-input.rules  udev 规则
├── requirements.txt
└── Makefile
```

### 流程

```
按住热键                             松开热键
   │                                    │
   ▼                                    ▼
浮窗显示 ──▶ sounddevice 采集 ──▶ 停止采集，拿到整段 float32
                                        │
                                 长度 > 4800 样本？
                                    ├── 否 ──▶ 「录音过短」，结束
                                    └── 是
                                        ▼
                        后台线程：POST /recognize (octet-stream)
                                        │
                                        ▼
                        wl-copy 写剪贴板 ──▶ uinput 模拟 Ctrl+V ──▶ 恢复剪贴板
```

识别和插入跑在后台线程，GTK 主循环不会被阻塞。录音状态用锁保护，避免热键抖动导致重入。

---

## 关键实现

### 全局热键：为什么是 evdev

Wayland 有意不提供全局按键监听 API（这正是它相对 X11 的安全改进之一）。合成器各自的快捷键接口不统一，也无法表达「按住 / 松开」这种需要按键抬起事件的语义。所以只能下到内核的 evdev 层，直接读 `/dev/input/event*` 的原始事件流。

实现细节：

- 扫描 `/dev/input/event*`，筛出带按键能力的设备，**同时监听所有键盘**（外接键盘、笔记本内置键盘可能是不同设备）。
- 自己维护按下键集合和修饰键状态，通过 `EV_KEY` 事件的 value（0=抬起 / 1=按下 / 2=重复）判断。
- 修饰键做归一化：`control`→`ctrl`、`option`→`alt`、`meta`/`cmd`/`command`→`super`。
- 不 grab 设备，所以热键组合仍会传给前台应用。

### 文本插入

Wayland 下没有跨应用的直接文本注入通道，只能走剪贴板 + 模拟粘贴：

1. `wl-paste` 读出当前剪贴板并备份；
2. `wl-copy` 写入识别文本（2 秒超时，检查返回码）；
3. 等 **80ms** 让剪贴板就绪；
4. 通过 uinput 虚拟键盘发 `Ctrl 按下 → V 按下 → V 抬起 → Ctrl 抬起`；
5. 等 30ms；
6. **500ms 后无条件恢复**原剪贴板内容。

> 注意第 6 步是**无条件**的：如果你在这 500ms 内复制了别的东西，会被旧内容覆盖掉。macOS / Windows 客户端在恢复前会检查剪贴板是否仍是自己写入的内容，Linux 客户端目前没有这道检查。

uinput 虚拟设备在首次插入时惰性创建，之后复用。

### 音频采集

`sounddevice` 以 16kHz / mono / float32 采集，回调把数据块累积到列表，停止时拼接成一整段 numpy 数组。没有分帧发送逻辑——非流式路径不需要。

### 请求格式

```
POST /recognize?llm_recorrect=true|false
Content-Type: application/octet-stream
Authorization: Bearer <api_key>   # 仅当配置了 api_key

<16kHz float32 mono 裸 PCM>
```

响应里取 `text` 字段。详见 [`PROTOCOL.md`](../PROTOCOL.md) §3。

---

## 故障排查

### 「未检测到键盘设备」

没有 `/dev/input/event*` 的读权限。

```bash
# 确认 udev 规则已安装
ls -l /etc/udev/rules.d/99-voicetyper-input.rules

# 确认用户在 input 组（注意：要在新登录的会话里查）
groups $USER | grep input

# 设备权限应为 crw-rw---- root input
ls -l /dev/input/event0
```

修复：`make install-udev`，然后**注销重新登录**。

### 「虚拟键盘模拟失败」/ 无法访问 `/dev/uinput`

```bash
ls -l /dev/uinput           # 应为 crw-rw---- root input
lsmod | grep uinput         # 模块没加载的话：sudo modprobe uinput
```

有些发行版默认不加载 `uinput` 模块，需要写进 `/etc/modules-load.d/`：

```bash
echo uinput | sudo tee /etc/modules-load.d/uinput.conf
```

### 文本插入失败

```bash
echo $XDG_SESSION_TYPE      # 应输出 wayland
which wl-copy               # 没有就装 wl-clipboard

echo test | wl-copy && wl-paste   # 应输出 test
```

如果目标程序不接受模拟的 `Ctrl+V`（部分终端要 `Ctrl+Shift+V`、部分 Electron 应用），文本仍在剪贴板里，手动粘贴即可。

### 识别请求返回 404

服务端跑在流式模式下，没有 `/recognize` 路由。重启服务端并加 `--no-streaming`。

### 识别请求返回 401

服务端配了 `--api-keys` 且监听的不是 `127.0.0.1`。在配置文件里填上 `api_key`。

### 浮窗不置顶 / 位置不对

部分 Wayland 合成器不允许普通窗口自行置顶。这是已知限制，GNOME 下表现最正常。

### 看日志

日志同时输出到终端和文件。终端只有消息正文，文件带时间戳。

```bash
make log      # 最近 100 行
make log-f    # 实时跟随
```

文件路径 `~/.config/voice_typer/app.log`，2MB 滚动，保留 3 份备份。配置目录不可写时会退化成只输出到终端，并在 stderr 打一条警告。

> `make log` / `make log-f` 里的路径是写死的。如果你设了 `XDG_CONFIG_HOME`，日志会跟着配置目录走，这两个 target 就读不到，需要直接 `tail` 实际路径。

### 录音一直是空的

检查默认输入设备：

```bash
python3 -c "import sounddevice; print(sounddevice.query_devices())"
```

PipeWire / PulseAudio 下默认设备可能指向了错误的源，用 `pavucontrol` 调整。

---

## 开发

```bash
make check-deps    # 检查依赖与权限
make run           # 开发模式运行
make clean         # 清理 __pycache__ 与 .pyc
make help          # 列出所有 target
```

版本号在两处，改的时候要同步：`config.py` 的 `APP_VERSION` 和 `Makefile` 的 `VERSION`。

### 可以做的改进

- **流式支持**：接 WebSocket `/recognize/stream`，就能拿到实时预览并去掉 `--no-streaming` 的要求。协议见 [`PROTOCOL.md`](../PROTOCOL.md) §4，可参考 `client_windows_native/Services/StreamingASRClient.cs`。
- **剪贴板恢复加保护**：恢复前先比对内容，避免覆盖用户这期间的复制操作。
- **`scheme` 配置项**：让 `asr_client.py` 支持 https，与 macOS 客户端对齐。
- **让 `make log` 跟随 `XDG_CONFIG_HOME`**：Makefile 里的日志路径目前写死为 `~/.config/voice_typer/app.log`。

---

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [服务端文档](../server/README.md)
- [客户端 ↔ 服务端协议](../PROTOCOL.md)
- [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) / [FunASR](https://github.com/alibaba-damo-academy/FunASR)
- [python-evdev 文档](https://python-evdev.readthedocs.io/)
