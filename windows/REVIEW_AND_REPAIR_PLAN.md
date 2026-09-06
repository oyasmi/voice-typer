# Windows 实现审查与修复计划

本文是供修复 agent 执行的任务说明，覆盖真机验证前发现的问题、修复方向、优先级和验收要求。

[项目入口](../README.md) · [Windows 使用说明](README.md) · [架构设计](DESIGN.md)

## 1. 审查基线与使用方法

- 审查日期：2026-09-06。
- 审查代码基线：`86424eb`，Windows 应用版本 `3.2.1`。
- 方法：Windows 源码、测试、构建与安装脚本审查；对照当前 macOS 实现；核对 Microsoft 文档与 NAudio 2.2.1 源码。
- 环境：Linux，没有可用的 .NET SDK，也没有 Windows 真机。本轮没有完成编译、运行测试、安装或性能测量。
- 已做的辅助验证：按固定宽度 Win32 类型进行 64 位结构体布局计算，当前 `INPUT` 为 32 字节，完整原生布局为 40 字节。这不是 Windows API 调用实测。
- 本文记录问题和方案，不表示修复已实施。全部任务初始状态为“待修复”或“待验证”。

修复 agent 开始前应重新读取根 `AGENTS.md`、检查工作区变化，并确认对应代码仍符合本文描述。定位以文件和方法名为准，行号可能随修复变化。若复现推翻某项结论，记录证据并更新该项，不要为满足清单而修改正确代码。

本文对当前缺陷的说明优先于 `DESIGN.md` 中历史性的“已对齐”“代码审查通过”等描述；原设计文档仍保留架构决策和历史证据。本文阶段使用 A–G 命名，避免与原设计文档的 P0–P8 实施阶段混淆。

### 严重程度、证据和难度

| 标记 | 定义 |
| --- | --- |
| P0 | 阻断构建、录音或自动输入，必须在基本功能验收前修复 |
| P1 | 影响主要使用路径、结果完整性或故障恢复，进入日常使用测试前应修复 |
| P2 | 体验、维护与发布完善项；其中发布相关项应在公开发布前完成 |
| S：静态确认 | 源码可明确证明缺陷或缺失的保护，不等同于已在真机复现 |
| C：契约确认 | 结合依赖源码或官方 API 契约确认；实际表现仍需运行验证 |
| V：待验证风险 | 代码存在风险或缺少证据，不能直接断言所有机器必然失败 |
| R：改进建议 | 产品或工程取舍，不把建议描述成已经发生的故障 |

| 难度 | 评估含义 |
| --- | --- |
| 低 | 修改局部，依赖少，主要通过编译或聚焦测试验证 |
| 中 | 涉及数个模块或平台行为，需要自动化测试和针对性的 Windows 验证 |
| 高 | 涉及线程、资源所有权、状态机或外部应用兼容性，需要分步实施与故障注入 |

难度是相对工程复杂度，不是完成时间承诺；Windows 环境、设备和目标应用准备时间另计。

## 2. 任务总览

| ID | 问题或任务 | 严重程度 | 难度 | 证据 | 批次 |
| --- | --- | --- | --- | --- | --- |
| WR-001 | 工程包含测试源码及多处 C# 编译错误 | P0 | 低 | S/C | A |
| WR-002 | 重采样循环与自动补零行为冲突 | P0 | 中 | C | B |
| WR-003 | SendInput 的 INPUT 联合体布局错误 | P0 | 低 | C | B |
| WR-004 | 空闲卸载后不触发引擎恢复 | P1 | 中 | S | C |
| WR-005 | 模型重载反馈循环及活跃会话重载 | P1 | 高 | S | C |
| WR-006 | 热键钩子同步执行录音启动，自愈逻辑不完整 | P1 | 高 | S/C | D |
| WR-007 | 热键穿透、修饰键释放及取消后重复触发 | P1 | 高 | S/C | D |
| WR-008 | 重叠听写共享状态、旧会话迟到回调 | P1 | 高 | S | C |
| WR-009 | 音频停止、资源释放及设备代际管理缺失 | P1 | 高 | S/C | B/C |
| WR-010 | 文本插入事务、剪贴板恢复及失败提示不可靠 | P1 | 高 | S/C/V | D |
| WR-011 | 麦克风状态误报，设备选择与检测不足 | P1 | 中 | S/R | E |
| WR-012 | 首次启动缺少可见的完整试用流程 | P1 | 中 | S/R | E |
| WR-013 | 下载不能及时取消，故障分类与修复路径不足 | P1 | 中 | S/C | E |
| WR-014 | 只修改 API Key 不更新运行中的纠错器 | P1 | 中 | S | C |
| WR-015 | 全局单实例跨会话互斥，二次启动不能激活窗口 | P1 | 中 | S/C | F |
| WR-016 | 安装升级卸载与原生依赖缺少验收 | P1 | 中 | S/V | F |
| WR-017 | 自启注册状态和系统实际状态不一致 | P2 | 中 | S/V | F |
| WR-018 | 测试假通过、错误断言和关键覆盖缺失 | P1 | 中 | S | A–G |
| WR-019 | 资源与延迟优化缺少 Windows 测量依据 | P2 | 中 | S/V/R | G |
| WR-020 | HUD、支持范围、签名和文档承诺需校准 | P2 | 中 | S/V/R | F/G |

## 3. 修复边界与目标架构

继续采用单进程本地桌面架构，保留 .NET 10、WinForms、WASAPI、ONNX Runtime CPU、SenseVoice-Small int8 和剪贴板加系统输入模拟。不引入独立服务端，不默认启用 LLM，不将音频发送到网络。

本轮不要求更换 UI 框架、移植整套通用输入法、实现 GPU/NPU 推理或自动更新系统。切换式录音、性能策略等建议按对应任务中的“最小修复”和“后续增强”区分实施，避免阻断基本可靠性修复。

建议明确四个所有权边界：

| 组件 | 所有权与责任 |
| --- | --- |
| 热键线程 | 独立消息循环；维护物理键状态并决定是否消费事件，异步通知业务 |
| 音频工作线程/上下文 | 拥有采集器、设备和重采样器；顺序投递音频，完成停止与释放 |
| UI 控制器 | 一个活跃听写对象；按会话身份处理事件；驱动 Recording → Recognizing → Inserting |
| ASR 串行执行队列 | 串行加载、推理和释放引擎；共享加载任务，防止旧会话访问已释放引擎 |

模型准备状态、听写状态和用户暂停状态应分别维护，再派生托盘/HUD 状态。不能让下载或加载通知随意覆盖正在进行的听写状态。

## 4. 逐项修复说明

### WR-001：编译阻断

**P0 · 低 · S/C。** 定位：[VoiceTyper.csproj](VoiceTyper.csproj)、[AppCoordinator](App/AppCoordinator.cs) 构造器、[AsrService](Asr/AsrService.cs) `MakeSession`、[SenseVoiceEngine](Asr/SenseVoiceEngine.cs) 构造器、[ConfigStoreTests](Tests/VoiceTyper.Tests/ConfigStoreTests.cs)。

问题：主工程未排除 `Tests/**/*.cs`，SDK 会递归编译测试源码，但主工程不引用 xUnit。另有以下独立编译问题：

- `_asrService.OnStateChange = _ => { _ = ReevaluateReadinessAsync(); };` 中单个 `_` 是 `AsrState` 参数，不能接收 `Task`。
- `AsrService` 使用 `LlmCorrector`，缺少 `VoiceTyper.Llm` 导入。
- `SenseVoiceEngine` 使用 `AppConstants`，缺少 `VoiceTyper.Support` 导入。
- 公开测试方法 `AsrLanguageParse_FallsBackToAutoOnUnknown` 使用内部枚举参数，存在可访问性不一致。

修复：排除主工程内的测试源码及测试产物内容，或将项目整理为互不嵌套的源码布局；首轮优先选择局部排除以降低变更。修正回调参数名、导入和测试参数设计，不为测试方便随意扩大生产类型公开范围。

验收：主工程和测试工程分别编译；x64/arm64 发布均成功；检查主程序集和发布目录没有混入测试程序集或测试工程输出。编译器发现的其他错误一并记录，不能将上述列表当成完整的编译错误穷举。[SDK 规则][sdk]

### WR-002：重采样循环不能正常耗尽

**P0 · 中 · C。** 定位：[AudioCaptureService](Services/AudioCaptureService.cs) `Start`、`OnCaptureDataAvailable`。

问题：`BufferedWaveProvider` 默认 `ReadFully = true`，数据不足时补零并返回请求长度；当前循环不断调用 `resampled.Read()`，直到返回 0 或短读。两者结合会持续生成静音、占用采集线程并堆积投递任务。识别会话的 120 秒缓存上限不能终止这个循环。[依赖源码][buffered]

最小修复：关闭自动补零，并按实际输入帧数限定每次回调的转换量；明确区分“暂时无输入”和“最终结束”，检查 WDL 的缓冲及尾部处理。不要仅增加任意循环次数上限来掩盖样本计数错误。

验收：无真实麦克风的测试覆盖 16/44.1/48kHz、单/双声道、多次不规则分块、空输入和结束刷新；输出时长与输入时长在明确的滤波延迟容差内一致；缓冲耗尽及时返回；停止不制造无限静音。真机观察首次录音 CPU、投递队列长度和停止耗时。[WDL 源码][wdl]

依赖：WR-001；与 WR-009 由同一批修改统筹，避免两套重采样器生命周期。

### WR-003：INPUT 原生布局错误

**P0 · 低 · C。** 定位：[NativeMethods](Support/NativeMethods.cs) `INPUT/InputUnion`、[TextInsertionService](Services/TextInsertionService.cs) `SendCtrlV`。

问题：联合体只有 `KEYBDINPUT`，缺少决定原生联合体大小的 `MOUSEINPUT`；当前 64 位结构为 32 字节，原生要求 40 字节，传入的 `cbSize` 不符，输入注入失败。[INPUT 定义][input]、[SendInput 契约][sendinput]

修复：按 Windows SDK 补全 `MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT` 联合体，或引入可靠的生成式 Win32 声明。不要仅将调用参数硬编码为 40 而保留错误的数组元素布局。

验收：在实际 x64/arm64 进程验证 `Marshal.SizeOf<INPUT>() == 40` 及关键字段偏移；普通记事本接受固定中文、英文、数字和换行文本。结构体测试与真实粘贴测试分别记录，不相互替代。

### WR-004：空闲恢复失效

**P1 · 中 · S。** 定位：[AsrService](Asr/AsrService.cs) `MakeSession`、[LocalAsrSession](Asr/LocalAsrSession.cs) `WaitForEngineThenFinalize`。

问题：`MakeSession` 将 `SuspendedForIdle` 与 Ready/Loading 一起排除在预加载条件外；引擎已释放，新会话只能积攒音频并在结束等待后失败。当前 macOS `makeSession` 只排除 Ready/Loading。

修复：从 SuspendedForIdle 确实发起加载；会话等待同一个可取消的加载任务，加载错误及时传递，避免轮询一个永远不会就绪的引用。加载期间保留有上限的音频，界面仍正确表达正在录音。

验收：通过可控计时器测试 Ready → 空闲卸载 → 按键 → 加载 → final；覆盖模型加载慢于用户说话、加载失败、期间取消。真机完成默认十分钟空闲后的再次听写。

依赖：与 WR-005 共用引擎生命周期实现，不分别补两个加载入口。

### WR-005：模型加载反馈循环与会话中的重载

**P1 · 高 · S。** 定位：[AsrService](Asr/AsrService.cs) `RequestLoadAsync/UnloadNowAsync`、[AppCoordinator](App/AppCoordinator.cs) `ReevaluateReadinessAsync/ReloadModelAsync`。

问题：Ready 状态手动重载时，卸载同步通知 Unloaded；协调器立即调用 Preload；正在执行的加载将其记为“完成后再加载一次”，下一轮卸载再次产生相同请求。模型按钮还可在活跃听写期间直接重载，旧识别缓冲仍持有旧引擎。

修复：区分 `EnsureLoaded` 与配置版本变化导致的 `Reload`；前者共享当前任务，不设置重复加载标志；仅真正的新配置版本产生合并重载。所有标志在异常、取消和退出路径正确释放。活跃听写期间禁止或延后重载，按单一策略实现并提示用户。

验收：一次点击仅产生预期的一次重载；加载中重复 ensure 不追加重载；多次修改配置最终只应用最新版本；录音、最终识别、纠错期间重载不释放正在使用的引擎；失败后可重试。用假引擎统计创建/释放次数，不依赖手工观察日志猜测。

### WR-006：热键线程与存活恢复

**P1 · 高 · S/C。** 定位：[HotkeyService](Services/HotkeyService.cs) `HookCallback/CheckHookHealth/Start`、[UiDispatcher](Support/UiDispatcher.cs) `Post`。

问题：钩子安装在 UI 线程，`UiDispatcher.Post` 在同一线程直接执行，因此回调同步进入麦克风启动等业务。Windows 10 1709 之后低级钩子的最大超时为 1000ms，原注释的 5000ms 不能作为依据。UI 被阻塞也影响钩子响应。[官方契约][hook]

自愈另有两个缺陷：`GetLastInputInfo` 包括鼠标输入，不能证明键盘钩子失活；重装时 `Start` 先 `Stop`，安装失败会连健康计时器一并销毁，不能按注释所述在下个周期重试。

修复：独立钩子消息线程，回调仅完成按键判定和必要消费；业务始终异步投递。恢复调度的所有权独立于当前钩子，失败可退避重试并显示实际不可用状态；不要把鼠标活动当成确定失活证据。恢复后清理按键状态，并协调正在录音的会话收尾。

验收：阻塞 UI 数秒后不出现永久死键；安装失败后能够重试；只有鼠标活动不会反复重装；暂停/退出期间不会自动重启监听；日志不记录普通用户按键内容。

依赖：WR-008/009 提供线程安全的业务入口；与 WR-007 一起验证事件顺序。

### WR-007：热键消费和物理按键状态

**P1 · 高 · S/C。** 定位：[HotkeyService](Services/HotkeyService.cs) `HookCallback/ReadCurrentModifiersExcluding`、[SetupForm](UI/SetupForm.cs) 热键页。

问题：识别热键和 Esc 取消后仍继续向前台应用传递；左右 Ctrl/Alt/Shift 被识别但未在释放排除逻辑中完整处理；Esc 清除激活状态后，尚未释放的主键 auto-repeat 可以重新触发录音。

最小修复：用显式状态机表示未按下、已接管组合键、等待整组释放等状态；归一化左右键，正确处理同时按住左右修饰键；按键事件更新状态，避免在当前事件尚未更新的系统异步状态上推断释放。消费已经接管的主键 down/repeat/up 和有效取消事件，不破坏其他应用收到的修饰键配对。

热键设置应使用按键录制与即时合法性校验；未知修饰键、空修饰键、不可支持主键不能显示“已保存并生效”后默默回退到默认。

后续增强：可选“按一下开始/再按一下结束”模式；可用 `RegisterHotKey` 获取注册冲突反馈，但它不提供完整的释放语义，注册成功也不证明与所有应用内部快捷键无冲突。不将切换模式列为本轮 P1 最小修复的必备条件。[注册契约][register]

验收：先松主键/先松修饰键、左右键、auto-repeat、Esc 后继续按住、暂停恢复、其他应用快捷键、中文 IME 与 AltGr 场景；确认接管事件不再触发目标应用动作，普通输入不受影响。

### WR-008：单活跃听写与统一收尾

**P1 · 高 · S。** 定位：[VoiceTyperController](Core/VoiceTyperController.cs) `BeginRecording/BeginLocalRecording/TeardownAsrSession`、[LocalAsrSession](Asr/LocalAsrSession.cs)。参考：[macOS 控制器](../macos/Sources/VoiceTyper/Core/VoiceTyperController.swift) 的 `active/finish`。

问题：只检查 `_isRecording`，上一会话识别或纠错期间可以新建会话；短录音标志、音频回调、预览和 UI 状态却共享。旧回调可改变新会话状态，结果可能乱序；停止后再恢复也可能重新接受旧结果。

修复：首版采用一个活跃听写对象，包含 ID、阶段、取消源、目标窗口、开始时间、ASR 会话和结果。Recording/Recognizing/Inserting 均拒绝新会话并给出反馈。所有事件验证身份；成功、失败、短录音、取消、暂停、设备变化和退出经由幂等的统一收尾入口。

引擎和队列退出也纳入同一生命周期：不在后台线程仍消费队列时直接释放队列；迟到 UI 回调检查关闭状态。识别看门狗不能只隐藏 HUD 并宣称恢复：评估 ORT 的运行取消，明确“底层推理未停止时不能复用同一引擎”的策略，无法恢复时提示重启，不能无限堆积新任务。

验收：用可控假音频源、假识别服务和延迟纠错器测试重复按键、短录音、各阶段取消、迟到 final、超时、暂停后恢复和退出。每个会话至多插入一次，结束后不再更改 UI/剪贴板，不出现旧会话污染新会话。

### WR-009：音频停止、释放和代际隔离

**P1 · 高 · S/C。** 定位：[AudioCaptureService](Services/AudioCaptureService.cs) `Start/Stop/OnCaptureStopped/Cleanup/Dispose`。

问题：每次开始新建采集器和设备；正常停止仅请求停止，下次开始覆盖字段，没有确定释放旧资源。NAudio 的停止请求是异步的。旧 DataAvailable/RecordingStopped 没有 generation 校验，并读取共享字段，存在跨会话污染风险。控制器收到设备异常时也没有完整同步录音阶段。[NAudio 生命周期][capture]

修复：每次采集拥有独立设备、采集器、转换器和 generation；停止完成后退订事件、释放所有设备/枚举器资源；旧回调只访问原上下文。明确“已采集且承诺接收的音频”与停止边界，尾音在完整顺序下交付一次。不要持有回调需要的锁同步等待采集线程退出。

支持设备异常时正常终结已收到音频，再通知 UI；不要自动将另一支麦克风的音频混入同一听写。对于多于两声道的设备，提供明确转换或明确不支持，不能将 stereo-to-mono 当成通用多声道转换。

验收：连续多轮开始停止后线程、句柄和设备对象不持续增长；拔出 USB、蓝牙断开、默认设备切换、睡眠恢复后可开始新会话；旧回调不能投递到新会话；尾音无重复和乱序。

### WR-010：插入事务与结果恢复

**P1 · 高 · S/C/V。** 定位：[TextInsertionService](Services/TextInsertionService.cs)、[VoiceTyperController](Core/VoiceTyperController.cs) `InsertFinalText`。

需要一并处理的问题：

- SendInput 成功表示事件入队，不等于目标已经粘贴；固定一秒恢复不能保证所有应用已读取。
- 剩余 Alt/Shift/Win 等键会改变 Ctrl+V 的含义，部分注入失败也可能留下不完整按键序列。
- 快照读取失败被当成空剪贴板，仍覆盖再恢复为空，可能丢失原内容。
- `CopyToClipboard` 不返回成功状态，UI 无条件显示“已复制”。
- 提权检测仅尝试 `OpenProcess`，没有读取进程令牌完整性信息。
- 只验证顶层 HWND/PID；同窗口内换输入框及快照期间换窗口仍有误插入风险。
- 待恢复任务已经投递到 UI 后，再取消其 delay 不一定使已投递动作失效；需验证任务身份。
- 两个历史/云剪贴板标记要求四字节 DWORD，目前写入一字节。第三个排除标记可能仍有效，不能据此声称已经发生隐私泄漏。[剪贴板格式][clipboard]

修复：明确快照“成功为空/成功有内容/失败”的结果；不能取得可靠备份时优先保留识别结果并让用户主动复制。对常见格式做真实可恢复的快照，对不支持格式说明降级。插入前等待相关物理修饰键释放并再次确认目标；目标不确定时停止自动插入。不要强行激活旧窗口。

将写入、注入、延迟恢复作为带 ID 的事务；延迟回调检查事务身份、剪贴板序列和内容，不覆盖用户期间的新复制。处理连续插入继承原快照、取消、退出和快照资源释放。读取实际令牌信息进行权限诊断；不能判断时显示未知。

保留最近一次未确认/失败结果于内存，提供可访问的“复制结果”入口；不默认记录持久化听写历史。明确区分“已发送粘贴”“结果已复制”“复制失败”，不能用 API 入队成功证明目标控件收到文本。[SendInput 契约][sendinput]

验收：纯文本、图片、文件列表、HTML/RTF、原所有者退出、剪贴板占用、连续听写、恢复期间用户复制、窗口和同窗口控件切换、管理员窗口。使用 Windows STA 集成测试；跨应用接受情况真机验证。终端不支持 Ctrl+V 时有可用恢复路径，不声称通用成功。

依赖：WR-003/007/008。

### WR-011：麦克风状态和选择

**P1 · 中 · S/R。** 定位：[MicPermissionProbe](Core/MicPermissionProbe.cs)、[AppCoordinator](App/AppCoordinator.cs) `ProbeMicrophone`、[SetupForm](UI/SetupForm.cs) `UpdateStatus`、[AudioCaptureService](Services/AudioCaptureService.cs) `Start`。

问题：探测调用方丢掉成功布尔值，仅保留 accessDenied；无设备或其他失败被显示成“麦克风可用”。当前默认使用 Communications 设备，不一定与用户选择的系统默认输入设备一致。没有设备选择及真实电平反馈。

修复：用结构化探测结果区分可用、无设备、权限拒绝、设备失败、检测中和未知；设备显示名称和稳定 ID；提供“跟随系统默认输入”及显式设备选择，说明是否跟随通信设备。选中设备不可用时提示，不静默切到其他来源。

探测应等待可确认的初始化结果，串行且可取消，避免轮询重叠。提供用户主动的电平测试；离开测试页释放采集，避免周期性开麦干扰日常使用。

验收：无麦克风、系统隐私开关关闭、设备忙/初始化失败、存在两支不同默认角色麦克风、显式设备被拔出；显示状态与实际结果一致。权限失败提供正确 Windows 设置入口。

### WR-012：首次启动到首次输入

**P1 · 中 · S/R。** 定位：[AppCoordinator](App/AppCoordinator.cs) `Start/ReevaluateReadinessAsync/OpenSetup`、[SetupForm](UI/SetupForm.cs)、[TrayController](UI/TrayController.cs)。

问题：首次正常下载可能只有折叠区域里的托盘反馈；模型下载、设备检测和第一次使用没有一个明确完成点。错误提示自动消失后缺少可发现的恢复入口。

修复：独立首次使用状态，首次主动启动显示引导；模型准备和设备检查并行，展示进度、重试、设备名称/电平及热键试用框。成功后解释托盘入口并提供自启选择；以后登录自启不重复弹出引导或抢焦点。将高级 ASR/LLM 选项移出首次使用必经流程。

验收：使用新的测试用户配置，从没有模型到成功输入全程不用命令行；下载失败、关窗、重开、重启、权限恢复均可继续；用户正在编辑设置时状态刷新不覆盖草稿、不反复激活窗口。

依赖：WR-011/013；最终试用依赖 B–D 批次通过。

### WR-013：下载取消、重试与模型修复

**P1 · 中 · S/C。** 定位：[ModelDownloader](Asr/ModelDownloader.cs) `Cancel/PerformDownloadAsync`、[ModelLocator](Asr/ModelLocator.cs) `Validate`、[SetupForm](UI/SetupForm.cs) `HandleModelAction`。

问题：无限 HTTP 超时加布尔取消不能中断阻塞中的连接或 ReadAsync；文件存在即视为可定位模型；下载、校验和加载失败的按钮行为不一致，损坏模型恢复不明确。

修复：下载器持有 CTS，与调用方 token 关联并传递到连接、读取和写入；设置连接/无进度超时，退出时取消并释放。续传校验 Content-Range 的起点及长度，异常时安全重下；处理 200/206/416、校验失败和磁盘写入失败，保留可恢复的临时文件。最终文件仅在校验成功后替换。

UI 区分网络失败、校验失败、文件缺失、模型加载失败、原生库加载失败；“重试加载”和“修复模型”执行各自动作。固定模型 revision 或不可变下载源，继续使用固定 SHA-256；如需修改哈希，必须确认模型版本及双平台影响，不能因校验失败直接接受远端新值。

验收：使用可控 HTTP 测试服务器覆盖断流、无限等待、取消、206 续传、忽略 Range 的 200、416、错误哈希和重试；干净用户完成下载后断网识别；损坏模型可通过应用内操作修复。设置新超时后记录取值理由。

### WR-014：密钥保存与运行态同步

**P1 · 中 · S。** 定位：[SetupForm](UI/SetupForm.cs) `HandleSaveRecognition`、[AppCoordinator](App/AppCoordinator.cs) `ApplyConfigAsync`、[VoiceTyperController](Core/VoiceTyperController.cs) 构造器、[LlmCorrector](Llm/LlmCorrector.cs)。

问题：API Key 单独保存，不参与 `onlyUiChanged` 比较；只改密钥后纠错器仍持有旧值，但测试按钮使用输入框新值。密钥在整体设置被拒绝前已经保存，存在部分生效。

修复：先统一校验和检查是否允许应用，再保存密钥与配置；将密钥变化作为显式更新信号，不能把明文密钥写进配置比较日志。更新客户端或安全重建；存储失败给出准确状态。密钥文件用可靠的原子替换方式，保留 DPAPI 当前用户保护。

纠错请求接受并传播 CancellationToken；取消会话后停止请求并丢弃迟到结果。区分用户取消和网络失败回落。核实“测试纠错”对语义回落的展示，不能把返回原文自动当作服务完成了有效纠错。

验收：只更换密钥后实际听写用新密钥；密钥写入失败、配置写入失败、活跃听写拒绝保存时无虚假成功；取消后无迟到插入；日志、YAML 和诊断信息不出现密钥。

### WR-015：单实例范围与二次启动

**P1 · 中 · S/C。** 定位：[Program](Program.cs) `MutexName/Main`。

问题：固定 `Global` mutex 跨登录会话互斥，同机其他用户可能被阻止，却被提示寻找不属于本会话的托盘。二次启动仅弹消息，不能激活原实例设置。

修复：按交互桌面应用的用户/会话范围确定唯一实例标识，处理 mutex 创建异常；通过有适当访问控制的本地 IPC 请求已有实例打开窗口。若允许同一用户多个登录会话，明确共享配置/模型下载目录的互斥与写入策略，不能简单替换 Global 前缀就结束。[命名空间规则][namespace]

验收：同会话双击只保留一个实例并激活窗口；不同用户登录、快速用户切换、RDP 场景不互相误阻止；IPC 不允许其他用户任意控制当前实例。

### WR-016：安装、升级、卸载与原生依赖

**P1 · 中 · S/V。** 定位：[build.bat](build.bat)、[VoiceTyper.iss](installer/VoiceTyper.iss)、[VoiceTyper.csproj](VoiceTyper.csproj)。

问题：缺少应用自启 Run 项的卸载清理；升级时已有实例、旧版本文件和架构切换缺少明确策略；`UninstallDelete` 递归删除整个安装目录，会连安装器不拥有的文件一起删除。原生依赖闭包尚无干净机器证据。

修复：仅移除本应用拥有的注册项和安装文件；配置与模型保留策略与文档一致。安装升级协调已有实例退出并重新启动，处理遗留文件。为最低系统版本和架构设定实际约束；打包命令失败必须向上传递，不能 ZIP 失败仍显示完成。

检查最终发布产物中的 ORT DLL、架构及 VC++ 运行库依赖，选择支持的部署方式并解释是否需要提权；不要因为 .NET 自包含就声称所有原生依赖齐全。官方列出了 VC++ runtime 要求，但当前尚未检查最终产物，缺 DLL 是待验证风险。[ORT 安装要求][ort-install]

验收：没有 SDK、没有预装开发工具的 Windows 安装/便携启动；覆盖升级、运行中升级、卸载、自定义安装目录；卸载后无失效自启项，不删除用户额外文件。arm64 发布成功不能代替 arm64 进程及真实设备运行验证。

### WR-017：自启状态与失败反馈

**P2 · 中 · S/V。** 定位：[StartupRegistration](Support/StartupRegistration.cs)、[SetupForm](UI/SetupForm.cs) `HandleSaveGeneral`、[TrayController](UI/TrayController.cs)。

问题：仅判断 Run 值存在就显示启用，无法表达用户在系统启动应用界面禁用的状态；写入异常被日志吸收，UI 仍可能提示成功；托盘和设置勾选不同步。

修复：区分应用注册状态与系统实际许可状态，不擅自覆盖系统禁用决定；采用支持的状态查询或提供明确的“已注册，请在系统启动应用中确认”及入口，不依赖未承诺稳定的私有注册表布局。写操作返回结果，失败恢复勾选，多个入口读取统一状态。

验收：托盘、设置页、Windows 启动应用页交替操作后反馈一致；注册表写入失败有可见反馈；便携目录移动和卸载不会留下无法解释的自启状态。

### WR-018：测试证据与覆盖

**P1 · 中 · S。** 定位：[FbankParityTests](Tests/VoiceTyper.Tests/FbankParityTests.cs)、[ConfigStoreTests](Tests/VoiceTyper.Tests/ConfigStoreTests.cs)、[测试工程](Tests/VoiceTyper.Tests/VoiceTyper.Tests.csproj)。

问题：缺夹具/模型时直接 return，测试显示通过；克隆测试给默认已有 Ctrl 的列表再次加 Ctrl，随后断言单元素，必然失败。现有测试未覆盖原生结构体、实际重采样、控制器/引擎生命周期、下载故障和完整语音识别。

修复：修正测试输入和断言；必需夹具缺失直接失败，可选真实模型测试明确 Skip 并列出原因；发布验收中必需的端到端测试不允许被跳过。不要放宽金标准阈值来让测试变绿。通过接口、时钟、路径及 HTTP 客户端注入隔离系统依赖，不读写开发者真实配置、密钥和剪贴板。

复用 `macos/Tests/VoiceTyperTests/Fixtures/`，增加 `speech_zh_en_mixed.wav` 到最终文本的 Windows 验收；执行现有设计的编辑距离 ≤ 2 标准并记录实际差异。fbank/LFR/CMVN 阈值保持 < 1e-3，改动算法时同步评估 macOS。

验收：CI 输出通过/失败/跳过及缺失原因；Windows STA 集成测试与纯逻辑测试分组；每项修复对应能揭示用户故障的回归测试，不能只验证私有布尔表达式。没有真机环境时交付未执行清单，不能填写“全部验证通过”。

### WR-019：性能、内存与电池

**P2 · 中 · S/V/R。** 定位：[AsrService](Asr/AsrService.cs) `UnloadNowAsync/CalibratePreviewWindowIfNeeded`、[SenseVoiceEngine](Asr/SenseVoiceEngine.cs)、[AsrPump](Asr/AsrPump.cs)。

问题：强制裁剪工作集不代表降低内存承诺；下次访问可能产生缺页。静音自校准在每次加载路径都可能执行，并非只运行一次，也不代表真实预览负载。当前性能参数主要来自 macOS，不能当作 Windows 实测结果。

修复方向：先保留可工作的 CPU/int8 基线，释放实际资源；以测量决定是否取消默认强制工作集裁剪、调整线程优先级、自旋、线程数和预打包策略。校准观察真实语音和预览执行时间，并保证配置更新及时作用于后续会话，不在用户首次说话时增加不必要的静音推理排队。

验收：记录冷/热启动、空闲恢复、首个预览、松键到插入 p50/p95；拆分等待预览、特征、推理、解码、LLM、插入耗时。覆盖短句及 30/60/120 秒音频，记录 Private Bytes、工作集、CPU、线程数、峰值内存和电池模式。测量未完成前不承诺包体、内存或延迟数字。[ORT 线程调优][ort-threading]

后续增强：GPU/NPU、SIMD 优化或替换 FFT 仅在热点和收益证据明确时单独立项，必须保留金标准验收。

### WR-020：HUD、支持范围与发布说明

**P2 · 中 · S/V/R。** 定位：[RecordingHud](UI/RecordingHud.cs)、[app.manifest](app.manifest)、[README](README.md)、[DESIGN](DESIGN.md)。

问题：错误和长预览在小窗口中截断，短暂提示消失后缺少恢复入口；自绘布局和字体需要混合 DPI 验证，仅声明 PerMonitorV2 不代表全部布局正确。文档存在“已输入/已复制/麦克风可用”、系统支持和体积方面的过强表述。

修复：常规 HUD 不抢焦点，错误通过独立可发现入口提供详情和恢复；验证 DPI 变化、字体缩放、深浅主题、高对比度、键盘操作及读屏。按实际验收矩阵说明 Windows 版本/架构，清理与 .NET 目标不符的旧系统暗示。

公开分发将签名、时间戳、升级说明和故障说明纳入发布流程；签名不能保证没有 SmartScreen 提示，EV 也不保证即时信誉。现有“未签名内部验证”与“适合普通用户公开发布”应明确区分。[.NET 支持矩阵][dotnet-os]、[SmartScreen 说明][smartscreen]

验收：100%/150%/200% 混合 DPI 下设置可操作、状态和错误可读；主流程不抢目标输入焦点；README、DESIGN 和发布产物所述能力一致。历史设计中的技术选型理由若要重写，需重新核对当前官方资料，不能直接复用过时结论。

## 5. 分阶段执行计划

按以下顺序形成可审查的小批次。每个批次完成后更新任务状态、测试记录和剩余风险；不必等所有 UI 增强完成才跑基础真机探针。

| 批次 | 目标与任务 | 前置条件 | 退出条件 |
| --- | --- | --- | --- |
| A | 构建和测试基线：WR-001、WR-018 的编译/断言/跳过修复；加入 Windows CI | 无 | 主程序和测试工程可编译，现有测试可信执行，两个 RID 可发布 |
| B | 修通基本 I/O：WR-002、WR-003、WR-009 的停止和资源所有权 | A | 音频转换样本数正确，固定文本可粘贴，多轮启停无资源持续增长 |
| C | 统一生命周期：WR-008 → WR-004/005 → WR-014；完成 WR-009 控制器接线 | B | 单会话、空闲恢复、重载、取消、超时和密钥更新均有回归证据 |
| D | Windows 输入体验：WR-006/007、WR-010 | B/C | 热键不穿透、不死锁，取消不重触发，失败结果可找回，剪贴板恢复正确 |
| E | 首次使用：WR-013、WR-011 → WR-012 | B–D 的接口稳定 | 新用户可完成下载、设备测试和首次输入；故障可恢复 |
| F | 桌面生命周期和分发：WR-015/016/017、WR-020 发布部分 | A；全流程验收依赖 E | 干净机器可装用，二次启动/自启/升级/卸载行为清楚 |
| G | 准确率、性能、兼容性和文档收尾：WR-018/019/020 | A–F | 验收矩阵有实际记录，未验证项明确，支持范围与证据一致 |

### 建议的修改分组

- **构建与测试基础**：工程定义、CI、已有错误用例。不要顺带升级所有 NuGet 包。
- **音频与听写生命周期**：AudioCaptureService、VoiceTyperController、LocalAsrSession、AsrService 作为协调修改范围，先定义资源和事件契约。
- **热键与插入**：HotkeyService、NativeMethods、TextInsertionService，复用稳定的单会话入口。
- **首次使用与配置**：SetupForm、AppCoordinator、下载器、麦克风选择和密钥事务。配置字段变化同步用户文档。
- **安装与系统集成**：Program、StartupRegistration、安装/构建脚本，验收时使用最终发布产物。

这些分组是后续任务分配边界，不要求同时启动多个 agent。若分配多人/多个 agent，同一个生命周期文件不能未经协调同时修改；由集成负责人维护事件和状态契约。

### 今天真机验证的最小顺序

1. 先完成 A/B，验证固定文本插入和真实音频采集，确认 Win32/NAudio 通路。
2. 跑共享语音夹具的完整 ASR，确认识别管线与参考输出一致。
3. 完成 C/D 后测按住说话、短录音、Esc、重复按键、空闲恢复和故障恢复。
4. 使用干净测试用户验证 E/F 的下载、权限、设备、首次使用及安装路径。
5. 最后采集 G 的性能和兼容性数据。前面有 P0/P1 未修复时，可继续独立探针，但不能将探针成功标成全产品验收通过。

## 6. 验证命令与场景

### Windows 构建基线

在 `windows/` 中执行；命令应以修复后的工程实际需要为准，变更时同步 README。

```powershell
dotnet --info
dotnet restore VoiceTyper.sln
dotnet build VoiceTyper.sln -c Release --no-restore
dotnet test Tests/VoiceTyper.Tests/VoiceTyper.Tests.csproj -c Release --no-build --logger trx
dotnet publish VoiceTyper.csproj -c Release -r win-x64 --self-contained true -o dist/win-x64
dotnet publish VoiceTyper.csproj -c Release -r win-arm64 --self-contained true -o dist/win-arm64
```

记录 SDK/运行时版本、目标 RID、提交号和测试结果。跨编译/交叉发布不是目标架构运行证据；不要把 x64 仿真运行记成 arm64 原生验收。真实模型由现有下载工具或应用下载器获取，不提交模型或构建产物。

### 手工验收矩阵

| 范围 | 必测场景 | 验收重点 |
| --- | --- | --- |
| 首次启动 | 新测试用户，无 SDK/模型；断网、取消、恢复 | 可见进度和故障原因；应用内完成第一次输入 |
| 麦克风 | 内置、USB、蓝牙；无设备、禁用权限、切默认设备 | 选中源正确；实际电平；失败可解释 |
| 按键 | 左右修饰键、不同松键顺序、Esc、重复按键、AltGr/IME | 不触发目标快捷键；不产生卡键/重复会话 |
| 常规输入 | 记事本、浏览器、Office、聊天工具、VS Code、终端 | 文本完整，原剪贴板尽可能恢复 |
| 插入边界 | 管理员窗口、切应用、同窗口换输入框、粘贴不支持 | 不误插入；结果可恢复；不虚报复制成功 |
| 剪贴板 | 文本、图片、文件、HTML/RTF、占用、期间再次复制 | 原内容恢复或明确降级，不覆盖新复制 |
| 生命周期 | 空闲十分钟、睡眠/锁屏恢复、拔麦、重载、退出 | 引擎可恢复，无旧结果迟到插入 |
| 桌面与安装 | 多用户/RDP、二次启动、自启、升级、卸载 | 实例范围正确，系统状态与 UI 一致 |
| 显示 | 多屏、100/150/200% DPI、字体缩放、高对比度 | 无遮挡/裁切，不抢输入焦点，恢复操作可达 |
| 性能 | 短句及 30/60/120 秒音频，冷/热/空闲恢复，电池模式 | 记录实际延迟和资源，分清测量与估算 |

不要为测试清空真实用户的配置、模型或剪贴板；使用隔离路径、测试账户或虚拟机。真实音频/识别文本仅用于必要验证，不附入公共日志或诊断包。

## 7. 修复 agent 的交付要求

每个修改批次提供以下记录，可放入 PR 描述或本文件后续更新中：

```text
任务 ID：
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

状态建议使用“待修复 → 修复中 → 代码与自动化验证完成 → 真机验证完成”。纯逻辑任务无需虚构真机依赖；涉及 Win32、音频、剪贴板、安装及性能的任务不能在只有代码检查时标记最终完成。

如果修复改变 fbank、LFR/CMVN、CTC、文本后处理或模型输入输出，必须评估两个平台并继续共用金标准夹具。修改 Windows 特定行为不要求机械同步到 macOS；两平台语义一致与平台实现相同是两件事。

## 8. 官方资料

以下资料在审查时已核对。版本相关结论在实施和发布时应复核，尤其是 OS 支持范围和分发策略。

- [.NET SDK 默认包含规则][sdk]
- [NAudio 2.2.1 BufferedWaveProvider][buffered]
- [NAudio 2.2.1 WdlResamplingSampleProvider][wdl]
- [NAudio 2.2.1 WasapiCapture][capture]
- [Win32 INPUT][input]
- [Win32 SendInput][sendinput]
- [低级键盘钩子线程与超时][hook]
- [RegisterHotKey][register]
- [剪贴板历史与云同步格式][clipboard]
- [Windows 内核对象命名空间][namespace]
- [ONNX Runtime 安装依赖][ort-install]
- [ONNX Runtime 线程调优][ort-threading]
- [.NET 10 系统支持矩阵][dotnet-os]
- [SmartScreen 与签名信誉][smartscreen]

[sdk]: https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview
[buffered]: https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Core/Wave/WaveProviders/BufferedWaveProvider.cs
[wdl]: https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs
[capture]: https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiCapture.cs
[input]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
[sendinput]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
[hook]: https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc
[register]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
[clipboard]: https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats
[namespace]: https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
[ort-install]: https://onnxruntime.ai/docs/install/
[ort-threading]: https://onnxruntime.ai/docs/performance/tune-performance/threading.html
[dotnet-os]: https://raw.githubusercontent.com/dotnet/core/main/release-notes/10.0/supported-os.md
[smartscreen]: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation
