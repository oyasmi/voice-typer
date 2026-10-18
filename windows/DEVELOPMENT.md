# VoiceTyper Windows 开发说明

[← Windows 使用说明](README.md) · [架构设计](DESIGN.md) · [变更记录](CHANGELOG.md) ·
[评审历史](docs/REVIEW_HISTORY.md) · [真机验证清单](docs/VERIFICATION.md) · [分发体积](docs/PACKAGING.md)

面向要自行编译、调试或参与开发的人。改代码前请先核对 [`docs/REVIEW_HISTORY.md`](docs/REVIEW_HISTORY.md)
里的遗留项与已否决方案，避免重复提案。

## 架构概览

```
VoiceTyperController（状态机，Idle→Recording→Recognizing→Inserting）
  ├── HotkeyService / AudioCaptureService / TextInsertionService
  │     （经 IHotkeyListening / IAudioCapturing / ITextInserting 注入，状态机可脱离真实钩子与麦克风单测）
  └── LocalAsrSession
         └── AsrService（AsrPump 专用串行线程）
                └── SenseVoiceEngine
                       ├── FbankFrontend（自写 512 点 radix-2 FFT，Kaldi 兼容 fbank）
                       ├── LfrCmvn
                       ├── InferenceSession（Microsoft.ML.OnnxRuntime，CPU EP）
                       └── CtcDecoder + TextPostprocessor
```

识别管线从 [`macos/`](../macos/) 的 Swift 实现直译而来。两个平台共用
`macos/Tests/VoiceTyperTests/Fixtures/` 下的金标准夹具；修改 fbank、LFR/CMVN、CTC 解码或文本后处理时
必须同步评估两个平台。所有终止路径（完成 / 出错 / Esc / 短录音 / 组合手势作废 / 停止）只走一个幂等的
`Finish()`。

## 构建

前置条件：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)；打安装包另需
[Inno Setup](https://jrsoftware.org/isdl.php)（`ISCC.exe` 在 PATH 中）。

```bat
cd windows
dotnet run          :: 开发调试
build.bat           :: 构建全部发布产物到 dist\
```

```
VoiceTyper-<版本>-win-x64-setup.exe       安装包（per-user，不弹 UAC）
VoiceTyper-<版本>-win-x64-portable.zip     便携版
VoiceTyper-<版本>-win-arm64-setup.exe
VoiceTyper-<版本>-win-arm64-portable.zip
```

- 未安装 Inno Setup 时只产出便携版。
- 依赖系统框架的目录式发布（`--self-contained false`），启用 ReadyToRun、不做 IL 裁剪；
  `scripts/verify_publish.ps1` 检查必需文件，并拒绝含 .NET 运行时、模型或测试程序集的产物。体积记录见
  [`docs/PACKAGING.md`](docs/PACKAGING.md)。
- 模型不随包分发。离线预置：`powershell -ExecutionPolicy Bypass -File scripts\fetch_model.ps1`。
- 改版本号：`VoiceTyper.csproj` 的 `<Version>` / `<AssemblyVersion>` / `<FileVersion>`、`app.manifest`，
  并同步 `build.bat` 与 `installer/VoiceTyper.iss` 的版本回退值。

### 签名（可选）

不设置环境变量时产物不签名。先把证书导入本机证书存储，再：

```bat
set VOICETYPER_SIGN_THUMBPRINT=<证书 SHA1 指纹>
set VOICETYPER_TIMESTAMP_URL=http://timestamp.digicert.com
build.bat
```

`build.bat` 会用 `signtool.exe` 依次签 `dist\<rid>\VoiceTyper.exe` 与安装包。

### 在 macOS / Linux 上开发

.NET SDK 可交叉编译 Windows 目标，能验证「能否编译、产物结构是否正确」和大部分纯逻辑：

```bash
cd windows
make cross-check      # 交叉编译
make cross-publish    # 交叉发布到 dist-cross/（只验证结构，不能运行）

# 在非 Windows 主机跑测试：不带 RID 构建，再删掉运行时配置里的 WindowsDesktop 框架依赖
dotnet build Tests/VoiceTyper.Tests/VoiceTyper.Tests.csproj -c Debug \
  -p:SelfContained=false -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=
#   编辑 Tests/VoiceTyper.Tests/bin/Debug/net10.0-windows/VoiceTyper.Tests.runtimeconfig.json，
#   删除 frameworks 里的 Microsoft.WindowsDesktop.App
dotnet test Tests/VoiceTyper.Tests/bin/Debug/net10.0-windows/VoiceTyper.Tests.dll
```

需要 WinForms STA 线程的少数用例在非 Windows 主机上必然失败，属已知基线。低级键盘钩子、`SendInput`、
剪贴板、WASAPI 采集、窗体绘制与 DPI **只能在真机验证**，不得根据交叉环境的结果下结论，清单见
[`docs/VERIFICATION.md`](docs/VERIFICATION.md)。

## 测试

```bat
cd windows
dotnet test
```

- 绝大多数用例是纯逻辑测试，不需要模型、麦克风或网络。
- `FbankParityTests`（LFR/CMVN 部分）与 `EndToEndRecognitionTests` 需要本机模型，缺失时通过
  `Xunit.SkippableFact` 显示为「已跳过」并写明原因，不会变成假通过。
- 中英文空格规则读与 macOS 共享的 `text_format_cases.json`（从 `macos/` 链接）。
- 涉及热键、麦克风、剪贴板、浮窗的改动，单测之外还要给出手工验证步骤，并区分
  「代码审查通过」与「真机验证通过」。
- Windows CI 会额外输出设置窗口中英文预览与 TRX 结果，下载该次运行的 `voicetyper-windows-ui-check`
  附件查看；真实系统缩放、跨屏和工作区变化仍按 [DESIGN §30.3](DESIGN.md#303-验证与手工步骤) 验收。

## 配置文件

```
%APPDATA%\VoiceTyper\config.yaml
```

```yaml
asr:
  language: "auto"           # auto / zh / en / yue / ja / ko
  threads: 0                 # 0 = 自动（min(4, 核数)）
  model_dir: ""              # 留空 = 自动定位（下载目录 / ModelScope 缓存）；没有图形界面入口
  preview_window: 0          # 秒；0 = 首次加载后按本机性能自动校准
  idle_unload_minutes: 0     # 0 = 常驻不卸载
  preload_on_launch: true    # false = 首次按热键才加载（与录音并行）
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
hotkey:
  modifiers: []
  key: "right_ctrl"          # 默认单独的右 Ctrl；或 right_alt，或组合键如 modifiers: ["ctrl"] + key: "f2"
  mode: "hold"               # hold = 按住说话；toggle = 按一次开始、再按一次结束
audio:
  input_device: "auto"       # auto / system / 音频端点 ID
ui:
  opacity: 0.85
  hud_position: "bottom_center"  # bottom_center / bottom_right / near_cursor / hidden
  interface_language: "zh"   # zh / en，重启后生效
```

越界或非法的值会在加载 / 保存时夹逼回合法范围并记 warning 日志。API Key 不在配置文件里，用 DPAPI
（当前用户范围）加密后存 `%APPDATA%\VoiceTyper\llm_api_key.dat`。其他本地数据：模型在
`%LOCALAPPDATA%\VoiceTyper\models\`（刻意不放漫游目录），日志在 `%LOCALAPPDATA%\VoiceTyper\logs\`。

## 日志与耗时摘要

日志文件 `%LOCALAPPDATA%\VoiceTyper\logs\app.log`，按 2MB 滚动、保留 3 份备份。

```powershell
Get-Content "$env:LOCALAPPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50
```

每次听写输出一行 `[metrics]` 摘要，只含数字与枚举（不含识别文本、设备名或窗口标题），例如（实为一行）：

```
dictation session=3fa1 outcome=inserted mode=hold hotkey=combo input=builtin capture_start=420 ...
release_to_finalize=8 asr=310 llm=- llm_result=off insert=24 release_to_done=352 previews=5 preview_avg=240 cold=0
```

`input=bluetooth*` 的星号表示由「自动」策略从系统默认输入切换而来。`key_lag` 至 `hud_ready` 拆解
「按下热键到可以开口」的耗时，见 [DESIGN.md §18.3](DESIGN.md#183-新增耗时字段)；`final_wait` 等字段见
[DESIGN.md §23.5](DESIGN.md#235-新增耗时字段)。设置窗口「诊断与帮助」页可查看最近 20 次听写的精简版。

### 键盘钩子自愈

`WH_KEYBOARD_LL` 回调超时可能被系统静默摘除。钩子回调只做判定与异步投递；另有独立的健康定时器在句柄
丢失或长时间零回调时按指数退避重装，锁屏 / 睡眠恢复后也会主动重装，持续失败时在托盘与设置页显示
「热键监听已失效」。真实摘钩场景尚未在真机上验证。
