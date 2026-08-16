# VoiceTyper

**按住一个键说话，松开，文字就出现在光标处。**

任何能打字的地方都能用——聊天窗口、浏览器、代码编辑器、备忘录。语音不上传，全程在你自己的机器上识别。

支持 macOS、Windows、Linux。

---

## 为什么用它

**说话比打字快。** 中文口述大约每分钟 200 字，打字通常在 60 字上下。写长消息、记想法、写提交信息的时候，这个差距很明显。

**你的声音不出本机。** 识别跑在本地，不联网也能用。市面上大多数语音输入要么走云端，要么绑定某个输入法。

**它不挑应用。** 不是输入法，也不是某个应用的插件，而是一个后台常驻的小工具。哪里能粘贴文字，它就能在哪里输入。

---

## 特点

**说话时就能看到文字**
按住热键的同时，识别结果实时显示在屏幕上的浮窗里。而且它会自我修正——先听成「识别功能和并」，多说两个字后自动改成「识别功能合并」。松手时最终结果和你看到的一致，不会整段跳变。
*（macOS 与 Windows 客户端）*

**标点自己会加**
说完不用手动补标点。数字也会转成阿拉伯形式：说「六十四兆」，出来的是「64兆」。

**中英粤日韩，不用切换**
一个模型全覆盖，默认自动判断语种。中英夹杂的句子也能正常处理。

**不上传，也不需要账号**
没有注册、没有 API 额度、没有联网检查。装好就一直能用。

**可以接大模型润色（可选）**
觉得同音字、口语词还不够干净，可以接一个 OpenAI 兼容的模型做二次纠错。这一步会把**识别出的文字**（不含音频）发给你自己配置的服务；不想联网的话，指向本机的 Ollama 也行。**默认关闭。**

**误触不会打扰你**
0.3 秒以下的录音自动丢弃，碰一下热键不会莫名其妙冒出文字。

---

## 它由两部分组成

```
   你的电脑                                  识别服务
┌──────────────┐                        ┌──────────────┐
│   客户端     │  ──── 音频 ────▶       │   服务端     │
│              │                        │              │
│ 监听热键     │  ◀──── 文字 ────       │  语音模型    │
│ 录音         │                        │              │
│ 显示浮窗     │                        │              │
│ 插入文字     │                        │              │
└──────────────┘                        └──────────────┘
```

- **客户端**：你看得见的那部分。菜单栏 / 托盘里的小图标，负责录音和上屏。
- **服务端**：识别引擎。**默认就装在你自己的电脑上**，和客户端一起跑，不需要另一台机器。

之所以拆成两半，是因为这样你也可以把服务端放到一台性能更好的机器上（比如家里带显卡的台式机），几台设备共用。但如果你只是想在自己电脑上用，两个都装在本机就行。

所以安装分两步：**先装服务端，再装客户端。**

---

## 第一步：安装服务端

需要 Python 3.10 或更高版本。首次启动会自动下载语音模型（约 241MB），耐心等一会儿。

### 方式一：让 AI Agent 帮你装（推荐）

如果你在用 Claude Code、Cursor、OpenCode 之类的 AI 编程助手，把下面整段复制给它就行：

````text
请帮我安装 VoiceTyper 语音识别服务端（voice-typer-server）。这是一个 Python 包，
安装完成后在本机提供离线语音识别服务。请按以下步骤操作：

【第 0 步】先问我：我用的是哪个平台的客户端？
  A. macOS   B. Windows   C. Linux
  这决定了后面的启动参数，必须先问清楚，不要替我猜。

【第 1 步】找到可用的 Python 3.10+ 解释器
  - 先看 python3 --version，如果 >= 3.10 就用它
  - 否则依次检查 PATH 里是否有 python3.10 / 3.11 / 3.12 / 3.13 / 3.14
  - 都找不到就停下，告诉我需要先装 Python 3.10+，并按我的操作系统给出安装建议

【第 2 步】准备虚拟环境 ~/.venvs/voice-typer
  - 已存在且 Python >= 3.10：问我是否复用（默认复用），复用则跳到第 3 步
  - 已存在但 Python < 3.10：告诉我需要重建，删掉后重新创建
  - 不存在：用第 1 步找到的 Python 创建
    <找到的python> -m venv ~/.venvs/voice-typer

【第 3 步】安装
  ~/.venvs/voice-typer/bin/pip install --upgrade pip setuptools wheel
  ~/.venvs/voice-typer/bin/pip install --upgrade voice-typer-server
  （Windows 下路径是 ~/.venvs/voice-typer/Scripts/）

【第 4 步】问我是否需要 LLM 智能纠错
  - 需要的话向我索取三个参数：--llm-base-url、--llm-api-key、--llm-model
  - 不需要就跳过，不要自己编造参数

【第 5 步】问我是否现在启动服务，如果是就启动
  启动命令按第 0 步的答案选择：
  - macOS / Windows 客户端（流式，默认）：
      ~/.venvs/voice-typer/bin/voice-typer-server
  - Linux 客户端（必须加 --no-streaming）：
      ~/.venvs/voice-typer/bin/voice-typer-server --no-streaming
  后台运行加 nohup ... & 即可。
  首次启动要下载约 241MB 的模型，可能耗时几分钟，属正常现象。

【第 6 步】验证并汇报
  等服务起来后执行：curl http://127.0.0.1:6008/health
  把返回的 JSON 念给我，并确认 "ready" 是否为 true。
  如果连不上，检查进程是否还在跑、日志里有没有报错，把结论告诉我。
  最后把完整的启动命令留给我，方便我下次自己启动。
````

### 方式二：自己跑脚本

```bash
curl -O -L https://github.com/oyasmi/voice-typer/raw/refs/heads/master/server/scripts/voice_typer_server.sh
bash ./voice_typer_server.sh setup
```

然后启动。**启动命令取决于你用哪个客户端**：

```bash
# macOS / Windows 客户端
bash ./voice_typer_server.sh run

# Linux 客户端（必须加 --no-streaming）
bash ./voice_typer_server.sh run --no-streaming
```

看到 `服务已启动` 就成功了。验证一下：

```bash
curl http://127.0.0.1:6008/health
```

返回里 `"ready": true` 表示模型加载完毕，可以用了。

> 想让服务端开机自启、跑在 Docker 里、注册成 Windows 服务，或者接显卡加速？见 [服务端文档](server/README.md)。

---

## 第二步：安装客户端

### macOS

- **系统要求**：macOS 14.0 (Sonoma) 或更高，Apple Silicon 和 Intel 都支持
- **下载**：从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper-<版本>-macOS-<架构>.dmg`。拿不准就选 `universal`
- **安装**：打开 DMG，把 `VoiceTyper.app` 拖进「应用程序」，然后打开它
- **首次打开被拦截**：到「系统设置 → 隐私与安全性」点「仍要打开」（应用未做 Apple 签名公证）
- **授权**：首次启动会引导你完成三项授权——麦克风、辅助功能、输入监控。缺一项都不能正常工作，应用会自动弹出设置窗口提示
- **默认热键**：`Fn`（地球仪）键。也可以改成组合键
- **取消录音**：录音时按 `Esc`

👉 [macOS 客户端完整文档](client_macos_swift/README.md)

### Windows

- **系统要求**：Windows 10 / 11（x64）
- **下载**：从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载
  - `VoiceTyper-<版本>-win-x64.exe` — **完整版**，下载即用，推荐
  - `VoiceTyper-<版本>-win-x64-portable.exe` — 便携版，体积小但需要先装 [.NET Desktop Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- **安装**：没有安装程序，双击 exe 即可，应用驻留系统托盘
- **授权**：首次录音时 Windows 会询问麦克风权限，允许即可
- **默认热键**：`Ctrl + F2`
- **设置**：单击托盘图标打开

👉 [Windows 客户端完整文档](client_windows_native/README.md)

### Linux

- **系统要求**：Wayland 会话（推荐 GNOME）、Python 3.10+、GTK4、wl-clipboard
- **注意**：服务端必须用 `--no-streaming` 启动，且录音时没有实时预览
- **安装**：

  ```bash
  cd client_linux
  make install        # Python 依赖
  make install-udev   # 输入设备权限，装完需注销重新登录
  make run
  ```

- **默认热键**：`Ctrl + F2`

👉 [Linux 客户端完整文档](client_linux/README.md)

---

## 怎么用

装好之后，日常使用就三个动作：

1. **按住热键**，浮窗出现，开始说话
2. 说话时看着浮窗里的文字（macOS / Windows），它会边听边修正
3. **松开热键**，文字插入到光标位置

几个细节：

- **别松太快**：不到 0.3 秒的录音会被当成误触丢掉
- **录音中按 `Esc` 可以取消**（仅 macOS）
- **可以连着说**：Windows 客户端允许上一段还在识别时就开始下一段
- **服务端要一直开着**：关掉服务端，客户端就用不了了。想省心可以设成开机自启，见[服务端文档](server/README.md)

---

## 想让识别更准？开 LLM 纠错

可选功能。识别完成后再让一个大模型过一遍，修同音字、口语词和标点。

**服务端**启动时带上三个参数：

```bash
voice-typer-server --llm-base-url https://api.openai.com/v1 \
                   --llm-api-key sk-xxx \
                   --llm-model gpt-4o-mini
```

**客户端**在设置里勾上「LLM 纠错」（或在配置文件里设 `llm_recorrect: true`）。两边都开才生效。

代价是松手到上屏会多等一会儿，最多 5 秒（可调）。纠错失败或超时会自动回退到原始识别结果，不会卡住。

想保持完全离线，把 `--llm-base-url` 指向本机的 Ollama 之类的服务即可。

---

## 常见问题

**客户端显示连不上服务端**
先确认服务端还在跑，然后 `curl http://127.0.0.1:6008/health` 看看返回。还要确认客户端的「流式识别」开关和服务端模式一致——服务端加了 `--no-streaming` 的话，客户端也要关掉流式。

**第一次启动特别慢**
在下载语音模型（约 241MB）。只有第一次会这样，之后是秒开。

**macOS 上热键完全没反应**
「输入监控」权限没给，或者更新版本后授权失效了。到「系统设置 → 隐私与安全性 → 输入监控」把 VoiceTyper 移除再加回去，然后完全退出应用重开。

**识别出来了但文字没插进去**
少数应用不接受模拟粘贴（部分终端、密码框）。这种情况下文字还在剪贴板里，手动粘贴一次就行。macOS 用户还要确认「辅助功能」权限已授予。

**识别不太准**
先试试[开 LLM 纠错](#想让识别更准开-llm-纠错)，效果提升通常最明显。说话时离麦克风近一点、语速稳一点也有帮助。

**能识别专有名词吗**
不能定向增强。早期版本有过热词功能，但在当前的识别链路上从未真正生效，已经移除。配置里残留的 `hotword_files` 字段会被忽略，`hotwords.txt` 可以直接删掉。

**支持显卡加速吗**
服务端支持 NVIDIA CUDA（`--device cuda`）。Apple Silicon 上用 CPU 就够快，不需要额外配置。

---

## 深入了解

| 文档 | 内容 |
| --- | --- |
| [服务端](server/README.md) | 参数详解、模型选型、部署（Docker / Windows 服务）、性能调优、接口 |
| [macOS 客户端](client_macos_swift/README.md) | 权限机制、Fn 键实现、文本插入策略、构建 |
| [Windows 客户端](client_windows_native/README.md) | 热键钩子、音频采集、并发会话、构建 |
| [Linux 客户端](client_linux/README.md) | evdev 权限模型、Wayland 限制、故障排查 |
| [通信协议](PROTOCOL.md) | 客户端与服务端的完整契约 |

---

## 致谢

- [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) — 默认使用的多语言语音识别模型，自带标点与 ITN
- [FunASR](https://github.com/alibaba-damo-academy/FunASR) — 阿里达摩院的语音识别工具包，服务端基于其 `funasr-onnx` 运行时
- [NAudio](https://github.com/naudio/NAudio) — Windows 客户端音频采集
- [PyGObject](https://pygobject.readthedocs.io/) / [python-evdev](https://python-evdev.readthedocs.io/) — Linux 客户端 GTK 绑定与输入设备处理
