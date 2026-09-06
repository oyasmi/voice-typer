# Windows 修复交付记录（R0–R2）

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
| R3 / R4 | 全部 | **未开始** |

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

## 给下一位实施者的接力说明
1. 先在带 .NET SDK 的环境跑 R0 验收；编译器报出的清单外错误按 R0-1 要求一并修并记录。
2. R3、R4 未开始。涉及 Win32/音频/剪贴板/安装的工单在只有代码检查时不得标记最终完成。
3. 同一生命周期文件（`AudioCaptureService` / `VoiceTyperController` / `AsrService` /
   `LocalAsrSession`）不要与其他 agent 并行修改。
4. 未触及识别管线（fbank/LFR/CMVN/CTC/后处理/模型 I/O）。
