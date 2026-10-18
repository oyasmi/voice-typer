# Windows 版评审历史、遗留项与方法论

[← Windows 目录](../README.md) · [架构设计](../DESIGN.md) · [真机验证清单](VERIFICATION.md) · [分发与体积](PACKAGING.md)

本文是 `windows/` 历次代码评审的**总索引与结论汇总**，取代此前散落在目录根部的
`REVIEW_AND_REPAIR_PLAN.md`、`REPAIR_TRIAGE.md`、`REPAIR_DELIVERY.md`、
`REVIEW_2026-09-14.md`、`REVIEW_2026-10-02.md`、`TRIAGE_2026-10-02.md`、
`REVIEW_UX_2026-10-11.md` 七份过程文档（原文可从 git 历史找回）。给两类读者：

- **接手后续工作的 agent / 人**：先读 §3 遗留项与 §4 否决项，避免重查已确认良好的方面、
  重提已否决的方案；用 §2 的编号对照表反查代码注释里的 `REVIEW_UX W-01`、`WT-03` 等引用。
- **发起下一轮评审的人**：§5 是历轮沉淀的证据分级、分诊与交付口径。

各批次**改动的权威记录**始终是 [DESIGN.md](../DESIGN.md) §13–§25 与
[CHANGELOG.md](../CHANGELOG.md)，本文不重复其内容，只做索引。

---

## 1. 评审轮次索引

| 轮次 | 日期 | 审查基线 / 版本 | 编号体系 | 覆盖主题 | 结局 |
| --- | --- | --- | --- | --- | --- |
| 一（审查+分诊+交付） | 2026-09-06 | `86424eb` / 3.2.1 | WR-001…WR-020 → 工单 R0–R4 | 「能不能跑、会不会崩」：编译阻断、重采样死循环、INPUT 布局、生命周期、热键、剪贴板、安装分发 | R0–R4 全部交付；3.5.0（2026-09-29）在带 SDK 环境真实编译+测试通过。见 DESIGN §13、CHANGELOG |
| 二（对 macOS 对读） | 2026-09-14 | `9bc6036` / 3.2.1（macOS 3.4.0） | VW-01…VW-25 | 与 macOS 3.2.1→3.4.0 的对等缺口（引导、HUD 反馈、下载优化）+ Windows 自身缺陷 + 性能空间 + CI/测试工程 | 3.2.2 落地高性价比项；其余 3.5.0 全部落地；仅性能项 VW-18/19/21 与 packages.lock.json 未做。见 DESIGN §14–§15 |
| 三（设计与实现审查+分诊） | 2026-10-02 | `8927427` / 3.5.0 | WT-01…WT-20 → 工单 T1–T3 | 生命周期与状态所有权的跨模块边界（加载占位、会话租约、门禁事实源、采集上下文、剪贴板恢复身份、日志滚动）、设置页、热路径 | T1/T2 全部实施并通过测试（T3-2 除外）；T3-1 测试已写未运行、T3-2 待真机数据。见 DESIGN §17 |
| 三·五（交互与性能批次） | 2026-10-09 | 真机日志驱动 | A/B/C/D/E 编号（§23 方案） | 按热键到可开口的延迟、跟嘴跟手、剪贴板后台备份、锁屏恢复、下载进度、麦克风测试 | 大部分实施；A5/A6/A7/C5/E1 留待数据。见 DESIGN §18–§24 |
| 四（用户体验评审） | 2026-10-11 | 当前代码（约 1.2 万行全量精读） | W/F/U/P-xx | 「最后一公里」体验：防丢结果、被动反馈、等待期反馈、启动排队 | 第一、二批共 15 项已实施；第三批未做。见 DESIGN §25 |

七份原文档的定位关系：每轮都是「审查（提出问题）→ 分诊（确证/降级/移出）→ 交付（实施记录）」
三段链，分诊环节的价值（否决理由）已并入本文 §3/§4，实施细节由 DESIGN/CHANGELOG 承接。

---

## 2. 编号对照总表

代码注释中引用这些编号（如 `REVIEW_UX W-01`、`WT-03`）。「已完成」表示代码落地且自动化测试
通过；真机验证状态统一见 [VERIFICATION.md](VERIFICATION.md)——**没有任何一轮完成过整体真机验收**。

### 2.1 第一轮 WR-001…WR-020（分诊后执行为 R0–R4）

| ID | 一句话结论 | 处置 |
| --- | --- | --- |
| WR-001 | 主工程把测试源码编进来且有多处编译错误，从未编译成功 | R0-1 已修 |
| WR-002 | `BufferedWaveProvider.ReadFully=true` 补零导致重采样死循环，一按热键就挂 | R1-1 已修 |
| WR-003 | `INPUT` 联合体缺 `MOUSEINPUT`，cbSize=32≠40，文本插入 100% 失败 | R1-2 已修（NativeLayoutTests） |
| WR-004 | 空闲卸载后被排除在预加载外，每次听写报「尚未就绪」不可自愈 | R2-2 已修 |
| WR-005 | Ready 手动重载形成同步重入无限循环，每轮构建 ~500MB session | R2-3 已修（EnsureLoaded/Reload 语义分离） |
| WR-006 | 钩子回调同步执行录音启动；自愈把鼠标输入当键盘、重装失败连健康计时器一起销毁 | R1-4 + R3-5 已修；独立钩子线程见遗留 L1 |
| WR-007 | 热键穿透到前台应用；左右修饰键松开判定失效；Esc 后 auto-repeat 重启录音 | R2-1 已修（HotkeyStateMachine） |
| WR-008 | 识别/插入期间可开新会话，旧结果迟到仍插入 | R3-1 最小收口已修；活跃听写对象整体重构被否决（身份校验已存在） |
| WR-009 | 每次听写泄漏一组采集 COM 资源；>2 声道设备直接不可用 | R1-3 已修；generation 隔离当时降级，后由 WT-04/T2-6 `CaptureContext` 完成 |
| WR-010 | 复制失败仍报「已复制」；排除格式写 1 字节非 DWORD；提权检测恒 false；快照读失败清空用户剪贴板 | R3-2 四子问题已修；「SendInput 成功≠已粘贴」「1s 恢复窗口」记为固有限制（DESIGN §13.3） |
| WR-011 | 探测返回值被丢弃，无设备/设备忙都显示「麦克风可用」 | R3-3 已修（MicProbeResult）；设备选择/电平测试当时移出，3.5.0 与 §24.3 已做 |
| WR-012 | 无首启引导 | 当时移出（产品功能非缺陷）；3.5.0 VW-02 OnboardingForm 已做 |
| WR-013 | 下载无超时不可取消，卡死连接永久挂起 | R3-4 已修（CTS+双超时）；分段并发/退避重试 3.5.0 VW-08 已做 |
| WR-014 | 先存密钥后存配置造成部分生效；只改密钥不重建纠错器（测试通过、听写用旧密钥） | R2-4 已修（SaveRecognitionAsync 单一事务） |
| WR-015 | `Global\` mutex 跨登录会话误阻止其他用户 | R4-1 已修（`Local\`）；IPC 激活已有实例被否决 |
| WR-016 | 卸载残留自启项；打包失败仍报成功 | R4-2 已修；`UninstallDelete` 递归删除被否决（私有目录的常规做法） |
| WR-017 | 自启写失败 UI 仍显示成功 | R4-2 已修（SetEnabled→bool）；查询系统禁用态被有意否决（StartupApproved 布局无稳定性承诺） |
| WR-018 | 测试必然失败却留在仓库（证明从未跑过）；缺夹具静默 return 假通过 | R0-2/R0-3 已修；覆盖缺口由 3.5.0 VW-23 补强（180 项用例含端到端） |
| WR-019 | 性能/内存参数全部来自 macOS，无 Windows 测量 | 当时移出；主线移交后续 VW-18/19/21、WT-14、§18 批次（见遗留 L7） |
| WR-020 | 文档「已对齐/已复制/麦克风可用」等过强表述；DPI/无障碍未验证 | R4-3 文案已修；DPI/高对比度审计见遗留 L9 |

### 2.2 第二轮 VW-01…VW-25（3.2.2 + 3.5.0 落地）

| ID | 一句话结论 | 处置 |
| --- | --- | --- |
| VW-01 | 未就绪时按热键无任何反馈（死键） | 已完成：BlockedReason 门禁 + HUD 说明 |
| VW-02 | 无首启引导 | 已完成：四步 OnboardingForm，最后一步真跑听写 |
| VW-03 | HUD 缺电平波形/设备名/静音提示/空结果提示/纠错中状态/双行预览 | 已完成 |
| VW-04 | 识别中不能 Esc 取消 | 已完成 |
| VW-05 | 无切换式热键 | 已完成：`hotkey.mode = hold/toggle` |
| VW-06 | 缺 `preload_on_launch`；空闲卸载默认值与 macOS 漂移 | 已完成：默认 0（从不） |
| VW-07 | HUD 显示在屏幕顶部（macOS 在底部）、无位置设置 | 已完成：`ui.hud_position`，默认底部居中 |
| VW-08 | 模型下载无分段并发/退避重试 | 已完成：四段并行 + 5/20/60s 退避（真实网络待验证） |
| VW-09 | 无检查更新入口 | 已完成：托盘「检查更新…」（不做后台轮询） |
| VW-10 | 预览不跳过静音段，稳态白耗 CPU | 已完成：`hasSpeechSinceLastPreview` |
| VW-11 | ModelMissing 每次重评估抢焦点；自动弹窗永久污染 `_userOpenedSetup` | 已完成（3.2.2）：`_forcedPresentations` 去重 |
| VW-12 | 预览滚窗只滚一次，慢机器窗口单调增长正反馈 | 已完成（3.2.2）：`if`→`while` |
| VW-13 | 听写中被模型状态变化覆盖托盘状态 | 已完成（3.2.2）：Loading/Ready 分支保留活动听写态 |
| VW-14 | HUD 尺寸像素硬编码，高 DPI 偏小 | 代码已完成（按 DeviceDpi 缩放）；**真机 100/150/200% 未确认** |
| VW-15 | 保存非 UI 配置销毁重建 HUD | 已完成（3.2.2）：就地 ApplyOpacity |
| VW-16 | 麦克风选 `Role.Communications` 与系统默认设备不一致；无设备选择 | 已完成：`Role.Console` + `audio.input_device`（端点分类待真机核对） |
| VW-17 | 音频分帧 `Queue<float>` 逐样本 + float↔byte 往返且在采集锁内 | 已完成：定长线性缓冲（float↔byte 往返的最终消除见 WT-14 否决理由） |
| VW-18 | 特征前端锯齿数组三级分配，finalize 路径 GC 压力大 | **未做**：需真机基线，且触碰与 macOS 共享的金标准（遗留 L7） |
| VW-19 | 用完整复数 FFT 做实数变换，理论多做一倍功 | **未做**：先测 fbank 在总 RTF 占比再决定（遗留 L7） |
| VW-20 | 未启用 ReadyToRun | 3.5.0 开启 → 后续撤回 → **2026-10-09 重新开启**（见 PACKAGING.md；启动收益未实测） |
| VW-21 | 托盘状态图标每次状态变化重绘位图 | **未做**：静态图形可预渲染缓存（遗留 L7） |
| VW-22 | 无 CI；packages.lock.json 缺失 | CI 已建（含双 RID 发布与产物断言）；**packages.lock.json 仍未生成**（遗留 L10） |
| VW-23 | 测试规模 97 vs macOS 188，缺端到端识别 | 已完成：控制器依赖接口化 + 180 项用例 |
| VW-24 | AsrService 不可注入，引擎生命周期无法单测 | 已完成（3.5.0）；三轮 T1-0 再补 `IAsrEngine`/locate/build/schedule 接缝 |
| VW-25 | 版本号口径混乱（csproj/manifest/双语特性不一致） | 已完成：统一升版 + Windows 专属 CHANGELOG |

### 2.3 第三轮 WT-01…WT-20（分诊后执行为 T1–T3）

| ID | 一句话结论 | 处置 |
| --- | --- | --- |
| WT-01 | 缺模型时同步完成的加载任务永久占住 `_inFlightLoad`，下载完成后不加载、「重新加载模型」永远无效 | T1-1 已修（TaskCompletionSource 占位，先发布后启动，按身份清理） |
| WT-02 | 加载完成无条件重启空闲计时器，可在录音中到期释放活跃引擎；已入队卸载撤不回 | T1-2 已修（会话租约 + 执行前复查） |
| WT-03 | 拒绝重载时把显示状态改成 Error，反而解除了后续破坏性操作的忙碌门禁 | T1-3 已修（门禁用 `HasActiveDictation` 事实源） |
| WT-04 | 旧采集器迟到事件可读写新录音 | T2-6 已修（CaptureContext 绑定单次采集） |
| WT-05 | 退出 Join 超时仍 Dispose 队列；推理超时后仍接新任务 | T1-5 已修（Join 成功才 Dispose）；应用级 Closing 门禁/RunOptions.Terminate/引擎健康态被移出（见 §4） |
| WT-06 | ORT 构造中途失败不释放已创建 session | T1-4 已修（小文件先解析、using SessionOptions、局部变量拥有） |
| WT-07 | 钩子仍挂 UI 消息线程，UI 阻塞影响全局键鼠 | T3-2 **未做**：验证驱动，需真机数据（遗留 L1）；§18 批次已把设备打开挪到专用控制线程 |
| WT-08 | 迟到恢复回调清掉别人的 pending；控制器重建丢失剪贴板恢复状态 | T1-6 已修（应用级持有 + 恢复操作身份 + FlushPendingRestore） |
| WT-09 | 日志只在启动滚动，常驻进程无上限 | T1-7 已修（运行中按大小滚动）；异步队列/重复合并被移出 |
| WT-10 | UI 与配置范围不同，打开合法配置即被夹逼误判 dirty | T2-1 已修（ConfigLimits 单一来源） |
| WT-11 | 保存 preview_window 后新会话仍用旧参数 | T2-2 已修（有效值现算） |
| WT-12 | 每次加载都重复 5s 静音校准 | T2-2 已修（按模型+线程缓存）；「Ready 与热身分离」移出。后续 §25.2 P-01 又加了校准让位 |
| WT-13 | WDL 拉取式重采样把临时短读当 flush，可能影响所有 44.1/48kHz 设备音质 | T3-1 测试已写（`ResamplerStreamingTests`），**测量结果未运行**（遗留 L2） |
| WT-14 | 每次预览重复分配多级特征数组、每会话预留完整 120s 数组 | 移出：无基线不动，特征链改动须同步评估 macOS（遗留 L7） |
| WT-15 | 延迟打点缺钩子投递等待，电平事件缺会话身份 | 移出（诊断增强）；§18.3 已补 `key_lag`/`dispatch`/`hud_ready` 等字段 |
| WT-16 | HUD 每帧 new StringFormat 不 Dispose；纠错回落被成功提示覆盖 | T2-3 已修（缓存+Stopwatch+回落副标题）；宽度单调/跨 DPI 锚点/静态降频移出（宽度单调已于 DESIGN §29 实施） |
| WT-17 | 权限探测可重叠、重复开设备 | T2-4 已修（单个在飞+版本丢弃）；探测与所选设备一致/Unknown 态移出 |
| WT-18 | 设置草稿/即时动作/全局状态耦合 | 移出：需独立设计任务（与 WT-19 一起） |
| WT-19 | 设置页视觉与交互规范不完整 | 移出：产品设计，先出设计稿再实施（§23 方案 E3 布局重构同类否决） |
| WT-20 | 生命周期测试/真实推理 CI/文档口径缺口 | T2-7 已修 CI 触发路径（含 macOS Fixtures）与文档事实；真实模型专项 job、arm64 原生验收、packages.lock.json 仍开放（遗留 L10） |

### 2.4 第三·五批 §23 方案编号（2026-10-09，详见 DESIGN §23/§24）

A 系（跟嘴/稳健）：A1–A4 已实施；A5 停顿处提前定稿分段、A6 松键后补录、A7 连续听写保持麦克风
**留待真机数据**（遗留 L4）。B4 锁屏恢复、E3 下载进度、D1+E2 麦克风测试已实施（DESIGN §24）。
C5 逐像素 alpha、E1 引导加选择热键、E3 布局重构按 §24.4 否决。

### 2.5 第四轮 UX W/F/U/P-xx（2026-10-11）

第一、二批 15 项已实施（DESIGN §25.1–§25.3）：

| ID | 一句话结论 | 处置 |
| --- | --- | --- |
| W-01 | finalize 看门狗固定 30s，慢机 + 120s 长听写整段丢失 | 已实施：`clamp(时长×5, 30s, 300s)` + 起点挪到推理真正开始 + 超时兜底复制预览文本；引擎等待上限 20s→60s |
| W-02 | 引导被 × 关闭后每次启动重弹，无退出通道 | 已实施：关闭计数（3 次视为完成，`onboarding-dismissed.txt`） |
| F-01 | 模型下载完成/重试用尽无任何主动通知 | 已实施：托盘气泡（完成 Info、放弃 Error+点击打开模型页） |
| F-02 | 录音阶段引擎仍在加载时 HUD 无说明 | 已实施：攒满 1s 待回灌音频提示「录音中 · 模型加载中…」 |
| F-03 | 长听写松键后「识别中」无时长预期 | 已实施：超 2s 追加「· Ns」递进计时 |
| F-04 | 重试等待期按热键提示不含重试进度 | 已实施：BlockedReason 直接返回带进度的下载错误文案 |
| F-05 | Inserting 阶段 HUD 仍显示「识别中」 | 已实施：HUD 新增 Inserting 阶段「正在输入…」 |
| F-06 | 纠错后的最终文本上屏前不可见 | 已实施：OnFinal 前先 OnPartial(纠错文本) |
| U-01 | 引导页下载期间每 200ms 全量重排，闪烁风险 | 已实施：按 `_shown` 集合布局 + 可见性 diff + SuspendLayout |
| U-02 | 托盘 tooltip/隐藏设置窗每 200ms 全量刷新 | 已实施：变化守卫 + 隐藏窗跳过 UpdateStatus |
| U-03 | 麦克风测试被强制停止时状态行空白 | 已实施：StopMicTest(reason)，非空原因用暗金色 |
| U-04 | 设置页麦克风下拉不随插拔刷新 | 已实施：DropDown 展开时重列并保留选择 |
| P-01 | 预览自校准占住推理队列，首次听写被排队 1–3s | 已实施：校准在 pump 开跑时检查活跃会话让位，SessionEnded 后补跑 |
| P-02 | LLM 能力缓存每次纠错读文件（UI 线程同步段） | 已实施：懒加载进内存 + 写透 |
| P-03 | 电平回调双重 30ms 节流，波形帧率约减半 | 已实施：去掉 HUD 内层节流 |
| W-03 | 会话 120s 上限在引擎未就绪阶段不收尾 | **未实施**（第三批；触发窗口极窄） |
| W-05 | 目标应用粘贴处理 >1s 时恢复可能先于读取（理论竞态） | **未实施**（第三批；建议恢复延迟 1s→2s + DESIGN 记残余风险） |
| U-05 | 输入设备 COM 枚举在 UI 线程同步执行，可卡 100–300ms | **未实施**（第三批；建议与 U-04 式刷新配合后台化） |
| U-06 | 高对比度与系统深色模式未适配 | 观察项，不建议本轮处理 |
| F-07 | 识别阶段 Esc 彻底丢弃结果 | 观察项/产品决策：是否取消路径兜底复制（与插入失败策略对齐） |
| P-04 | 长听写 finalize 整段重跑与 ORT 内存池 | 引用已知项（= 遗留 L4/L6），建议真机顺手采集 >60s 的 release_to_done 分布 |

---

## 3. 未完成与遗留项汇总

> 与 DESIGN §18.6（留待数据决定）、§19（遗留项表）、§24.4（本批没做）互补：那三处是各批次
> 自身的记录，本表把它们与已删评审文档中仍开放的条目**按决策条件归类**。真机步骤见
> [VERIFICATION.md](VERIFICATION.md)。

### L1 · 待真机数据决策（先测量，阈值见 VERIFICATION.md §5）

| 项 | 内容 | 决策条件 |
| --- | --- | --- |
| 独立钩子线程（WT-07/T3-2） | 钩子仍挂 UI 线程；§18 已把设备打开挪到控制线程，但 UI 阻塞仍影响全局键鼠 | capture start p95 > 150ms、出现钩子自愈日志、或用户反馈全局按键卡顿，三者任一 |
| 重采样音质（WT-13/T3-1） | WDL 拉取模式每次短读进 flush 分支，可能影响 44.1/48kHz 音质 | 跑 `ResamplerStreamingTests`（严格模式 `VOICETYPER_RESAMPLER_STRICT=1`）：SNR ≥ 40dB 且样本偏差 <1% 则不改实现 |
| 麦克风两档延迟根因（§18.1） | 间隔 >10s 的打开慢 0.5–0.6s，推断驱动低功耗，未验证 | 按 §18.5 采集日志；若 `init` 占大头，评估预初始化 IAudioClient（须先确认不点亮麦克风指示灯） |
| A5 停顿处提前定稿分段 | 长句松键后整段重跑的根治方案，改识别管线、须与 macOS 一起评估 | 真机看 >12s 长句的 `release_to_done`；若普遍 1s 内则不值得做 |
| A6 松键后补录 | 尾音丢失（`tail_speech=1`）时补录约 150ms | 真机看 `tail_speech=1` 比例，比例高再做 |
| A7 连续听写保持麦克风 | 避免两档打开延迟，代价是任务栏指示灯多亮约 20s | 产品决策 + 真机确认 `init` 占大头 |

### L2 · 待执行的测量/验证欠账

| 项 | 说明 |
| --- | --- |
| 重采样测量未运行 | T3-1 的测试已写好（DESIGN §17.4），测量结果尚未产生 |
| 全量真机手工验证 | 各批次定义的步骤一条都没执行过（VERIFICATION.md §3–§4 汇总） |
| 常驻稳定性 | 24h（发布前 72h）稳定性验收从未做过（VERIFICATION.md §4 矩阵） |
| 安装器验证 | Inno Setup 安装器从未编译实测：缺失框架提示、旧自包含版升级清理、静默安装（PACKAGING.md） |
| arm64 | 始终只有交叉发布，无 arm64 原生进程/硬件验收 |
| ReadyToRun 启动收益 | 2026-10-09 重新开启，启动收益未实测（PACKAGING.md） |
| VW-14/VW-16 真机确认 | HUD 按 DPI 缩放、`Role.Console` 端点分类，代码完成但真机未核对 |

### L3 · 低优先待办（已确证、条件允许即做）

| 项 | 说明 |
| --- | --- |
| UX 第三批 | W-03 会话上限放宽到 Starting 阶段收尾；W-05 恢复延迟 1s→2s；U-05 设备枚举后台化；F-07 识别中 Esc 兜底复制（产品决策） |
| Alt/Win 组合键孤立松开（§19） | 主键被吞后系统看到孤立修饰键松开可能弹菜单；修法是复用右 Alt 的哑键掩码，需真机确认 |
| 热键自检鼠标误判（§19） | 只动鼠标时每 ~2.5 分钟误重装钩子；修法用 Raw Input 区分键盘 |
| 混合 DPI 浮窗字体（§19） | 字体按磅值、尺寸按显示器 DPI，混合 DPI 多屏文字大小可能不一致 |
| VW-21 托盘图标重绘 | 静态图形可构造时预渲染缓存，顺带消掉 GDI 句柄反复申请 |
| ORT 内存池 + EcoQoS（§19） | 长听写 `ctc_logits` ~200MB 不还给系统；Win11 后台降频。都要真机对比后再决定 |

### L4 · 工程欠账

| 项 | 说明 |
| --- | --- |
| packages.lock.json | VW-22/WT-20 两轮提出，至今未生成（需 Windows/带 SDK 环境跑 `dotnet restore --lock-mode`）；DESIGN §11 已改口径为「尚未启用」 |
| 真实模型 CI 专项 job | 普通 CI 无模型，CMVN/端到端推理跳过；应有带模型缓存与固定哈希的 job 显式要求不跳过 |
| 高对比度/读屏/键盘审计 | WR-020 降级项，历轮均未做，需真机且应在主功能可用后 |
| 24h/72h 常驻测试自动化 | 见 L2 |

### L5 · 已识别但**有意搁置**的性能优化（等基线，勿提前动手）

VW-18 特征前端扁平化（约 16000 次小对象分配 + 三次全量拷贝在松键关键路径）、
VW-19 打包实数 FFT（先测 fbank 占总 RTF 比例）、WT-14 分配优化表（120s 预留 7.32MiB、
waveform 缩放复制、float↔byte 往返消除、logits 解码保持 span）。共同约束：
**触碰 fbank/LFR/CMVN/CTC/模型 I/O 契约的改动必须同步评估 macOS 并共用金标准夹具，
浮点运算重排可能翻转 argmax，不能只以更快作为验收**。先 profiler 后动手。

---

## 4. 明确否决的方案（条件不变不要重复提案）

| 否决项 | 否决理由 | 出处 |
| --- | --- | --- |
| 活跃听写对象整体重构（WR-008 原案） | 控制器已有 `ReferenceEquals(session)` 身份校验，增量收益有限、回归风险高；最小收口已覆盖真实缺口 | 一轮分诊 §3.2 |
| UI Automation 逐控件写入 / 完整输入法 | 「SendInput 成功≠目标已粘贴」「1s 恢复窗口对慢应用可能不足」是剪贴板+Ctrl+V 方案的**固有限制**（macOS 同样），应作为已知限制（DESIGN §13.3）而非待修缺陷 | 一轮分诊 §3.4 |
| `UninstallDelete` 递归删除算缺陷 | `{localappdata}\Programs\VoiceTyper` 私有目录下是 Inno Setup 标准做法 | 一轮分诊 §3.9 |
| 查询系统「启动应用」禁用态（StartupApproved） | 微软未承诺该注册表布局稳定；改为 UI 文案说明「已注册，请在系统启动应用中确认」 | 一轮分诊 §3.10 |
| 二次启动经 IPC 激活已有实例 | 需命名管道+ACL+安全评审，现状（提示消息）不是故障 | 一轮分诊 §3.8 |
| `RegisterHotKey` 冲突检测 / 短按长按自动判别 | RegisterHotKey 无释放语义；自动判别把误触代价从「没发生」升级为「一直在录」（macOS 同样刻意不做） | 一轮分诊 §4 R2-1 / VW-05 |
| WT-05 应用级 Closing 门禁/取消源/在飞任务登记 | 退出后迟到的 `UiDispatcher.Post` 在消息泵结束后不会执行，实际危害小，改动面横跨所有异步入口 | 三轮分诊 §3 |
| WT-05 `RunOptions.Terminate` / 引擎健康状态 | 「原生推理永不返回」是无观测的假设场景；需真机验证 ORT 行为 | 三轮分诊 §3 |
| WT-05 机械补 CTS Dispose | 这些 CTS 不含计时器/WaitHandle，不 Dispose 不泄漏原生资源，机械补反而引入竞态 | 三轮分诊 §3 |
| WT-09 异步日志队列/单 writer/重复错误合并 | 日志量很小（每次听写几行），同步写入不是热路径瓶颈 | 三轮分诊 §3 |
| WT-12 区分加载/热身/可推理三态 | 首个会话用默认 15s 窗口无害 | 三轮分诊 §3 |
| WT-17 探测与所选设备一致 / Unknown 状态 | 产品语义变更，需配合设置页设计 | 三轮分诊 §3 |
| WT-18/WT-19 设置页状态模型与视觉规范 | 设计工作，先出设计稿再实施；三轮 §4.9 的设计规格表（窗口/侧栏/字段行/五页内容）可作起点 | 三轮分诊 §3 |
| 最终识别入队后作废未开始的预览 | 至多一个预览在飞且通常已开始执行，可取消窗口极窄 | 三轮分诊 §3 |
| ORT 自旋/线程数/工作集修剪实验、ReadyToRun A/B | 纯测量工作需真机；`SetProcessWorkingSetSize` 修剪≠释放托管/原生分配，不应无条件调用 | 三轮 §5 / §6 |
| 改任何设置都重建控制器 → 改为长生命周期服务 | 收益小（设置极少改）、改动面大 | DESIGN §19 |
| 界面语言热切换 | 托盘/设置文案构造时取值，要重建；风险收益不成比例 | DESIGN §19 |
| 切换模式目标窗口改在停止时记录 | 行为变化，需与 macOS 一起评估 | DESIGN §19 |
| C5 逐像素 alpha、E1 引导加热键步骤、引导/设置共用控件重构 | 无真机反馈支撑 / 引导页 SetBounds 布局加步骤要连布局重构（更轻替代：欢迎页加 Fn 提示行） | DESIGN §24.4 |
| WinForms IL 裁剪 / 更换 UI 栈求小体积 | WinForms 不支持裁剪；共享框架方案已达数十 MiB 级缩减 | PACKAGING.md |
| 裁剪算子的自定义 ORT | 解压体积 87% 是原生 ORT，再减需专门编译+复测金标准，维护成本不值 | PACKAGING.md |
| `PublishSingleFile` / `PublishTrimmed` | csproj 注释已记录拒绝理由（自解压与 AV 误报风险） | csproj / VW-20 |

---

## 5. 评审方法论（历轮沉淀的口径）

### 5.1 证据分级与严重程度

| 标记 | 定义 |
| --- | --- |
| S | 源码可明确证明（不等同于已在真机复现） |
| C | 结合依赖源码或官方 API 契约确认（实际表现仍需运行验证） |
| V | 待验证风险（存在风险或缺少证据，不能断言必然失败） |
| R | 改进建议（产品/工程取舍，不得描述成已发生的故障） |
| P0 | 阻断构建、录音或自动输入 |
| P1 | 影响主要使用路径、结果完整性或故障恢复 |
| P2 | 体验、维护与发布完善项 |

分诊处置四分法：**保留**（确证且整条值得修）/ **收窄**（只做确证且便宜的部分）/
**验证驱动**（先写测试或采数据，按结果决定）/ **移出**（本轮不做，记理由）。

### 5.2 交付与验证状态口径

- 状态链：待修复 → 修复中 → 代码与自动化验证完成 → 真机验证完成。
- 每批分三栏记录：**代码+自动化 / Windows x64 真机 / Windows arm64 真机**；未执行的写「未验证」，
  不得填「全部验证通过」。
- **「代码审查通过」≠「真机验证通过」**（AGENTS.md 硬性要求）。涉及 Win32、音频、剪贴板、
  热键、安装、性能的改动，只有代码检查时不得标记最终完成。
- 交叉发布（如 macOS 主机编 win-x64）不是目标架构运行证据；x64 仿真运行不能记成 arm64 验收。

### 5.3 教训

1. **「从未编译过」的教训（一轮）**：静态审查写得再自信，没有一次真实编译/测试执行就可能整体
   翻车——当时 4 个「P1」实际是功能性阻断（一按热键就挂、插入 100% 失败）。结论：恢复可编译 +
   让测试可信（R0）永远排在一切之前；CI 是性价比最高的单项投资（VW-22），它把一半以上「待验证」
   直接变成「已验证」。
2. **环境限制必须如实标注**：历轮评审多在 Linux、无 SDK、无真机环境完成，结论全部是代码审查
   结论；性能数字要么是 macOS 实测要么是结构分析，不得写成 Windows 实测。
3. **不要夸大**：DWORD 写 1 字节 ≠ 隐私泄漏已发生（第三标记可能仍生效）；工作集修剪 ≠ 内存已
   释放；float↔byte 往返消除 ≠ 数量级提速（带宽很小，只是无谓分配）。提交信息与文档措辞跟着
   证据走。
4. **不要为了让清单变绿改正确的代码**：若复现推翻了审查结论，以复现为准，记录证据并更新该条。
5. **历史文档与现实冲突时加注、不重写**（如 VW-20 ReadyToRun 先开后撤再开）：历史结论保持原样，
   用补注指向当前事实，避免「文档否定文档」。
6. **金标准红线**：fbank/LFR/CMVN 阈值 < 1e-3 不得放宽来让测试变绿；必需夹具缺失应 Fail 而非
   静默跳过；可选真实模型测试明确 Skip 并写原因。
7. **先测再改**：所有性能相关条目（L1/L5）在拿到真机基线前动手，就是拿能工作的基线去赌。
8. **注释表达不变量，不写修复史**：新注释说明当前所有权/线程/为何如此；「修复编号+历史经过」
   移入评审与变更记录（即本文与 DESIGN/CHANGELOG）。

### 5.4 交付记录模板

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

### 5.5 官方资料与固定版本依赖源码（历轮核对过的链接）

依赖固定版本：.NET 10 · ONNX Runtime **1.24.2**（NuGet）· NAudio **2.2.1** · YamlDotNet · xUnit 2.9。

- [Win32 INPUT 结构](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input) ·
  [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) ·
  [低级键盘钩子](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) ·
  [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [剪贴板格式（历史/云同步排除标记）](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats) ·
  [内核对象命名空间（Global/Local）](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces) ·
  [SetProcessWorkingSetSize](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsize)
- NAudio 2.2.1 源码：[BufferedWaveProvider](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Core/Wave/WaveProviders/BufferedWaveProvider.cs) ·
  [WdlResamplingSampleProvider](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs) ·
  [WdlResampler](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Core/Dsp/WdlResampler.cs) ·
  [WasapiCapture](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiCapture.cs)
- ONNX Runtime：[安装依赖](https://onnxruntime.ai/docs/install/) ·
  [线程调优](https://onnxruntime.ai/docs/performance/tune-performance/threading.html) ·
  [1.24.2 RunOptions.Terminate](https://raw.githubusercontent.com/microsoft/onnxruntime/v1.24.2/csharp/src/Microsoft.ML.OnnxRuntime/RunOptions.shared.cs) ·
  [1.24.2 会话配置键](https://raw.githubusercontent.com/microsoft/onnxruntime/v1.24.2/include/onnxruntime/core/session/onnxruntime_session_options_config_keys.h)
- [.NET SDK 默认包含规则](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview) ·
  [.NET 10 支持矩阵](https://raw.githubusercontent.com/dotnet/core/main/release-notes/10.0/supported-os.md) ·
  [SmartScreen 与签名信誉](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)
- [BlockingCollection.Dispose 并发限制](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.blockingcollection-1.dispose?view=net-10.0)

版本相关结论（OS 支持范围、分发策略、依赖行为）在实施和发布时应复核。
