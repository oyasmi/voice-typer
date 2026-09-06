# Windows 修复分诊与执行顺序

本文是对 [REVIEW_AND_REPAIR_PLAN.md](REVIEW_AND_REPAIR_PLAN.md) 的**分诊结果**：剔除不成立的
结论、降级性价比过低的条目、把剩余问题重排成可直接交给实施 agent 的工单。

[项目入口](../README.md) · [Windows 使用说明](README.md) · [架构设计](DESIGN.md)

- 分诊日期：2026-09-06
- 分诊基线：`86424eb`，Windows 应用版本 `3.2.1`
- 分诊环境：Linux，无 .NET SDK、无 Windows 真机。**本轮没有编译、没有运行测试**。
- 分诊方法：逐条回到源码定位，只保留能在源码 / 依赖源码 / ABI 布局层面确证的结论；
  无法确证的降级为"待验证"或移出本轮。

原计划的 WR-xxx 编号在本文中保留，便于交叉引用；执行顺序改用 R0–R4。

## 1. 分诊的总体结论

**这份工程从未被成功编译过。** `VoiceTyper.csproj` 没有排除 `Tests/**`，SDK 默认 glob 会把测试
源码编进主工程，而主工程不引用 xUnit；此外还有 4 处独立编译错误。这条事实重新定义了整份审查
报告的性质：

- 所有"P1 运行期缺陷"都**没有一条被执行验证过**，包括那些写得很自信的。
- 反过来，凡是能从源码逻辑闭环推出的缺陷，几乎都还在——因为没有任何一次运行反馈修正过它们。
- 因此 R0（恢复可编译 + 让测试可信）不是"清理工作"，而是后续每一项判断的前提。

分诊中确证了 4 个此前被列为"P1"的问题实际是**功能性阻断**，应提级到 P0：

| 问题 | 实际后果 |
| --- | --- |
| WR-002 重采样循环 | WASAPI 回调线程死循环产生静音，投递队列无限增长 —— 一按热键就挂 |
| WR-003 `INPUT` 布局 | `cbSize=32` ≠ 原生 40，`SendInput` 必定失败 —— 文本插入 100% 不工作 |
| WR-004 空闲卸载不恢复 | 空闲 10 分钟后每一次听写都报"识别引擎尚未就绪"，且不可自愈 |
| WR-005 重载反馈环 | 点一次"重新加载模型"进入**无限重载循环**，每轮构建一个 ~500MB ORT session |

## 2. 分诊结果总表

图例：**保留** = 确证存在且值得修；**降级** = 部分成立，只保留其中的最小修复；
**移出** = 不成立、或成本远大于收益、或属于产品功能而非缺陷。

| ID | 原严重度 | 分诊后 | 处置 | 依据 |
| --- | --- | --- | --- | --- |
| WR-001 | P0 | **P0** | 保留（全部 5 项已逐条确证） | 见 §4 R0-1 |
| WR-002 | P0 | **P0** | 保留 | `BufferedWaveProvider.ReadFully` 默认 `true` |
| WR-003 | P0 | **P0** | 保留 | x64 联合体 32B vs 原生 40B，已按 ABI 复算 |
| WR-004 | P1 | **P0** | 保留（提级） | `MakeSession` 排除 `SuspendedForIdle`，一行即可修 |
| WR-005 | P1 | **P0** | 保留（提级，范围收窄） | 已确证同步重入形成无限环 |
| WR-006 | P1 | P1 | **降级**：只做"钩子回调必须异步投递"，不做独立钩子线程 | 见 §3.1 |
| WR-007 | P1 | P1 | 保留（3 个子问题全部确证） | 见 §4 R2-1 |
| WR-008 | P1 | P2 | **降级**：只加"识别/插入期间拒绝新会话"，不做活跃听写对象重构 | 见 §3.2 |
| WR-009 | P1 | P1 | **降级**：只修资源泄漏 + 多声道，不做 generation 隔离 | 见 §3.3 |
| WR-010 | P1 | P1 | **降级**：保留 4 个确证子问题，其余移出 | 见 §3.4 |
| WR-011 | P1 | P1 | **降级**：只修状态误报，设备选择/电平表移出 | 见 §3.5 |
| WR-012 | P1 | — | **移出本轮**：首启引导是产品功能，不是缺陷 | 见 §3.6 |
| WR-013 | P1 | P1 | **降级**：只修超时与取消传播，续传测试矩阵移出 | 见 §3.7 |
| WR-014 | P1 | P1 | 保留（两个子问题均确证） | 见 §4 R2-4 |
| WR-015 | P1 | P2 | **降级**：`Global\` → `Local\` 保留；IPC 激活移出 | 见 §3.8 |
| WR-016 | P1 | P2 | **降级**：只保留卸载残留自启项、打包失败静默两项 | 见 §3.9 |
| WR-017 | P2 | P2 | **降级**：只修"写失败仍显示成功"，系统禁用态查询移出 | 见 §3.10 |
| WR-018 | P1 | **P0/P2** | **拆分**：断言错误与假跳过归入 R0；覆盖率补强归入 R4 | 见 §4 R0-3 |
| WR-019 | P2 | — | **移出本轮**：无真机即无法测量，改动缺乏依据 | 见 §3.11 |
| WR-020 | P2 | P2 | **降级**：只保留文档口径校准，DPI/无障碍审计移出 | 见 §3.12 |

净结果：20 项 → **12 项进入本轮**（其中 6 项 P0），4 项移出，其余大幅收窄范围。

## 3. 被否决与降级项的理由

分诊只推翻具体结论，不推翻"该处需要注意"。下列判断若被真机复现推翻，以复现为准。

### 3.1 WR-006：独立钩子消息线程 —— 降级

**成立的部分**：钩子装在 UI 线程，而 `UiDispatcher.Post` 在 UI 线程上**同步执行**
（`Support/UiDispatcher.cs` 的 `IsOnUiThread` 分支），所以 `HookCallback` → `OnPress` →
`AudioCaptureService.Start()`（WASAPI 设备枚举 + 打开）全部发生在钩子回调内部。这确证成立，
且是真实风险：低级钩子回调超时会被系统静默摘除。

**不成立/无依据的部分**：原文断言"1709 之后最大超时为 1000ms，原注释的 5000ms 不能作为依据"。
这条在没有真机的情况下无法确证，且对修复方案没有影响——无论超时是 300ms 还是 5000ms，正确做法
都是让回调立刻返回。**不要把具体毫秒数写进代码注释或文档当作事实。**

**降级理由**：独立钩子消息线程要新起线程、搬消息泵、重做生命周期与 `Stop` 的线程亲和性，属于
高风险改动；而"回调内绝不同步执行业务"这一条就消除了绝大部分风险，代价是几行。独立线程只在
"UI 线程本身被长时间阻塞"时才额外有价值——而当前推理已在 `AsrPump` 上，UI 阻塞源尚未确证存在。
**先做最小修复，等真机出现死键再立项。**

同样降级的还有自愈逻辑：`GetLastInputInfo` 包含鼠标输入确属事实（会误判），`Start()` 里先
`Stop()` 导致安装失败连健康计时器一起销毁也确属事实。但这两条都是"自愈机制本身可能失灵"，
不是"主功能失灵"，放在 R3。

### 3.2 WR-008：活跃听写对象重构 —— 降级

原文称"旧回调可改变新会话状态，结果可能乱序"。**这条基本不成立**：
`Core/VoiceTyperController.cs` 的 `BeginLocalRecording` 已经对 `OnFinal`/`OnWarning`/`OnError`/
`OnSessionCapped` 全部做了 `ReferenceEquals(_asrSession, session)` 身份校验，前台窗口句柄也用
局部变量按会话捕获（代码里有明确注释说明就是为连按两次热键设计的）。原文描述的污染路径已被堵住。

**残留的真实缺口只有两个，都很窄**：
1. `BeginRecording` 只检查 `_isRecording`，所以上一会话处于 Recognizing/Inserting 时可以开始新
   录音；旧会话完成后走 `else` 分支**仍然会插入文本**——在用户已经开始下一句的时候插入。
2. `_discardCurrentSession` 是实例字段：短录音的 tail 经投递线程异步到达，若期间用户又按了热键，
   `BeginRecording` 把它重置为 `false`，短录音丢弃失效。

这两点各自几行就能修（见 R3-1）。而"引入含 ID/阶段/取消源/目标窗口的活跃听写对象"是对控制器
的整体重写，在身份校验已经存在的前提下增量收益有限、回归风险高。**移到 P2，本轮只修上述两点。**

看门狗那段（"评估 ORT 的运行取消"）也一并降级：ORT 的 `RunOptions.Terminate` 语义与本项目
串行 pump 的交互需要真机验证，无 SDK 时改它是盲改。

### 3.3 WR-009：generation 代际隔离 —— 降级

**确证成立且必须修**的是资源泄漏：`Start()` 每次新建 `MMDeviceEnumerator` / `MMDevice` /
`WasapiCapture`，而正常 `Stop()` 路径**从不调用 `Cleanup()`**——只有异常路径和 `Dispose()` 才会。
下一次 `Start()` 直接覆盖字段，上一轮的 COM 对象与 WASAPI 采集线程全部泄漏。每次听写泄漏一组。
这是确凿的、便宜的、必须修的。

**同时确证**：`sampleProvider.ToMono()` 构造 `StereoToMonoSampleProvider`，源不是 2 声道时抛
`ArgumentException`，会被外层吞成"启动录音失败"。多声道麦克风阵列会直接不可用。

**降级的部分**：为每次采集引入独立上下文对象 + generation 校验。这是正确的目标架构，但在
`Cleanup()` 被正确调用、旧 `capture` 被退订并 Dispose 之后，跨会话污染窗口已经很小；而完整的
上下文重构会和 WR-008 的控制器改动互相纠缠。**本轮只做泄漏修复 + 退订 + 多声道处理**，
generation 隔离等真机出现跨会话串音再做。

### 3.4 WR-010：插入事务 —— 降级为 4 个确证子问题

`Services/TextInsertionService.cs` 里**确证成立、且都便宜**的是：

1. `CopyToClipboard` 返回 `void`，写失败只记日志；控制器随后无条件提示"已复制到剪贴板"。
   用户会在结果其实已经丢了的情况下去粘贴。
2. `ApplyClipboardExclusionFormats` 对三个排除格式写 1 字节；
   `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` 契约要求 DWORD（4 字节）。
   （`ExcludeClipboardContentFromMonitorProcessing` 只要求格式存在，那个是对的。）
   注意：这**不等于已经发生隐私泄漏**——第三个标记可能仍然生效，不要在提交信息里这么写。
3. `IsForegroundWindowElevated()` 只试 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`。该权限
   对提权进程通常也会授予，所以函数**几乎恒返回 `false`**，UIPI 提示形同虚设。
4. 快照读取失败（`Clipboard.GetDataObject()` 抛异常）返回空 `ClipboardSnapshot`，之后
   `RestoreClipboardSnapshotUnconditionally` 在 `Entries.Count == 0` 时执行 `Clipboard.Clear()`
   ——把读不出来的原剪贴板**清空**。"读失败"和"原本就是空"必须区分开。

**移出的部分**："SendInput 成功不等于目标已粘贴"、"固定 1 秒恢复不能保证所有应用读完"。这两条
陈述是对的，但它们是**剪贴板+粘贴这一整套方案的固有性质**，macOS 侧同样如此，没有便宜的修法
（可靠方案要么是 UI Automation 逐控件写入，要么是完整输入法，都超出本轮边界）。应作为已知限制
写进文档，而不是当作待修缺陷挂在清单上。"插入前等待物理修饰键释放"保留为 R3 的小改动。

### 3.5 WR-011：麦克风 —— 降级

**确证**：`App/AppCoordinator.cs` 的 `ProbeMicrophone` 写的是
`MicPermissionProbe.TryProbe(out var denied); return denied;` ——**返回值被直接丢弃**。于是
"没有麦克风"、"设备被占用"、"初始化失败"全部落到 `denied == false`，UI 显示"麦克风可用"。
这是确凿的状态误报，修法就是把 `TryProbe` 换成结构化结果。

**移出**：设备选择下拉、稳定设备 ID、实时电平表、"跟随系统默认 / 通信设备"选项。这些都是新功能
（当前代码里根本不存在设备选择这一概念），属于产品增强而非缺陷修复，且要配合 UI 改版。
`Role.Communications` 的选择也是一个**有意的取舍**（通信设备通常是用户为语音选的那支），
原文把它列为问题缺乏依据。

### 3.6 WR-012：首次启动引导 —— 移出本轮

这一整条描述的是"应该有一个首启引导流程"，不是"现有代码坏了"。它是本清单里成本最高的一项
（新状态机 + 新 UI + 并行准备流程），却依赖 B–D 全部修完才能验收。在主功能还不能编译的阶段
投入首启体验，顺序是反的。**等 R0–R3 完成、能在真机上跑通一次完整听写之后再单独立项。**

### 3.7 WR-013：下载器 —— 降级

**确证**：`HttpClient { Timeout = Timeout.InfiniteTimeSpan }` 配 `volatile bool _cancelled`，
而 `_cancelled` 只在读循环的迭代间检查——一个卡死在 `ReadAsync` 上的连接**永远不会超时也不会
被取消**，用户点"取消"没有反应，退出时线程还挂着。这个修法很便宜：CTS + 合理超时。

**移出**：可控 HTTP 测试服务器 + 206/200/416 全矩阵测试。续传逻辑本身（`RangeHeaderValue`、
`resumed` 判定、`.part` 文件、成功后才 `File.Move`）读下来是**基本正确**的，原文没有指出其中
的实际缺陷，只是要求补测试。在没有 SDK 的当下，这是纯投入。降到 R4。

### 3.8 WR-015：单实例 —— 降级

`Global\VoiceTyper.Unified.SingleInstance` 会跨登录会话互斥确属事实，改成 `Local\` 是一行、
零风险、明确正确。**保留这一行。**

"通过带访问控制的本地 IPC 请求已有实例打开窗口"移出：需要新增命名管道 + ACL + 消息协议 +
安全评审，而现状（弹一个 MessageBox）虽不理想但不是故障。

### 3.9 WR-016：安装分发 —— 降级

**确证保留**两项，都便宜：
- `installer/VoiceTyper.iss` 没有任何 `[Registry]` 段，卸载不会清理 `HKCU\...\Run` 里的
  `VoiceTyper` 值 —— 卸载后残留一个指向已删除 exe 的自启项。
- `build.bat` 里 `Compress-Archive` 的 `powershell` 调用**没有检查 errorlevel**，打包失败仍然
  打印 "Build complete!"。

**移出**："`UninstallDelete` 递归删除 `{app}` 会删掉不属于安装器的文件"——对一个
`{localappdata}\Programs\VoiceTyper` 下的私有目录，这是 Inno Setup 的标准做法，原文把常规实践
写成了缺陷。原生依赖闭包（ORT DLL / VC++ 运行库）确实是待验证风险，但**只能在干净 Windows 机器
上验证**，无法在本轮修复，作为待验证项列入 R4 清单而非工单。

### 3.10 WR-017：自启状态 —— 降级

`StartupRegistration.SetEnabled` 返回 `void` 并 catch 掉一切异常，UI 无从得知写失败 —— 确证，
改成返回 `bool` 即可。

"表达用户在系统启动应用界面禁用的状态"移出：Windows 用于记录该状态的
`StartupApproved` 注册表布局**微软没有承诺稳定**，原文自己也写了"不依赖未承诺稳定的私有注册表
布局"。既然没有受支持的查询方式，就不应该把它挂成待修工单；改为在 UI 文案里说明"已注册，
请在系统启动应用中确认"。

### 3.11 WR-019：性能与内存 —— 移出本轮

整条的行动项都是"以测量决定"。没有 Windows 真机就没有测量，没有测量就不该动
`SetProcessWorkingSetSize`、线程数、自旋策略——那是拿一个能工作的基线去赌。
静音自校准"在每次加载路径都可能执行"确属事实（`CalibratePreviewWindowIfNeeded` 在每次
`LoadAsync` 成功后调用），但它的实际代价要等 R0 之后能跑起来才知道。**整条推迟到 R0–R3 完成、
拿到真机之后。**

### 3.12 WR-020：HUD 与文档 —— 降级

保留：把 `README.md` / `DESIGN.md` 里"已输入 / 已复制 / 麦克风可用 / 代码审查通过"这类过强表述
校准为实际状态。这条**成本很低而且现在就该做**——本轮分诊本身就在推翻这些描述。

移出：混合 DPI / 高对比度 / 读屏 / 键盘操作审计。这是一整轮无障碍验收，必须在真机上做，且
在主功能可用之前做没有意义。

## 4. 修复顺序与工单

五个批次严格串行；批次内的工单可并行，但**同一文件不得由两个 agent 同时改**。
每完成一个批次，更新本文状态并把结论回写 `DESIGN.md`。

| 批次 | 目标 | 工单 | 退出条件 |
| --- | --- | --- | --- |
| R0 | 让它能编译、让测试可信 | R0-1 ~ R0-3 | 主工程 + 测试工程分别编译通过；`dotnet test` 全绿或明确 Skip；两个 RID 能 publish |
| R1 | 让主链路能跑通一次 | R1-1 ~ R1-4 | 按住热键 → 录音 → 识别 → 文本真的落到记事本里 |
| R2 | 让它能重复使用 | R2-1 ~ R2-4 | 连续听写、空闲十分钟后再听写、改配置、改密钥都正常 |
| R3 | 收敛边界与故障恢复 | R3-1 ~ R3-5 | 异常路径有正确反馈，不虚报成功 |
| R4 | 分发、文档、测试补强 | R4-1 ~ R4-4 | 干净机器可装可用；文档口径与证据一致 |

**R0 必须由一个 agent 独立完成并交付**，之后才谈并行——在工程不能编译之前，任何"修好了"的
声明都无法验证。

> **进度（2026-09-06，无 SDK/无真机）** — 详见 [REPAIR_DELIVERY.md](REPAIR_DELIVERY.md)：
> R0（R0-1~R0-3）、R1（R1-1~R1-4）、R2（R2-1~R2-4）已完成代码与静态验证，**待编译与真机验证**。
> R3、R4 尚未开始。

---

### R0-1 · 恢复可编译（原 WR-001）· P0 · 低

**定位**：`VoiceTyper.csproj`、`App/AppCoordinator.cs:61`、`Asr/AsrService.cs`、
`Asr/SenseVoiceEngine.cs`、`Tests/VoiceTyper.Tests/ConfigStoreTests.cs:44`

**已确证的 5 个问题**：

1. `VoiceTyper.csproj` 无 `Compile Remove`，SDK 默认 glob `**/*.cs` 会把
   `Tests/VoiceTyper.Tests/*.cs` 编进主工程，而主工程不引用 xUnit。
2. `App/AppCoordinator.cs:61`：`_asrService.OnStateChange = _ => { _ = ReevaluateReadinessAsync(); };`
   —— lambda 形参名为 `_`（单个 `_` 不构成弃元形参，它是一个 `AsrState` 类型的具名参数），
   函数体的 `_ = ...` 因此是**对该参数赋值**，把 `Task` 赋给 `AsrState`，CS0029。
3. `Asr/AsrService.cs:146` 使用 `LlmCorrector`（位于 `VoiceTyper.Llm`），文件顶部无
   `using VoiceTyper.Llm;`，工程也无 global using。
4. `Asr/SenseVoiceEngine.cs:78,82` 使用 `AppConstants`（位于 `VoiceTyper.Support`），无对应 using。
5. `ConfigStoreTests.cs:44` 是 `public void`，形参类型 `AsrLanguage` 是 `internal` 枚举，CS0051。

**修复方案**：

- csproj 加：
  ```xml
  <ItemGroup>
    <Compile Remove="Tests/**/*.cs" />
    <None Remove="Tests/**" />
  </ItemGroup>
  ```
  首轮选局部排除而非重排目录，把变更面压到最小。
- 回调改为具名参数并显式忽略；更清晰的做法是抽一个
  `private void OnAsrStateChanged(AsrState state) { _ = ReevaluateReadinessAsync(); }`。
- 补两处 using。
- 测试方法改 `internal void`，并确认测试工程有 `InternalsVisibleTo`。
  **不要为了测试把 `AsrLanguage` 改成 public。**

**验收**：`dotnet build VoiceTyper.sln -c Release` 通过；两个 RID `dotnet publish` 通过；
检查 `dist/win-x64` 下没有 `xunit.*` 或 `VoiceTyper.Tests.dll`。
**编译器报出的其他错误一并修掉并记录**——上面 5 条不是穷举，是静态阅读能看到的部分。

---

### R0-2 · 修正必然失败的测试（原 WR-018 的一半）· P0 · 低

**定位**：`Tests/VoiceTyper.Tests/ConfigStoreTests.cs:18-30`

`HotkeyConfig.Modifiers` 默认值是 `new() { "ctrl" }`（`Core/AppConfig.cs:231`）。测试却对一个
新建 `AppConfig` 再 `Add("ctrl")`，然后断言 `Assert.Single(original.Hotkey.Modifiers)` ——
列表里有 2 个元素，**这个测试必定失败**。它能一直留在仓库里，本身就是"测试从未被执行"的证据。

**修复**：把 `original.Hotkey.Modifiers.Add("ctrl")` 改为不改变元素数量的写法，或改断言为
`Assert.Equal(new[]{"ctrl"}, original.Hotkey.Modifiers)` 并去掉那次 `Add`。
测试意图是验证深拷贝，保持这个意图。

---

### R0-3 · 消除假通过（原 WR-018 的另一半）· P0 · 低

**定位**：`Tests/VoiceTyper.Tests/FbankParityTests.cs:38,90,94`

缺夹具时 `return` 让测试显示为通过。文件里已有注释承认这是已知局限，但"已知"不等于可接受——
一个绿色的 CI 在夹具路径配错时不会告诉任何人。

**修复**：
- **必需夹具**（`macos/Tests/VoiceTyperTests/Fixtures/` 下已提交的那些）缺失时
  `Assert.Fail($"缺少必需夹具: {path}")`，不允许跳过。
- **需要真实模型**的测试用条件跳过机制明确 Skip，并在跳过原因里写清缺什么。
- **不要放宽金标准阈值**（fbank/LFR/CMVN 保持 < 1e-3）来让测试变绿。

---

### R1-1 · 重采样死循环（原 WR-002）· P0 · 中

**定位**：`Services/AudioCaptureService.cs` `Start`（`_inputBuffer` 构造）、`OnCaptureDataAvailable`

**根因（已对依赖源码确证）**：`BufferedWaveProvider` 构造函数把 `ReadFully` 置为 `true`，其
`Read` 在数据不足时**补零并返回完整请求长度**。`WdlResamplingSampleProvider.Read` 透传这一行为，
于是 `OnCaptureDataAvailable` 里的
```csharp
while ((read = resampled.Read(tmp, 0, tmp.Length)) > 0) { ...; if (read < tmp.Length) break; }
```
永远拿到 `read == tmp.Length`，**永不退出**：在 WASAPI 回调线程上无限生成静音，
`AppendSamples` 持续切出 600ms chunk 灌进 `_deliveryQueue`，内存与 CPU 双爆。
识别会话的 120 秒上限拦不住它——那个上限在会话侧，这个循环在采集侧。

注意这个 bug 还会经由 `MicPermissionProbe.TryProbe`（启动时探测麦克风会真的 `Start()` 一次）
在**应用启动阶段**就触发。

**修复**：
```csharp
_inputBuffer = new BufferedWaveProvider(_captureFormat)
{
    BufferDuration = TimeSpan.FromSeconds(2),
    DiscardOnBufferOverflow = true,
    ReadFully = false,   // ← 关键：让 Read 只返回真实可用数据，循环才能正常耗尽
};
```
另加一道保险：给 `while` 循环设单次回调的迭代上限（按 `BufferDuration` 折算的最大帧数），
超限记一次 warn 并退出。**保险不能代替 `ReadFully = false`**，两个都要。

**验收**：新增纯逻辑测试——喂入已知长度的 N 声道 PCM，断言经过 mono + 重采样后
`AppendSamples` 收到的总样本数落在 `N * 16000 / captureRate` 的 ±1 帧内，且
`OnCaptureDataAvailable` 单次调用能返回。真机上录 5 秒确认 CPU 不飙升、内存不增长。

---

### R1-2 · `INPUT` 原生布局（原 WR-003）· P0 · 低

**定位**：`Support/NativeMethods.cs:80-95`、`Services/TextInsertionService.cs:316`

**根因（已按 ABI 复算确证）**：`InputUnion` 只声明了 `KEYBDINPUT`。x64 下
`KEYBDINPUT` = 2+2+4+4(pad)+8 = 24 字节，`INPUT` = 4(type)+4(pad)+24 = **32 字节**。
原生 `MOUSEINPUT` = 4+4+4+4+4+4(pad)+8 = **32 字节**，决定了联合体大小，原生 `INPUT` = **40 字节**。
`SendInput` 收到 `cbSize=32` 会以 `ERROR_INVALID_PARAMETER` 返回 0。
**这意味着文本插入在 x64 上 100% 不工作**——不是偶发。

**修复**：按 Windows SDK 补全三个成员：
```csharp
[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}
```
并补 `MOUSEINPUT { int dx; int dy; uint mouseData; uint dwFlags; uint time; IntPtr dwExtraInfo; }`
与 `HARDWAREINPUT { uint uMsg; ushort wParamL; ushort wParamH; }`。
**不要**保留错误的联合体而把 `cbSize` 硬编码成 40——托管数组的 stride 仍是 32，元素边界会错位。

**验收**：结构体测试断言 `Marshal.SizeOf<INPUT>() == 40`（x64/arm64 都是 40）以及
`ki` 相对 `INPUT` 起始的偏移为 8。结构体测试与真机粘贴测试**分别记录，不互相替代**。

---

### R1-3 · 采集资源泄漏与多声道（原 WR-009 的最小修复）· P1 · 中

**定位**：`Services/AudioCaptureService.cs` `Start` / `Stop` / `StopWithoutResult` / `Cleanup`

**已确证的两个问题**：
1. 正常 `Stop()` 路径**从不调用 `Cleanup()`**。`MMDeviceEnumerator`（从未 Dispose）、`MMDevice`、
   `WasapiCapture` 每次听写泄漏一组，采集线程也不确定退出。
2. `sampleProvider.ToMono()` 构造 `StereoToMonoSampleProvider`，源声道数 ≠ 2 时抛
   `ArgumentException`，被外层吞成"启动录音失败"。多声道麦克风阵列完全不可用。

**修复**：
- `MMDeviceEnumerator` 用 `using`，或存字段并在 `Cleanup` 中 Dispose。
- 在 `RecordingStopped` 真正到达之后完成退订与释放：`_capture.DataAvailable -= ...`、
  `_capture.RecordingStopped -= ...`，再 Dispose capture 与 device。NAudio 的 `StopRecording`
  是**异步**的，不能在 `Stop()` 里同步 Dispose。
  **注意**：不要在持有 `_lock` 的情况下等待采集线程退出——`AppendSamples` 在音频线程上要拿同一把锁，
  会死锁。
- 声道处理显式分三种：1 声道直通；2 声道 `ToMono()`；`> 2` 声道要么实现明确的降混，
  要么抛出**可读的**、说明"暂不支持 N 声道设备"的 `AudioStartException`。
  **不要**把 stereo-to-mono 当作通用多声道转换。

**本轮不做**：per-capture 上下文对象与 generation 校验（理由见 §3.3）。

**验收**：连续 20 轮 `Start`/`Stop`，断言进程句柄数与线程数不持续增长。真机上拔 USB 麦克风后
能开始新会话。

---

### R1-4 · 钩子回调不得同步执行业务（原 WR-006 的最小修复）· P1 · 中

**定位**：`Support/UiDispatcher.cs` `Post`、`Services/HotkeyService.cs` `HookCallback`

**根因**：`UiDispatcher.Post` 的 `IsOnUiThread` 分支**同步执行**；钩子装在 UI 线程，所以
`HookCallback` 里的 `UiDispatcher.Post(() => OnPress?.Invoke())` 会在钩子回调内部直接跑完
`AudioCaptureService.Start()`（COM 设备枚举 + WASAPI 打开）。低级钩子回调超时会被系统静默摘除。

**修复**：新增 `UiDispatcher.PostAsync(Action)`（或给 `Post` 加 `forceAsync` 参数），
**始终**走 `_context.Post`，绝不走同步分支；`HotkeyService` 的三处 `OnPress`/`OnRelease`/`OnCancel`
投递全部改用它。钩子回调此后只做按键判定、状态更新和投递，立刻 `return`。

**不做**：独立钩子消息线程（理由见 §3.1）。
**不要**在注释或文档里写死 `LowLevelHooksTimeout` 的具体毫秒数——那个值没有被验证过。

**验收**：单元测试断言 `PostAsync` 在 UI 线程调用时不同步执行（回调里置位一个标志，调用返回后
立即检查仍为 false）。真机上按住热键时钩子响应不受录音启动耗时影响。

---

### R2-1 · 热键消费与按键状态机（原 WR-007）· P1 · 高

**定位**：`Services/HotkeyService.cs` `HookCallback` / `ReadCurrentModifiersExcluding`

**已确证的 3 个问题**：

1. **热键穿透**：所有分支都 `return CallNextHookEx(...)`，被接管的主键 down/up 和取消用的 Esc
   **原样传给前台应用**。用 Ctrl+F2 听写时目标应用会同时收到 Ctrl+F2；Esc 取消会同时关掉对话框。
2. **左右修饰键**：钩子的 `vkCode` 给的是 `VK_LCONTROL(0xA2)`/`VK_RCONTROL(0xA3)` 等具体键，
   而 `ReadCurrentModifiersExcluding` 的 `switch` 只匹配 `VK_CONTROL(0x11)`/`VK_MENU`/`VK_SHIFT`。
   于是"先松修饰键"这条路径的 `mask` **不会被清掉**，`afterRelease == _expectedModifiers`，
   `OnRelease` 不触发 —— 用户先松 Ctrl 再松 F2 时录音不会在松 Ctrl 时结束。
   （`IsModifierVk` 倒是把 0xA0–0xA5 都列全了，所以会进入这个分支再什么都不做。）
3. **Esc 后重复触发**：Esc 分支把 `_isActive = false`，但用户手指还按在主键上，auto-repeat 的
   keydown 会命中 `isKeyDown && vk == _targetVk && currentMods == _expectedModifiers && !_isActive`
   —— **立刻重新开始录音**。取消变成了重启。

**修复**：引入显式状态机（`Idle` / `Engaged` / `AwaitingFullRelease`）：
- 归一化左右键：`VK_LCONTROL/VK_RCONTROL → Ctrl`，`VK_LMENU/VK_RMENU → Alt`，
  `VK_LSHIFT/VK_RSHIFT → Shift`，`VK_LWIN/VK_RWIN → Win`。
- **自己维护物理按键状态**，由 key down/up 事件更新，不要在当前事件尚未反映到系统状态时用
  `GetAsyncKeyState` 推断释放。同时按住左右 Ctrl 时，松开其中一个不应判定为 Ctrl 已释放。
- Esc 取消后进入 `AwaitingFullRelease`，直到主键**真正抬起**才回到 `Idle`；auto-repeat 在该状态
  下被吞掉。
- 已接管的主键 down/repeat/up 与生效的 Esc **返回 `(IntPtr)1` 消费掉**，不再 `CallNextHookEx`。
  修饰键事件**必须继续传递**，否则会破坏其他应用看到的修饰键配对（一个只收到 down 没收到 up 的
  Ctrl 会让目标应用卡在 Ctrl 状态）。

**同时修**（`UI/SetupForm.cs` 热键页）：当前主键是自由文本框，`MapKeyToVk` 返回 0 时
`HotkeyService.Start` 抛 `HotkeyServiceException`，但保存流程可能已提示"已保存并生效"。
改为保存前用 `HotkeyService.IsSupportedKey` 即时校验并给出明确错误；空修饰键同样拒绝。

**不做**：切换式（按一下开始/再按一下结束）录音模式；`RegisterHotKey` 冲突检测。
两者都是增强，且 `RegisterHotKey` 不提供本项目需要的释放语义。

**验收**：先松主键 / 先松修饰键 / 左右键混按 / auto-repeat / Esc 后继续按住 / 暂停恢复。
真机确认目标应用不再收到热键，且普通输入与中文 IME、AltGr 不受影响。

---

### R2-2 · 空闲卸载后无法恢复（原 WR-004）· P0 · 低

**定位**：`Asr/AsrService.cs:146` `MakeSession`

**根因**：
```csharp
if (State is not (AsrState.Ready or AsrState.Loading or AsrState.SuspendedForIdle))
{
    _ = PreloadAsync();
}
```
`SuspendedForIdle` 被排进了"不需要加载"的集合，但该状态下引擎**已经被 Dispose 且引用已置 null**。
于是新会话拿到的 `CurrentEngine` 恒为 null，`LocalAsrSession.WaitForEngineThenFinalize` 每 100ms
轮询、共 50 次，5 秒后报"识别引擎尚未就绪"。默认 `IdleUnloadMinutes` 下，**空闲十分钟后的每一次
听写都会失败，且不会自愈**。macOS 侧 `makeSession` 只排除 Ready/Loading，Windows 这里是直译时
多加的。

**修复**：把 `SuspendedForIdle` 从排除集合里去掉：
```csharp
if (State is not (AsrState.Ready or AsrState.Loading))
{
    _ = PreloadAsync();
}
```
`PreloadAsync` 自身的 guard（`State is Loading or Ready` 才提前返回）已经正确，不需要改。

**顺带**（与 R2-3 同一次改动）：`LocalAsrSession` 目前靠 100ms 轮询等引擎。改为等待
`AsrService` 暴露的**同一个可取消加载 Task**，加载失败时立刻把错误传给会话，而不是空转 5 秒再
报一句无信息量的"尚未就绪"。

**验收**：用可注入的时钟/计时器测试 `Ready → 空闲卸载 → MakeSession → 加载 → final`；
覆盖"模型加载慢于用户说话"、"加载失败"、"加载期间取消"。真机走一次默认十分钟空闲后的再听写。

---

### R2-3 · 模型重载无限循环（原 WR-005）· P0 · 中

**定位**：`Asr/AsrService.cs` `RequestLoadAsync` / `UnloadNowAsync`、
`App/AppCoordinator.cs` `ReevaluateReadinessAsync` / `ReloadModelAsync`

**根因（已逐步确证的同步重入环）**：从 `Ready` 点"重新加载模型"：

1. `ReloadAsync()` → `RequestLoadAsync(unloadFirst: true)`，置 `_isLoadInFlight = true`；
2. `UnloadNowAsync(false)` 在 `State == Ready` 时 `SetState(Unloaded)`；
3. `SetState` **同步**调用 `OnStateChange` → `ReevaluateReadinessAsync()`；该方法在到达
   `switch` 之前没有真正的 await，所以整段同步执行；
4. `case AsrState.Unloaded:` → `_ = _asrService.PreloadAsync()` → `RequestLoadAsync(false)`；
5. `_isLoadInFlight` 仍为 true → 置 `_reloadRequestedWhileLoading = true` 返回；
6. 第 1 步的加载完成后，看到该标志 → 再次 `RequestLoadAsync(unloadFirst: true)` → **回到第 2 步**。

每一轮都会构建一个 ~500MB 的 ORT session。这是无限循环，不是"多加载一次"。

**修复**：把两个语义彻底分开：
- `EnsureLoaded()`：若已有加载在飞，**共享当前 Task**，不设置任何"再来一次"标志。
  `AppCoordinator` 的自动预加载路径全部走这个。
- `Reload(configVersion)`：只有**真正的新配置版本**才产生一次合并重载。给 `AsrConfig` 加一个
  单调递增的版本号或内容哈希，`RequestLoadAsync` 只在"目标版本 ≠ 已加载版本"时才排队。
- `UnloadNowAsync` 内部的状态迁移**不应触发面向用户的自动预加载**。最干净的做法是让
  `ReevaluateReadinessAsync` 不再从 `Unloaded` 无条件发起预加载，改由显式入口驱动。
- `_isLoadInFlight` 的清除放进 `finally`，覆盖异常、取消和退出路径。

**同时修**：`AppCoordinator.ReloadModelAsync` 直接调 `ReloadAsync`，**没有活跃听写检查**
（`ApplyConfigAsync` 有，这条路径漏了）。录音/识别/纠错期间点"重新加载模型"会把正在使用的引擎
Dispose 掉。加上与 `ApplyConfigAsync` 相同的 `_currentState.State.IsActiveDictation()` 拒绝逻辑
并给用户明确提示。

**验收**：用假引擎统计构造/释放次数——一次点击**只能**产生 1 次构造 1 次释放；加载中重复
`EnsureLoaded` 不追加重载；连续改三次配置最终只应用最新一次；活跃听写期间重载被拒绝且引擎未被
释放。**不要靠肉眼看日志猜。**

---

### R2-4 · API Key 保存事务（原 WR-014）· P1 · 中

**定位**：`UI/SetupForm.cs` `HandleSaveRecognition`、`App/AppCoordinator.cs` `ApplyConfigAsync`、
`Core/VoiceTyperController.cs` 构造器

**已确证的两个问题**：

1. **部分生效**：`HandleSaveRecognition` **先** `OnSaveLlmApiKey(...)` 落盘，**再**
   `await OnSaveConfig(draft)`。而 `ApplyConfigAsync` 在活跃听写期间会抛
   `InvalidOperationException`。结果：密钥已经写进 DPAPI，用户看到的却是"保存失败"。
2. **运行态不同步**：`AsrConfigEquals`/`LlmConfigEquals`/`HotkeyConfigEquals` **都不包含密钥**，
   所以"只改密钥"会被判为 `onlyUiChanged == true`，走不重建控制器的分支。而
   `LlmCorrector` 的 ApiKey 是在 `VoiceTyperController` **构造时**从 `SecretStore` 读一次的
   （`Core/VoiceTyperController.cs:64`）。于是新密钥要等下次重启才生效——但"测试纠错"按钮用的是
   输入框里的新值，**测试通过、实际听写仍用旧密钥**。这个组合特别容易骗过验收。

**修复**：
- 先做全部校验、先判断"当前是否允许应用"，**通过之后**再依次写密钥和配置。任一步失败给出准确
  的分状态提示（密钥写失败 / 配置写失败 / 活跃听写拒绝），不出现虚假成功。
- 把"密钥已变化"作为一个**显式的更新信号**传给 `ApplyConfigAsync`，让它走重建或安全更新分支。
  **绝不能**把明文密钥放进配置比较、日志或诊断信息里——这条是 `AGENTS.md` 的硬约束。
- 密钥文件写入用原子替换（写临时文件 + `File.Replace`），保持 DPAPI 当前用户保护。

**顺带**（低成本，同一处）：`LlmCorrector` 的请求应接受并传播外部 `CancellationToken`；
目前只有一个内部超时 CTS（`Llm/LlmCorrector.cs:197`），会话取消后请求仍在跑、迟到结果仍可能被使用。

**验收**：只改密钥后**实际听写**用新密钥（用假 HTTP 服务端断言 Authorization 头）；
密钥写失败 / 配置写失败 / 活跃听写拒绝三种情况都无虚假成功；取消会话后无迟到插入；
日志、YAML、诊断包里搜不到密钥。

---

### R3-1 · 单活跃听写的最小收口（原 WR-008 收窄）· P2 · 中

**定位**：`Core/VoiceTyperController.cs` `BeginRecording` / `DiscardShortSession`

只修 §3.2 里确证的两个缺口，**不做控制器重构**：

1. `BeginRecording` 除 `_isRecording` 外，还要在**上一会话仍处于 Recognizing/Inserting** 时拒绝
   新会话并给出用户可见反馈（当前 `_asrSession != null` 即可作为判据）。
   顺带处理 `OnFinal` 的 `else` 分支：旧会话完成时若已有新会话在录音，**不应再插入文本**，
   改为走"结果已复制"路径。
2. `_discardCurrentSession` 从实例字段改为**随会话捕获的局部状态**（放进传给 tail 回调的闭包，
   或记在 `LocalAsrSession` 上），消除"新一轮 `BeginRecording` 把它重置掉"的竞态。

**验收**：可控假音频源 + 假识别服务，测试连按热键、短录音后立刻再按、各阶段取消、迟到 final。
断言**每个会话至多插入一次**，且结束后不再改动 UI 或剪贴板。

---

### R3-2 · 剪贴板与插入反馈的确证修复（原 WR-010 收窄）· P1 · 中

**定位**：`Services/TextInsertionService.cs`、`Core/VoiceTyperController.cs` `InsertFinalText`

四个已确证子问题（详见 §3.4），逐条修：

1. `CopyToClipboard` 改为返回 `bool`；`InsertFinalText` 的 `FocusChanged` 与 `Failed` 分支据此
   区分"结果已复制到剪贴板，可手动粘贴"与"复制也失败了，请重新听写"。
   **不能用 API 调用成功证明目标控件收到了文本。**
2. `ApplyClipboardExclusionFormats` 对 `CanIncludeInClipboardHistory` 与
   `CanUploadToCloudClipboard` 写 **4 字节 DWORD**（值 0）。
   `ExcludeClipboardContentFromMonitorProcessing` 保持现状（只要求格式存在）。
   提交信息里**不要**写"修复隐私泄漏"——第三个标记可能一直是生效的，未经真机验证不能这么断言。
3. `IsForegroundWindowElevated`：改为 `OpenProcessToken` + 查询 `TokenIntegrityLevel`
   （或 `TokenElevation`），与自身进程比较。**无法判定时返回"未知"而不是 `false`**，
   提示文案相应改为不确定语气。当前实现几乎恒返回 `false`，UIPI 提示等于没有。
4. `SnapshotClipboard` 区分三种结果：**成功且为空 / 成功有内容 / 读取失败**。
   只有"成功且为空"才允许 `RestoreClipboardSnapshotUnconditionally` 执行 `Clipboard.Clear()`；
   "读取失败"时**优先保留识别结果**、不去动用户剪贴板，并告知用户自行复制。

**顺带**（便宜且相关）：插入前检查是否仍有物理修饰键按下（Alt/Shift/Win 会改变 Ctrl+V 的含义），
有则短暂等待或放弃插入改为复制。

**本轮不做**：带 ID 的插入事务、逐格式保真快照、"等目标确认粘贴完成"。
后两者是方案固有限制，写进文档（见 R4-3）。

**验收**：Windows STA 集成测试覆盖纯文本 / 图片 / 文件列表 / 原所有者已退出 / 剪贴板被占用 /
恢复期间用户复制了新内容。跨应用接受情况**必须真机验证**，不能用测试替代。

---

### R3-3 · 麦克风状态误报（原 WR-011 收窄）· P1 · 低

**定位**：`Core/MicPermissionProbe.cs`、`App/AppCoordinator.cs` `ProbeMicrophone`、
`UI/SetupForm.cs` `UpdateStatus`

**根因**：`MicPermissionProbe.TryProbe(out var denied); return denied;` —— **返回值被丢弃**。
"无设备"、"设备忙"、"初始化失败" 全部落进 `denied == false`，UI 显示"麦克风可用"。

**修复**：把 `TryProbe` 换成结构化结果：
```csharp
internal enum MicProbeResult { Available, NoDevice, AccessDenied, DeviceFailure, Unknown }
```
`AppCoordinator` 与 `SetupForm` 按枚举分别展示，权限拒绝时给出正确的 Windows 隐私设置入口。
探测保持串行且可取消，不要出现重叠轮询。

**注意**：该探测会真的 `Start()` 一次采集，**依赖 R1-1 已修**——否则启动探测就会踩进死循环。

**本轮不做**：设备选择下拉、电平表（理由见 §3.5）。

---

### R3-4 · 下载超时与取消（原 WR-013 收窄）· P1 · 低

**定位**：`Asr/ModelDownloader.cs:47,87,178`

**根因**：`HttpClient { Timeout = Timeout.InfiniteTimeSpan }` + `volatile bool _cancelled`，
而 `_cancelled` 只在读循环迭代之间检查。卡在 `ReadAsync` 上的连接**永不超时、永不可取消**。

**修复**：`Cancel()` 改为 `CancellationTokenSource.Cancel()`；该 CTS 与调用方 `ct`
用 `CreateLinkedTokenSource` 关联，并把 token 传进 `SendAsync` / `ReadAsync` / `WriteAsync`。
设置连接超时与"无进度超时"（例如 30s 内字节数无增长即失败），**并在代码注释里写明取值理由**。
退出时取消并释放。

**本轮不做**：206/200/416 全矩阵的可控 HTTP 测试服务器（续传逻辑本身读下来是正确的，
原报告未指出其中的实际缺陷）。列入 R4。

---

### R3-5 · 钩子自愈的两个缺陷（原 WR-006 剩余）· P2 · 中

**定位**：`Services/HotkeyService.cs` `CheckHookHealth` / `Start`

1. `GetLastInputInfo` **包含鼠标输入**。用户只动鼠标不打字时，`sinceUserInputMs` 很小而
   `sinceHookActivityMs` 很大 → 误判钩子失活 → 反复重装。
2. `CheckHookHealth` 调 `Start(hotkey)`，而 `Start` 开头就 `Stop()`——`Stop` 会销毁
   `_healthTimer`。若随后的 `SetWindowsHookExW` 失败，**健康计时器一并没了**，
   注释里说的"等下一个周期再试"不会发生，热键永久失效且无提示。

**修复**：恢复调度的所有权独立于当前钩子实例（健康计时器不随 `Stop` 销毁，或重装失败后重建）；
失败按退避重试并在托盘/设置里显示**实际不可用**状态；不要把鼠标活动当作失活证据。
恢复成功后清理按键状态，并协调正在录音的会话正常收尾。

**验收**：只移动鼠标不触发重装；安装失败后能持续重试并有可见状态；暂停/退出期间不会自动重启监听。
日志**不得记录普通用户按键内容**。

---

### R4-1 · 单实例范围（原 WR-015 收窄）· P2 · 低

`Program.cs:11` 的 `Global\VoiceTyper.Unified.SingleInstance` 改为 `Local\`（会话范围），
并处理 `Mutex` 构造可能抛出的 `UnauthorizedAccessException` / `WaitHandleCannotBeOpenedException`。
若决定允许同一用户多个登录会话，需在文档中写明配置目录与模型下载目录的共享写入策略。

**不做**：IPC 激活已有实例窗口（理由见 §3.8）。

---

### R4-2 · 安装与自启的残留（原 WR-016 / WR-017 收窄）· P2 · 低

1. `installer/VoiceTyper.iss` 增加 `[Registry]` 段，用
   `Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "VoiceTyper"; Flags: uninsdeletevalue`
   清理自启项。**只移除本应用拥有的注册项。**
2. `build.bat` 的 `Compress-Archive` 调用后检查 `errorlevel` 并向上传递失败；
   当前打包失败仍会打印 "Build complete!"。
3. `Support/StartupRegistration.cs` 的 `SetEnabled` 改为返回 `bool`；`UI/SetupForm.cs`
   `HandleSaveGeneral` 与 `UI/TrayController.cs` 在写失败时**恢复勾选状态**并提示，
   两个入口读取同一状态源。

**不做**：`StartupApproved` 私有注册表布局的系统禁用态查询（微软未承诺稳定）。
改为在 UI 文案里说明"已注册，请在系统启动应用中确认"，并提供跳转入口。

---

### R4-3 · 文档口径校准（原 WR-020 收窄）· P2 · 低

把 `windows/README.md`、`windows/DESIGN.md` 中以下表述改为与实际证据一致：

- "代码审查通过" ≠ "真机验证通过"，逐处标注。本轮分诊已推翻其中若干"已对齐"的描述。
- "已输入 / 已复制 / 麦克风可用" 等结果性文案，改为反映 R3-2 / R3-3 引入的真实状态区分。
- 把"剪贴板 + Ctrl+V 无法确认目标已接收"、"1 秒恢复窗口对慢应用可能不足"作为**已知限制**写入
  设计文档，而不是留作待修缺陷。
- 系统支持范围按 .NET 10 的实际矩阵与本项目的验收矩阵描述，清理不符的旧系统暗示。
- 区分"未签名内部验证"与"适合公开发布"；签名不保证消除 SmartScreen 提示。

**不做**：混合 DPI / 高对比度 / 读屏审计（需真机，且应在主功能可用后进行）。

---

### R4-4 · 测试覆盖补强（原 WR-018 剩余）· P2 · 中

R0 修完断言与假跳过之后，按每项修复补能揭示**用户可见故障**的回归测试，而不是验证私有布尔表达式：

- 原生结构体布局（R1-2）、重采样样本数（R1-1）、采集资源不增长（R1-3）
- 引擎生命周期计数（R2-3）、空闲恢复时序（R2-2）
- 热键状态机的按键序列（R2-1）、剪贴板事务（R3-2，需 STA）
- 下载故障注入（R3-4，可控 HTTP 服务端）
- 端到端：复用 `macos/Tests/VoiceTyperTests/Fixtures/speech_zh_en_mixed.wav`，
  执行编辑距离 ≤ 2 的既有标准并**记录实际差异**

分组要求：Windows STA 集成测试与纯逻辑测试分开；通过接口 / 时钟 / 路径 / HTTP 客户端注入隔离
系统依赖，**不读写开发者真实配置、密钥和剪贴板**。CI 输出必须区分通过 / 失败 / 跳过及跳过原因。

## 5. 给实施 agent 的约束

1. **先读根 `AGENTS.md`**，再读本文对应工单，最后核对代码是否仍符合描述。
   定位以文件和方法名为准，行号可能已随修复变化。
2. **不要为了让清单变绿而改正确的代码。** 若复现推翻了某项结论，记录证据并更新本文该项。
3. **区分"代码审查通过"和"真机验证通过"。** 涉及 Win32、音频、剪贴板、安装、性能的工单，
   在只有代码检查时**不得标记最终完成**。没有真机时交付未执行清单，不要填"全部验证通过"。
4. **不改识别管线。** fbank / LFR / CMVN / CTC / 文本后处理 / 模型 I/O 契约若被触及，
   必须同步评估 macOS 并继续共用金标准夹具。本轮所有工单都不应触及这些。
5. **日志约束**：不记 API Key、完整敏感配置、用户按键内容或听写正文。
6. R0 完成前不要开始 R1；**每个批次内同一生命周期文件不得由两个 agent 同时修改**
   （`AudioCaptureService` / `VoiceTyperController` / `AsrService` / `LocalAsrSession` 尤其如此）。

### 交付模板

```text
工单 ID：
修复提交：
问题复现或静态证据：
修改后的行为：
涉及接口/配置变更：
自动化测试及结果（含跳过原因）：
Windows 验证环境（系统 build、架构、SDK/应用版本、设备）：
手工场景及结果：
未完成或仍需真机验证的部分：
macOS 影响评估：
```

状态：待修复 → 修复中 → 代码与自动化验证完成 → 真机验证完成。
