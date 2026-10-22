# VoiceTyper macOS 开发说明

[← macOS 使用说明](README.md) · [架构设计](DESIGN.md) · [更新日志](CHANGELOG.md)

面向要自行编译、调试或参与开发的人。架构决策、实测数据与逐项取舍见 [`DESIGN.md`](DESIGN.md)。

## 架构概览

```
VoiceTyperController（状态机，Idle→Recording→Recognizing→Inserting）
  ├── HotkeyService / AudioCaptureService / TextInsertionService
  └── LocalASRSession（进程内识别会话）
         └── ASRService（asrQueue 串行队列）
                └── SenseVoiceEngine
                       ├── FbankFrontend（Accelerate/vDSP，Kaldi 兼容 fbank）
                       ├── LFRCMVN
                       ├── ORTSession（onnxruntime-swift-package-manager）
                       ├── CTCDecoder（无词汇时，贪心）
                       ├── Hotword/：HotwordCompiler + ContextGraph + CTCPrefixBeamSearch（有词汇时，热词偏置束搜索）
                       └── TextPostprocessor
VocabularyReplacer（词汇替换层，LocalASRSession 与 OutputFormatter 各调用一次）
```

识别管线（fbank → LFR/CMVN → CTC 解码）移植自
`client-server/server/voice_typer_server/recognizer.py`，由金标准夹具
（`Tests/VoiceTyperTests/Fixtures/`，与 Windows 共用）持续校验数值一致性。

## 构建

依赖：Xcode、Ruby（生成工程用）。SwiftPM 依赖为 [Yams](https://github.com/jpsim/Yams) 与
[onnxruntime-swift-package-manager](https://github.com/microsoft/onnxruntime-swift-package-manager)
（精确锁定 `1.24.2`）。

```bash
cd macos
ruby scripts/generate_xcodeproj.rb   # 新增、删除或移动 Swift 文件后必须重跑
open VoiceTyper.xcodeproj

./build_xcode.sh                     # 命令行出包，产物在 dist/
```

也可在仓库根目录用 `make macos`（Mac 上直接 `make` 即可）、`make macos-project`、`make macos-test`、
`make macos-model`、`make clean-macos`，分别对应构建、生成工程、测试、预置模型与清理。

只出 **arm64** 一个变体，产物为 `VoiceTyper-<版本>-macOS-arm64.dmg / .zip`。DMG 是正式分发物；ZIP 是附带产物，
优先用 `ditto` 打包，`ditto` 失败（例如被安全软件以 `Killed: 9` 终止）时自动改用 `zip -ry`，仍失败则只告警、
只产出 DMG。设置 `VOICETYPER_SKIP_ZIP=1` 可完全跳过 ZIP。模型不随包分发，
首次启动由应用下载；需要离线预置（跑金标准测试、跳过首启下载）时：

```bash
./scripts/fetch_model.sh
```

改版本号：`scripts/generate_xcodeproj.rb` 里的 `MARKETING_VERSION`（两处），改完重新生成工程。

## 签名与公证（可选）

默认构建是 **ad-hoc 本机签名**，代价有两个：

- 系统的权限记录以代码签名为键，ad-hoc 签名每次构建都变，用户**更新后可能要重新授权**三项权限；
- 无法公证，用户首次打开需要在「系统设置 → 隐私与安全性」手动放行。

有付费 Apple Developer 账号时，可启用 Developer ID 签名 + 强化运行时 + 公证：

```bash
security find-identity -v -p codesigning            # 查看可用签名身份

xcrun notarytool store-credentials "voicetyper-notary" \
  --apple-id "you@example.com" --team-id "TEAMID1234" --password "应用专用密码"   # 仅需一次

VOICETYPER_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID1234)" \
VOICETYPER_NOTARY_PROFILE="voicetyper-notary" \
./build_xcode.sh
```

只设置 `VOICETYPER_SIGN_IDENTITY` 则只签名、不公证。内嵌的 `onnxruntime.framework` 与主程序用同一身份
分别签名（不用 `--deep`），这是 Apple 公证指南的建议做法。工程已在 `ENABLE_HARDENED_RUNTIME=YES` 下
验证可正常编译、运行和通过测试，但**Developer ID 签名与公证流程本身尚未实际走通**，需要持有账号的维护者
跑一次后更新本节。

## 测试

```bash
xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper -destination 'platform=macOS' test
```

- 绝大多数用例是纯逻辑测试，不需要模型、麦克风或网络。
- `FbankParityTests`、`EndToEndRecognitionTests` 需要本机已有模型，缺失时自动 `XCTSkip`。
  夹具由 `scripts/dump_reference_fixtures.py` 生成（需要 `client-server/server/` 的 Python 环境）。
- 端到端测试用编辑距离而非逐字相等验收：Python 与 macOS 使用两套独立编译的 ONNX Runtime，
  个别模棱两可的 token 可能因浮点求和顺序翻转（如英文大小写），详见 [`DESIGN.md`](DESIGN.md) §8。
- 中英文空格规则读共享夹具 `Fixtures/text_format_cases.json`，修改规则时两个平台同步。
- 自定义词汇的替换层与束搜索读共享夹具 `Fixtures/vocabulary_cases.json`、`Fixtures/ctc_hotword_cases.json`，
  Windows 同读这两份；规则变更先改 [`docs/VOCABULARY_SPEC.md`](docs/VOCABULARY_SPEC.md)。
  `EndToEndRecognitionTests.testIrrelevantVocabularyKeepsOutputIdentical` 钉死「50 条无关词汇、最强档时输出与贪心逐字相同」。
- 涉及热键、权限、麦克风、剪贴板、浮窗的改动，单测之外还要给出手工验证步骤（记录在 `DESIGN.md`）。

### 热词离线评估（调参用，默认跳过）

`HotwordEvaluationTests` 用来校准识别偏向的三档参数。准备一个目录（不入库）：若干 16kHz 单声道
`*.wav` 与同名 `*.txt`（期望文本），外加一份 `vocabulary.yaml`（顶层写 `entries:`，格式见下文「配置文件」的 `vocabulary` 段）：

```bash
VT_HOTWORD_EVAL_DIR=~/vt-eval xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper \
  -destination 'platform=macOS' test -only-testing:VoiceTyperTests/HotwordEvaluationTests
```

对关 / 轻 / 标准 / 强四档输出召回、误触发、字错误率与平均解码耗时（只含数字与文件名）。建议挑 10–20 个真实会
认错的词，每个各录 3 句含它的话，另录 30 句不含任何词条的日常话测误触发；校准目标是标准档误触发为 0、召回尽量高。

## 配置文件

所有选项都有设置界面。`config.yaml` 是应用的内部存储，运行期不监听文件变化，手改会绕过界面校验，
仅供排障时对照：

```
~/Library/Application Support/VoiceTyper/config.yaml
```

```yaml
asr:
  language: "auto"            # auto / zh / en / yue / ja / ko
  threads: 0                  # 0 = 自动（min(4, 核数)）
  model_dir: ""               # 留空 = 自动定位（下载目录 / ModelScope 缓存）
  idle_unload_minutes: 0      # 0 = 常驻不卸载
  preload_on_launch: true     # false = 首次按热键才加载（与录音并行）
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5                  # api_key 存在 Keychain（service com.voicetyper.app, account llm_api_key）
hotkey:
  modifiers: []
  key: "fn"                   # 或 right_command / right_option / left_option / right_control（modifiers 须为空）
  mode: "hold"                # hold = 按住说话；toggle = 按一次开始、再按一次结束
audio:
  input_device: "auto"        # auto / system / 设备 UID
ui:
  opacity: 0.85
  hud_position: "bottom_center"   # bottom_center / bottom_right / near_cursor / hidden
  interface_language: "zh"        # zh / en，重启后生效
vocabulary:                       # 自定义词汇（docs/VOCABULARY_SPEC.md），Windows 同一 schema
  bias: "standard"                # off / light / standard / strong；未知值回落 standard
  entries:
    - term: "Claude"              # 正确写法；≤ 50 字符
      aliases: ["克劳德"]          # 听起来像，≤ 10 个；可省略
    - term: "张三丰"
```

`vocabulary` 段手写时可以简写：`- 张三丰`（只有 term），`aliases: "克劳德"`（单个字符串）。读取时逐条规整：
去首尾空白、内部空白收敛为一个空格，空 term 丢弃，与 term 相同或重复的 alias 丢弃（不区分大小写），同名 term
合并，超过 200 条截断。日志只记条数，不记词条文本。

「引导是否完成」等一次性状态存在 `UserDefaults`，不在配置文件里。

文件读不出来（语法错误、编码损坏）时，应用另存一份 `config.yaml.broken`、以默认设置启动并弹出说明；
原文件保持不动，直到在设置里保存一次。

## 日志与耗时摘要

日志走系统统一日志（`os.Logger`），subsystem 为 `com.voicetyper.app`，category 有 `app`、
`permissions`、`hotkey`、`audio`、`asr`、`llm`、`model`、`metrics`。图形界面可在「控制台」App 里
搜索 `com.voicetyper.app`。

```bash
log stream --predicate 'subsystem == "com.voicetyper.app"' --level debug
log show   --predicate 'subsystem == "com.voicetyper.app"' --last 10m
log stream --predicate 'subsystem == "com.voicetyper.app" AND category == "metrics"'
```

`metrics` 每次听写输出一行，只含数字与枚举，不含识别文本；走 AX 插入时要等回读，这一行比上屏晚约 50ms。常用字段：

| 字段 | 含义 |
| --- | --- |
| `outcome` | inserted / empty / cancelled / discarded / gesture_cancelled / failed / secure_field / interrupted … |
| `release_to_done` | 松手到上屏的总耗时（核心指标） |
| `asr` / `llm` | 本地识别 / 智能校对耗时 |
| `llm_fail` / `llm_retry` | 校对回落原因；是否因服务拒绝 `thinking` 字段而重发 |
| `finalize_timeout` | 终稿看门狗是否触发 |
| `preview_avg` / `final_wait` | 预览平均耗时 / 终稿等待推理开始 |
| `tail_speech` | 松键那一刻是否仍在说话 |
| `insert_verify` / `clip_backup` | AX 插入回读判定（按写入前后的字符数）/ 提前备份剪贴板耗时（预判走 AX 不备份时为 `-`） |
| `input` / `cold` | 实际输入设备类别（`builtin*` 表示自动改用内置麦克风）/ 引擎是否冷加载 |
| `decode_mode` / `vocab_hits` / `decode_ms` | 终稿解码方式（`greedy` = 无词汇，`beam` = 热词束搜索）/ 最优路径上的热词命中次数 / CTC 解码耗时（一位小数） |

词汇编译另有一行 `asr` 日志：`词汇已编译：entries=… sequences=… biased=… too_short=… unsupported=… truncated=… bias=… 耗时=…ms`。

重置权限（调试授权流程时）：

```bash
tccutil reset Microphone    com.voicetyper.app
tccutil reset Accessibility com.voicetyper.app
tccutil reset ListenEvent   com.voicetyper.app
```
