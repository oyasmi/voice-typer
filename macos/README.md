# VoiceTyper — 一体化 macOS App

[← 返回主项目](../README.md) · [设计方案](DESIGN.md) · [分体式客户端](../client-server/client_macos_swift/README.md)

单进程 macOS 菜单栏应用：把 [`client-server/server/`](../client-server/server/README.md) 的 SenseVoice 识别链路用 Swift 重写并
内联进客户端，拖进「应用程序」打开即用，**不需要**单独部署 Python 服务端。当前版本 **3.2.0**，
应用名 **VoiceTyper**，Bundle ID `com.voicetyper.app`。

**本文适合**：想在自己 Mac 上直接用的用户，以及要自行编译或二次开发的人。深入的架构决策、
实测数据、逐项取舍见 [`DESIGN.md`](DESIGN.md)。

---

## 目录

- [功能与限制](#功能与限制)
- [系统要求](#系统要求)
- [安装](#安装)
- [权限与模型下载](#权限与模型下载)
- [使用](#使用)
- [设置](#设置)
- [架构](#架构)
- [构建](#构建)
- [测试](#测试)
- [日志与排障](#日志与排障)

---

## 功能与限制

**支持**

- 单进程运行，识别引擎（SenseVoice-Small）直接跑在 App 内，不连接任何服务端
- 首次启动自动下载并部署一次模型（约 240MB），此后完全离线
- 按住热键（默认 `Fn`）录音，松开自动识别并插入文本；录音中按 `Esc` 取消
- 流式实时预览：录音时 HUD 浮窗持续显示识别文本，且会自我修正
- 可选的 LLM 智能校对，配置项直接在设置面板里（Base URL / API Key / 模型 / 温度 / 超时）
- 识别语言可指定为自动 / 中文 / 英文 / 粤语 / 日语 / 韩语
- 空闲一段时间后自动释放识别引擎内存，下次按热键与录音并行自动重新加载
- 开机自启、HUD 不透明度可调
- 菜单栏可暂停听写

**不支持 / 已知限制**

- 只支持 **Apple Silicon**（arm64），不支持 Intel Mac
- 只能按住说话，没有「按一次开始、再按一次结束」的切换模式
- 热键主键限于字母、数字、`space`/`tab`/`enter`、`F1`–`F12`，以及独立的 `fn`
- 应用未做签名公证，首次打开需在「系统设置 → 隐私与安全性」手动放行
- 只支持 SenseVoice-Small 模型，不支持更换为 paraformer；热词在两种形态里都已移除
  （如需 paraformer，用[分体式客户端](../client-server/client_macos_swift/README.md) +
  [服务端](../client-server/server/README.md)）
- 不支持远程/共享服务端——识别永远在本机跑

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 系统 | macOS 14.0 (Sonoma) 或更高 |
| 架构 | **仅 Apple Silicon**（M 系列芯片） |
| 磁盘 | 约 300MB（App 本体 + 首次下载的模型，留有余量） |
| 网络 | 仅首次启动下载模型时需要；此后完全离线 |
| Xcode | 仅自行构建时需要 |

---

## 安装

### 从 Release 安装

1. 从 [Release](https://github.com/oyasmi/voice-typer/releases) 下载 `VoiceTyper-<版本>-macOS-arm64.dmg`。
2. 打开 DMG，把 `VoiceTyper.app` 拖进「应用程序」。
3. 从「应用程序」打开 VoiceTyper。
4. 首次打开若被系统拦截，到「系统设置 → 隐私与安全性」点「仍要打开」。

第 4 步是因为应用只做了 ad-hoc 签名，没有 Apple 开发者签名和公证。DMG 里附了一份精简的
`INSTALL.txt` 说明同样的流程。

### 卸载

删掉 `/Applications/VoiceTyper.app`，再删配置与模型目录
`~/Library/Application Support/VoiceTyper/`。权限授予记录留在系统设置里，可手动移除。

### 和分体式客户端共存吗？

Bundle ID（`com.voicetyper.app` vs `com.voicetyper.client`）与配置目录都是隔离的，技术上可以
同时安装。但两者都会注册同一个默认热键（`Fn`），**同时运行会抢热键**，建议只保留一个。
若之前装过 [`client-server/client_macos_swift/` 的 VoiceTyperClient](../client-server/client_macos_swift/README.md)，
迁移过来时热键与 HUD 不透明度会在首次启动时自动继承。

---

## 权限与模型下载

首次启动有两条独立的准备工作，互不阻塞：

1. **三项系统权限**（麦克风、辅助功能、输入监控）——缺一不可，应用会自动弹出设置窗口的
   「权限」页引导授权。
2. **自动准备语音模型**——应用会在后台自动下载、校验并加载；权限没给全时也会继续进行。用户首次安装时只需完成系统授权，两条准备线都就绪后即可使用。

| 权限 | 用途 | 缺失时的表现 |
| --- | --- | --- |
| **麦克风** | 录音 | 按下热键无法开始录音 |
| **辅助功能** | 文本插入（AX 直写 + 模拟粘贴） | 识别成功但文字插不进去 |
| **输入监控** | 全局热键，尤其是 Fn 键 | 热键完全无响应 |

模型下载在设置窗口的「识别」页可见：显示百分比进度，支持取消；自动下载失败后可在此手动重试。下载的四个文件
（`config.yaml`、`am.mvn`、`tokens.json`、`model_quant.onnx`）来自 ModelScope，逐个校验
sha256，支持断点续传。如果这台机器之前跑过 [`client-server/server/`](../client-server/server/README.md)（`~/.cache/modelscope/`
下已有模型缓存），App 会自动复用，无需重新下载。

彻底重置某项权限：

```bash
tccutil reset Microphone com.voicetyper.app
tccutil reset Accessibility com.voicetyper.app
tccutil reset ListenEvent com.voicetyper.app
```

---

## 使用

1. 启动后菜单栏出现 VoiceTyper 图标。
2. **按住热键**（默认 `Fn` / 地球仪键）开始录音，HUD 浮窗出现。
3. 说话。HUD 会实时显示识别文本，并随着你继续说而自我修正。
4. **松开热键**，本地引擎做一次整段复识别，最终文本插入当前光标位置。
5. 录音中随时按 **Esc** 取消，不会识别也不会插入。

录音不足 **0.3 秒**视为误触，直接丢弃。

菜单栏图标状态：

| 状态 | 图标 | 含义 |
| --- | --- | --- |
| 就绪 | `mic` | 等待热键 |
| 录音中 | `mic.fill` | 正在录音 |
| 识别中 | `waveform` | 已松手，等本地引擎返回 |
| 输入中 | `character.cursor.ibeam` | 正在插入文本 |
| 需要下载模型 | 向下箭头（橙） | 首次启动，模型尚未下载 |
| 下载中 | 向下箭头 + 动效 | 正在下载模型 |
| 模型加载中 | 旋转箭头 | 权限已就绪，正在把模型载入内存 |
| 需要授权 | 警告三角 | 有权限未授予 |
| 已暂停 | `mic.slash` | 从菜单主动暂停 |

---

## 设置

设置窗口分三页，全部 UI 化，不需要手工编辑 YAML：

| 页 | 内容 |
| --- | --- |
| **权限** | 三项权限状态、授权按钮、跳转系统设置 |
| **识别** | 模型状态卡片（自动下载/加载/就绪/失败 + 重试/重新加载）、空闲多久后卸载模型、识别语言、智能校对（开关 + Base URL + API Key + 模型 + 温度 + 超时 + 测试校对） |
| **通用** | 热键（Fn 或组合键，支持按键录制）、开机自启、HUD 背景不透明度 |

配置文件：

```
~/Library/Application Support/VoiceTyper/config.yaml
```

完整字段：

```yaml
asr:
  language: "auto"          # auto / zh / en / yue / ja / ko
  threads: 0                 # 0 = 自动（min(4, 核数)）
  model_dir: ""               # 留空 = 自动定位（下载目录 / ModelScope 缓存）
  idle_unload_minutes: 10     # 0 = 常驻不卸载
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key 不在这里，存在 Keychain（service com.voicetyper.app, account llm_api_key）
hotkey:
  modifiers: []
  key: "fn"
ui:
  opacity: 0.85
```

LLM API Key 出于安全考虑不落配置文件，存在系统 Keychain 里。

---

## 架构

```
VoiceTyperController（状态机，Idle→Recording→Recognizing→Inserting）
  ├── HotkeyService / AudioCaptureService / TextInsertionService
  └── LocalASRSession（进程内识别会话，取代旧架构里跨进程的 WebSocket 客户端）
         └── ASRService（asrQueue 串行队列）
                └── SenseVoiceEngine
                       ├── FbankFrontend（Accelerate/vDSP，Kaldi 兼容 fbank）
                       ├── LFRCMVN
                       ├── ORTSession（onnxruntime-swift-package-manager）
                       └── CTCDecoder + TextPostprocessor
```

识别管线（fbank → LFR/CMVN → CTC 解码）是 `client-server/server/voice_typer_server/recognizer.py`
的 Swift 移植，逐点数值对齐用金标准测试持续验证（见 `Tests/VoiceTyperTests/Fixtures/`）；
其余模块是本地一体化架构下的独立实现，不再与旧的分体式客户端共享代码或协议。

完整的设计决策、实测数据（性能、内存、模型 I/O 契约）、模块职责映射见 [`DESIGN.md`](DESIGN.md)。

---

## 构建

### Xcode

```bash
cd macos
ruby scripts/generate_xcodeproj.rb   # 生成/重新生成工程（新增文件后需要重跑）
open VoiceTyper.xcodeproj
```

依赖：[Yams](https://github.com/jpsim/Yams)（YAML 解析）、
[onnxruntime-swift-package-manager](https://github.com/microsoft/onnxruntime-swift-package-manager)
（精确锁定 `1.24.2`），均走 SwiftPM。

### 命令行

```bash
cd macos
./build_xcode.sh
```

只出 **arm64** 一个变体（不支持 Intel Mac）。产物在 `dist/`：

```
VoiceTyper-<版本>-macOS-arm64.zip / .dmg
```

模型不随构建产物打包，首次启动时应用会自动下载并部署。如需离线预置模型用于测试或跳过自动下载：

```bash
./scripts/fetch_model.sh
```

### 签名与公证（可选）

默认（不设置任何环境变量）构建产物是 **ad-hoc 本机签名**，这也是绝大多数贡献者应该使用的路径。
ad-hoc 签名有两个已知代价：

- **每次更新都可能需要用户重新授权**麦克风/辅助功能/输入监控三项权限——ad-hoc 签名没有稳定的
  Team ID，cdhash 每次构建都变，TCC 记录以代码签名标识为键。
- 无法通过公证分发，用户首次打开需要在「系统设置」里手动放行（"仍要打开"）。

若已有付费 Apple Developer 账号，可通过环境变量启用 **Developer ID 签名 + 强化运行时 + 公证**：

```bash
# 1. 查看本机可用的 Developer ID 签名身份
security find-identity -v -p codesigning

# 2.（仅需一次）把公证凭据存进本机 Keychain，之后无需再输入密码
xcrun notarytool store-credentials "voicetyper-notary" \
  --apple-id "you@example.com" --team-id "TEAMID1234" --password "应用专用密码"

# 3. 签名 + 公证 + 装订一次跑完
VOICETYPER_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID1234)" \
VOICETYPER_NOTARY_PROFILE="voicetyper-notary" \
./build_xcode.sh
```

只设置 `VOICETYPER_SIGN_IDENTITY`（不设置 `VOICETYPER_NOTARY_PROFILE`）则只做 Developer ID 签名、
不提交公证——适合先本地验证签名是否正常，再决定要不要公证。

实现要点（详见 `build_xcode.sh` 注释）：内嵌的 `onnxruntime.framework` 会与主程序用同一身份分别
签名（强化运行时 `--options runtime`），而不是用 `--deep` 笼统签一次——这是 Apple 官方公证指南的
建议做法，也是公证失败最常见原因之一。工程已在 `ENABLE_HARDENED_RUNTIME=YES` 下验证过可以正常
编译、运行、通过全部单元测试（含真实 ONNX 推理），但**实际的 Developer ID 签名与公证流程本身
未经真机验证**——本仓库的自动化环境没有付费开发者账号与可用凭据，这一步需要持有账号的维护者
走通一次后更新本节状态。

自动更新（Sparkle 等）不在本次范围内：证书能解决"重装即用"，但持续的版本检测/推送需要额外的
发布基础设施（appcast 托管、签名密钥管理），留待后续按实际分发需求决定。

### 改版本号

`scripts/generate_xcodeproj.rb` 里的 `MARKETING_VERSION`（两处：`build_configuration_list` 与
per-target 配置）。

---

## 测试

```bash
xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper -destination 'platform=macOS' test
```

| 测试 | 内容 |
| --- | --- |
| `FbankParityTests` | fbank / LFR+CMVN 特征逐点比对 `client-server/server/` 产出的金标准（阈值 1e-3），含全零输入的对数下限回归 |
| `EndToEndRecognitionTests` | 完整识别链路对真实语音样本的验收（编辑距离容忍度，见下方说明） |
| `LFRCMVNTests` | LFR 尾帧补齐分支与 CMVN 逐元素仿射的结构校验（纯逻辑，不需要模型） |
| `TextPostprocessorTests` | CTC 解码后文本清洗的各条规则 |
| `RecognitionBufferTests` | 滑窗预览调度逻辑（用假引擎，不依赖真实模型） |
| `AudioChunkerTests` | 音频定长分帧、跨调用累积余量、尾音刷出（纯逻辑，不依赖麦克风） |
| `LocalASRSessionTests` | 会话层行为：预览重入跳过、finalize 看门狗、120 秒上限一次性告警并触发收尾、close 后抑制迟到回调 |
| `ASRServiceTests` | 引擎加载互斥、空闲卸载不反向触发自动预加载、加载期间的语言变更重放到新引擎 |
| `VoiceTyperControllerTests` | 状态机行为：识别中重叠按键拒绝而非覆盖、Esc 取消、设备变化后仍正常收尾、短录音丢弃等 |
| `AppCoordinatorReadinessTests` | 就绪编排的状态优先级（暂停 > 权限缺失 > 模型下载 > 引擎状态） |
| `ConfigStoreTests` | YAML 读写往返、缺字段回落默认 |
| `ConfigMigratorTests` | 老配置迁移只继承 hotkey 与 HUD 不透明度 |
| `AppConfigTests` | 配置校验：越界夹逼、NaN/Inf 重置、非 fn 裸键回落默认热键 |
| `LLMCorrectorTests` | 校对客户端的失败兜底（网络错误/截断/格式错误都要原样返回原文） |
| `LLMEndpointTests` | 校对 Base URL 的结构化解析（scheme/host 白名单、明文 HTTP 限回环私网、`/chat/completions` 后缀去重） |
| `ModelDownloaderTests` | sha256 校验、文件清单自洽性、下载顺序与单次自动触发策略 |
| `TextInsertionServiceTests` | AX 插入范围合法性判定（负值、越界、溢出一律拒绝） |

除前两组金标准测试外，其余都是纯逻辑单元测试，不需要模型或网络。

`FbankParityTests` / `EndToEndRecognitionTests` 依赖本机已有模型（`ModelLocator` 任一优先级命中
即可），缺失时自动 `XCTSkip`。夹具由 `scripts/dump_reference_fixtures.py` 生成（需要 `client-server/server/`
的 Python 环境）。

`EndToEndRecognitionTests` 用编辑距离而非逐字节相等做验收：已定位到 Python 与 macOS 用的是两套
独立编译的 ONNX Runtime 二进制，在个别真正模棱两可的 token 上可能因浮点求和顺序不同而翻转
识别结果（例如英文单词的大小写），这是良性的跨平台浮点非确定性，不是逻辑 bug。详见
[`DESIGN.md`](DESIGN.md) §8 的实测结论。

---

## 日志与排障

日志走统一日志系统（`os.Logger`），不落地为独立的 `.log` 文件，subsystem 为
`com.voicetyper.app`，分 `app` / `permissions` / `hotkey` / `audio` / `asr` / `llm` /
`model` 几个 category。

**命令行**（`log` 是系统自带工具）：

```bash
# 实时跟踪
log stream --predicate 'subsystem == "com.voicetyper.app"' --level debug
log stream --predicate 'subsystem == "com.voicetyper.app" AND category == "asr"'

# 查看历史（过去 10 分钟）
log show --predicate 'subsystem == "com.voicetyper.app"' --last 10m
```

**图形界面**：打开「控制台」（Console.app，`/Applications/Utilities/`），左侧选中本机设备，
搜索框输入 `com.voicetyper.app` 过滤即可；也可以按 category 进一步筛选（如 `llm`、`hotkey`）。

### 模型下载失败

检查网络能否访问 ModelScope；下载支持断点续传，可在「识别」页重试。若反复失败，可用
`scripts/fetch_model.sh` 手动下载到默认模型目录。

### 热键完全没反应

参见[分体式客户端文档的对应章节](../client-server/client_macos_swift/README.md#热键完全没反应)——`CGEventTap`
实现完全一致，排查方式相同（换成本应用的 Bundle ID `com.voicetyper.app`）。

### 识别成功但文字没插进去

同上，参见[分体式客户端文档](../client-server/client_macos_swift/README.md#识别成功但文字没插进去)，文本插入
实现代码原样搬运，行为一致。

---

## 相关链接

- [VoiceTyper 主项目](../README.md)
- [设计方案](DESIGN.md)
- [分体式客户端（多设备共享服务端场景）](../client-server/client_macos_swift/README.md)
- [服务端](../client-server/server/README.md)（本 App 不使用，但识别管线移植自此）
