# Windows 修复交付记录（R0–R4）

对应 [REPAIR_TRIAGE.md](REPAIR_TRIAGE.md) 的工单。分诊基线 `86424eb`，本轮在 **Linux、无 .NET SDK、
无 Windows 真机** 环境完成，全部条目状态为「代码与静态验证完成」，**尚未编译、未跑测试、未真机验证**。

## 批次状态

| 批次 | 工单 | 状态 |
| --- | --- | --- |
| R0 | R0-1 恢复可编译 | 代码完成，待 `dotnet build` 确认 |
| R0 | R0-2 修正必然失败的测试 | 代码完成 |
| R0 | R0-3 消除假通过 | 代码完成 |
| R1 | R1-1 重采样死循环 | 代码完成 |
| R1 | R1-2 INPUT 原生布局 | 代码完成，新增 NativeLayoutTests |
| R1 | R1-3 采集资源泄漏与多声道 | 代码完成 |
| R1 | R1-4 钩子回调不同步执行业务 | 代码完成 |
| R2 | R2-2 空闲卸载后无法恢复 | 代码完成 |
| R2 | R2-3 模型重载无限循环 | 代码完成 |
| R2 | R2-1 热键消费与按键状态机 | 代码完成，新增 HotkeyStateMachine + 单测 |
| R2 | R2-4 API Key 保存事务 | 代码完成，新增取消传播单测 |
| R3 | R3-1 单活跃听写收口 | 代码完成 |
| R3 | R3-2 剪贴板与插入反馈 | 代码完成（TokenElevation / DWORD / ReadFailed / 修饰键） |
| R3 | R3-3 麦克风状态误报 | 代码完成（MicProbeResult 结构化结果） |
| R3 | R3-4 下载超时与取消 | 代码完成（CTS + 响应头/无进度超时） |
| R3 | R3-5 钩子自愈两缺陷 | 代码完成（定时器解耦 + 退避 + 健康回调） |
| R4 | R4-1 单实例范围 | 代码完成（Local\ + Mutex 异常处理） |
| R4 | R4-2 安装与自启残留 | 代码完成（iss [Registry] / build.bat / SetEnabled→bool） |
| R4 | R4-3 文档口径校准 | 完成（README / DESIGN §13.2 / §13.3 已知限制） |
| R4 | R4-4 测试覆盖补强 | 部分：新增状态机 / INPUT 布局 / 取消传播用例；可控 HTTP 服务端矩阵、引擎生命周期计数、STA 剪贴板集成测试待补 |

## 逐工单交付

### R0-1 恢复可编译
- 问题静态证据：`VoiceTyper.csproj` 无 `Compile Remove`，SDK glob 把 `Tests/**/*.cs` 编入不引用
  xUnit 的主工程；`AppCoordinator.cs` lambda 形参 `_` 使 `_ = ReevaluateReadinessAsync()` 变成
  Task→AsrState 赋值（CS0029）；`AsrService` 缺 `using VoiceTyper.Llm`；`SenseVoiceEngine` 缺
  `using VoiceTyper.Support`；`ConfigStoreTests` 的 Theory 用 `public` 方法暴露 internal 枚举
  `AsrLanguage`（CS0051）。
- 修改后行为：`csproj` 增 `<Compile Remove="Tests/**/*.cs" />` 与 `<None Remove="Tests/**" />`；
  `OnStateChange` 改具名方法 `OnAsrStateChanged(AsrState)`；补两处 using；Theory 参数改
  `string` 用名称比对。
- 接口/配置变更：无对外变更。
- 自动化测试：未执行（无 SDK）。
- 仍需验证：`dotnet build VoiceTyper.sln -c Release`、两个 RID `dotnet publish`、确认
  `dist/win-x64` 无 `xunit.*` / `VoiceTyper.Tests.dll`；编译器可能报出清单外的其他错误需一并处理。
- macOS 影响：无（仅 Windows 工程与测试）。

### R0-2 / R0-3 测试
- `ConfigStoreTests.Clone_IsDeepCopy…`：`HotkeyConfig.Modifiers` 默认已含 `"ctrl"`，去掉多余
  `Add("ctrl")`，断言改 `Assert.Equal(new[]{"ctrl"}, …)`，保持"深拷贝"意图。
- `FbankParityTests`：必需夹具缺失由静默 `return` 改为 `Assert.Fail`；需要 `am.mvn` 的 CMVN
  用例改用 xUnit 2.9 `Assert.SkipWhen` 明确跳过并写清原因。**未放宽 1e-3 金标准阈值。**
- 仍需验证：确认 `xunit` 2.9.3 的 `Assert.Fail` / `Assert.SkipWhen` 在当前 runner 下可用
  （2.9.0 起提供；若 runner 不支持动态跳过需换回 `[Fact(Skip=…)]` 静态跳过）。

### R1-1 重采样死循环
- 静态证据：`BufferedWaveProvider` 默认 `ReadFully=true`，数据不足补零返回满长度，
  `WdlResamplingSampleProvider` 透传，`OnCaptureDataAvailable` 的
  `while ((read = resampled.Read(...)) > 0)` 永不退出，在 WASAPI 回调线程无限产静音。
- 修改后行为：`ReadFully = false`；另加单次回调迭代上限（约 3s@16kHz），触顶记 warn 退出。
- 仍需验证：真机录 5s 观察 CPU 不飙升、`_deliveryQueue` 不无限增长、停止耗时正常；
  多采样率/多声道/不规则分块的纯逻辑样本数测试（R4-4）。

### R1-2 INPUT 原生布局
- 静态证据（按 ABI 复算）：`InputUnion` 仅含 `KEYBDINPUT`，x64/arm64 托管 `INPUT`=32B，
  原生=40B，`SendInput` 收 `cbSize=32` 返回 0（`ERROR_INVALID_PARAMETER`），文本插入 100% 失败。
- 修改后行为：`InputUnion` 补 `MOUSEINPUT`/`HARDWAREINPUT`；调用点已用 `Marshal.SizeOf<INPUT>()`
  故自动变 40。
- 自动化测试：新增 `NativeLayoutTests`（`SizeOf<INPUT>()==40`、`OffsetOf(U)==8`、union==32）。
- 仍需验证：**真机** x64/arm64 进程 `Marshal.SizeOf` 实测 + 记事本粘贴中/英/数字/换行。
  结构体测试与真机粘贴测试分别记录。

### R1-3 采集资源泄漏与多声道
- 静态证据：正常 `Stop()` 从不调 `Cleanup()`；`MMDeviceEnumerator` 从不 Dispose；
  `ToMono()`（`StereoToMonoSampleProvider`）源声道 ≠ 2 抛 `ArgumentException` 被吞成"启动录音失败"。
- 修改后行为：`MMDeviceEnumerator` 用 `using`；新增 `TeardownStoppedCapture`，在 NAudio 异步
  `RecordingStopped` 到达后退订事件并释放 capture/device，快速连按时按引用只释放已停止实例；
  `Cleanup` 也退订事件；声道显式分 1 / 2 / >2，>2 抛可读 `AudioStartException`。
- 仍需验证：连续 20 轮 Start/Stop 句柄数/线程数不增长；真机拔 USB 麦克风后能开新会话。

### R1-4 钩子回调不同步执行业务
- 静态证据：`UiDispatcher.Post` 的 `IsOnUiThread` 分支同步执行；钩子装在 UI 线程，
  `HookCallback` 会在回调内同步跑完 `AudioCaptureService.Start()`（COM 枚举 + WASAPI 打开）。
- 修改后行为：新增 `UiDispatcher.PostAsync`（始终 `_context.Post`）；`HotkeyService` 的
  `OnPress`/`OnRelease`/`OnCancel` 四处投递改用它；回调随即 `return`。
  **未在注释/文档写死 `LowLevelHooksTimeout` 毫秒数。**
- 未做：独立钩子消息线程（按 §3.1 降级）。
- 仍需验证：阻塞 UI 数秒钩子仍响应；`PostAsync` 在 UI 线程调用不同步执行的单测（需 SyncContext 夹具）。

### R2-2 空闲卸载后无法恢复
- 静态证据：`MakeSession` 把 `SuspendedForIdle` 与 Ready/Loading 一并排除在预加载外，
  但该状态引擎已 Dispose 且引用为 null，会话轮询一个永不就绪的引用 5s 后报"尚未就绪"。
- 修改后行为：排除集合收窄为 `Ready or Loading`；`LocalAsrSession` 增 `engineLoadError` 回调，
  `State==Failed/ModelMissing` 时立刻带真实原因收尾；等待上限 5s→20s。
- 仍需验证：可注入时钟测 `Ready→空闲卸载→MakeSession→加载→final`，覆盖加载慢于说话/加载失败/
  加载期间取消；真机默认十分钟空闲后再听写。

### R2-3 模型重载无限循环
- 静态证据：`Ready` 手动重载 → `UnloadNowAsync` 置 `Unloaded` → `OnStateChange` 同步进
  `ReevaluateReadinessAsync` 的 `Unloaded` 分支 → `PreloadAsync` → `_isLoadInFlight` 仍 true →
  置 `_reloadRequestedWhileLoading` → 本次加载完成后再次 `RequestLoadAsync(unloadFirst:true)` →
  回到卸载。每轮构建一个 ~500MB ORT session。
- 修改后行为：`PreloadAsync` 为 EnsureLoaded 语义——有在飞任务直接返回它，**绝不置 pending**；
  `ReloadAsync` 才在在飞时记一次合并重载。`_inFlightLoad` 在 `finally` 清除。
  `ReloadModelAsync` 补活跃听写拒绝。
- 仍需验证：假引擎统计构造/释放次数——一次点击只 1 构造 1 释放；加载中重复 Ensure 不追加；
  连改三次配置只应用最新；活跃听写期间重载被拒且引擎未释放。

### R2-1 热键消费与按键状态机
- 静态证据：所有分支 `return CallNextHookEx` → 被接管的主键与 Esc 穿透到前台应用；
  `ReadCurrentModifiersExcluding` 的 switch 只认 `VK_CONTROL`(0x11) 等通用码，钩子实际交付
  `VK_LCONTROL`(0xA2) 等，"先松修饰键"路径 mask 不清除 → OnRelease 不触发；Esc 取消置
  `_isActive=false` 后主键 auto-repeat 命中"未激活 + 匹配"分支 → 立即重启录音。
- 修改后行为：抽出 `HotkeyStateMachine`（纯逻辑）——显式状态 Idle/Engaged/AwaitingFullRelease；
  `NormalizeModifier` 归一化左右键；自维护 `_pressedModifierKeys` 集合（左右 Ctrl 同按时松一个
  不判定 Ctrl 释放）；被接管的主键 down/repeat/up 与生效 Esc 返回 `(IntPtr)1` 消费，修饰键
  事件放行。安装钩子时用一次系统快照播种已按住的修饰键。
- SetupForm：保存热键前 `HotkeyService.IsSupportedKey` 即时校验 + 空修饰键拒绝。
- 自动化测试：`HotkeyStateMachineTests`（先松主键 / 先松修饰键 / 左右键混按 / auto-repeat /
  Esc 后继续按住 / 通用 VK_CONTROL 归一化）。
- 未做：切换式录音、`RegisterHotKey` 冲突检测（保留为增强）。
- 仍需验证：**真机** 目标应用收不到被接管的热键；中文 IME、AltGr 不受影响；暂停/恢复。
- macOS 影响：无（Windows 特有输入路径）。

### R2-4 API Key 保存事务
- 静态证据：`HandleSaveRecognition` 先 `OnSaveLlmApiKey` 落盘再 `await OnSaveConfig`，后者在活跃
  听写期间抛异常 → 密钥已进 DPAPI 但提示"保存失败"；`Asr/Llm/HotkeyConfigEquals` 都不含密钥
  → 只改密钥判为 `onlyUiChanged` → 控制器不重建，`LlmCorrector.ApiKey` 仍是构造时读的旧值，
  但"测试纠错"用输入框新值（测试通过、实际听写用旧密钥）。
- 修改后行为：新增协调器单一事务 `SaveRecognitionAsync(draft, newApiKey)`——先校验、先判断
  是否允许应用（活跃听写则在写任何东西前拒绝），通过后再写密钥、再写配置；密钥变化作为显式
  更新信号走控制器重建路径（重建时重新从 SecretStore 读密钥，新密钥立即生效）。
  `SecretStore.SaveLlmApiKey` 改为临时文件 + `File.Replace`/`Move` 原子替换，保留 DPAPI 当前用户
  保护。SetupForm 用载入时的原值判断密钥是否改动，`null` 表示未改。
- 顺带：`LlmCorrector.CorrectAsync` 接受并传播外部 `CancellationToken`（与内部超时 CTS 用
  `CreateLinkedTokenSource` 关联）；`LocalAsrSession` 加会话级 CTS，`Close` 时取消，取消后的
  纠错结果被静默丢弃。
- 自动化测试：`LlmCorrectorTests.Correct_PropagatesCancellation_InsteadOfFallingBack`。
- 仍需验证：**真机 / 假 HTTP 服务端** 只改密钥后实际听写 Authorization 头是新值；密钥写失败 /
  配置写失败 / 活跃听写拒绝三种情况均无虚假成功；日志/YAML/诊断包搜不到密钥。
- macOS 影响：无。

### R3-1 单活跃听写收口
- `BeginRecording` 在 `_asrSession != null`（上一段仍在识别/插入）时拒绝新会话并 `PreviewWarning`；
  `OnFinal` 的 else 分支若已有新会话，改为复制到剪贴板而非插入；短录音丢弃标志从实例字段移到
  `LocalAsrSession.ShortDiscard`。
- 仍需验证：假音频源 + 假识别，连按热键 / 短录音后立刻再按 / 各阶段取消 / 迟到 final，
  断言每会话至多插入一次。

### R3-2 剪贴板与插入反馈
- `CopyToClipboard`→`bool`；`CanIncludeInClipboardHistory`/`CanUploadToCloudClipboard` 写 4 字节
  DWORD；`CheckForegroundWindowElevation` 读 `TokenElevation`，无法判定返回 `Unknown`；
  `ClipboardSnapshot.ReadFailed` 时不 `Clipboard.Clear()`；插入前 Alt/Shift/Win 按住则改为复制。
- 仍需验证：Windows STA 集成测试（纯文本/图片/文件列表/原所有者退出/剪贴板占用/恢复期间用户
  复制）；跨应用接受情况、UIPI 提示触发必须真机。

### R3-3 麦克风状态误报
- `AudioStartException` 增 `Kind`；`MicPermissionProbe.Probe()` 返回 `MicProbeResult`
  （Available/NoDevice/AccessDenied/DeviceFailure/Unknown）；AppCoordinator + SetupForm 按枚举展示。
- 仍需验证：无麦克风 / 隐私开关关闭 / 设备忙 / 拔出显式设备，显示与实际一致。

### R3-4 下载超时与取消
- `volatile bool` → `CancellationTokenSource`（与调用方 token `CreateLinkedTokenSource`），传入
  `SendAsync`/`ReadAsync`/`WriteAsync`；响应头超时 30s + 正文滑动无进度超时 30s；`Dispose` 取消并释放。
- 仍需验证：可控 HTTP 服务端覆盖断流 / 无限等待 / 取消 / 206 续传 / 416 / 错误哈希（列入 R4-4）。

### R3-5 钩子自愈的两个缺陷
- 健康定时器生命周期独立于钩子实例（`InstallHook` 抽出，重装失败不销毁定时器）；指数退避重试
  （上限 5 分钟）；`OnHealthChanged` 把不可用状态报给控制器→UI；连续两次可疑才动手（降低
  `GetLastInputInfo` 含鼠标的误判）；恢复后清理按键状态、必要时补 `OnCancel` 收尾。
- 仍需验证：只移动鼠标不触发重装；安装失败后持续重试且有可见状态；暂停/退出不自动重启。

### R4-1 单实例范围
- `Global\` → `Local\`；`Mutex` 构造包 `UnauthorizedAccessException` /
  `WaitHandleCannotBeOpenedException` / `IOException`，失败退化为不做单例保护。
- 仍需验证：同会话双击只留一个实例；多用户 / 快速用户切换 / RDP 不互相误阻。

### R4-2 安装与自启的残留
- `installer/VoiceTyper.iss` 增 `[Registry]` 段（`uninsdeletevalue` 清理 `HKCU\...\Run` 的
  `VoiceTyper` 值，不在安装时创建）；`build.bat` 的 `Compress-Archive` 加 `$ErrorActionPreference=Stop`
  + errorlevel 检查；`StartupRegistration.SetEnabled`→`bool`，SetupForm / TrayController 写失败时
  恢复勾选为 `IsEnabled` 真实值并提示。
- 仍需验证：干净机器安装/卸载，卸载后无失效自启项，不删用户额外文件；ZIP 失败真的中止。

### R4-3 文档口径校准
- `windows/DESIGN.md` 顶部补 R0–R4 进度说明；§13.2 追加本轮几项待真机验证条目；新增
  §13.3「剪贴板 + Ctrl+V 方案的固有限制」（`SendInput` 成功 ≠ 已粘贴、1s 恢复窗口）作为已知限制。
- `windows/README.md` 热键失效排障段按 R3-5 实际行为重写。

### R4-4 测试覆盖补强（部分）
- 新增：`HotkeyStateMachineTests`（按键序列 + `IsEngaged`）、`NativeLayoutTests`（INPUT=40B）、
  `LlmCorrectorTests` 取消传播、`FbankParityTests` 必需夹具 `Assert.Fail`。
- 待补（需 SDK / 更多脚手架）：可控 HTTP 测试服务端的下载故障矩阵、`AsrService` 假引擎生命周期
  计数、空闲恢复时序（可注入时钟）、Windows STA 剪贴板事务集成测试、端到端
  `speech_zh_en_mixed.wav` 编辑距离 ≤ 2。

## 给下一位实施者的接力说明
1. 先在带 .NET SDK 的环境跑 R0 验收；编译器报出的清单外错误按 R0-1 要求一并修并记录。
2. R3、R4 未开始。涉及 Win32/音频/剪贴板/安装的工单在只有代码检查时不得标记最终完成。
3. 同一生命周期文件（`AudioCaptureService` / `VoiceTyperController` / `AsrService` /
   `LocalAsrSession`）不要与其他 agent 并行修改。
4. 未触及识别管线（fbank/LFR/CMVN/CTC/后处理/模型 I/O）。
