# VoiceTyper macOS 客户端 UI 现代化 — 设计规格

> 配套实施计划见 [plan.md](plan.md)。本文档回答"改成什么样、为什么"；plan.md 回答"按什么顺序、动哪些文件"。

## 0. 背景与目标

当前 UI（HUD、菜单栏、设置窗口）功能完整，但视觉与交互停留在"手绘 AppKit"层面：
硬编码 RGB、手绘渐变盖死系统磨砂、假波形动画、无进出场动画、
`NSTabView(.topTabsBezelBorder)` 老式分页、手填文本框式热键配置。

**目标**：对齐现代 macOS 工具（系统听写、Raycast 等）的观感与交互，同时不动
核心架构（状态机、服务分层、AppCoordinator 接线方式全部保留）。

**四条设计原则**（贯穿所有改动）：

1. **少手绘、多系统** — 语义色（`systemRed` / `labelColor` / `controlAccentColor`）、
   系统材质（`NSVisualEffectView`）、SF Symbols、系统控件样式；手调 RGB、手绘渐变、
   `bezelColor` 染色一律移除。这样深浅模式与未来 macOS 视觉更新自动跟进。
2. **状态可感知** — 波形反映真实音量；插入成功有明确反馈；菜单栏图标随状态变色/动效。
3. **形态轻量** — HUD 胶囊化、按内容伸缩；出现/消失有动画，不闪现。
4. **即时生效为主、显式保存为辅** — 有校验/连接成本的（服务连接）保留"测试 + 保存"；
   其余（热词、热键、通用开关）改为即时/自动保存。

**非目标**：不迁移 App 生命周期到 SwiftUI（保留 AppKit `NSApplicationDelegate`）；
不改 wire protocol / 状态机语义；不做多语言。

---

## 1. HUD（录音悬浮窗）

### 1.1 形态：单层材质的胶囊

**窗口**：`NSPanel`，`styleMask = [.borderless, .nonactivatingPanel]`，
`level = .floating`，`ignoresMouseEvents = true`，collectionBehavior 不变。
**强制深色**：`panel.appearance = NSAppearance(named: .vibrantDark)`，
之后文本一律用 `labelColor` / `secondaryLabelColor` / `tertiaryLabelColor`
（在 vibrantDark 下自动解析为合适的白色系），不再写 `NSColor(white:alpha:)`。

**背景（替换现有三层结构）**：只保留一层 `NSVisualEffectView`
（`material: .hudWindow`, `state: .active`, `blendingMode: .behindWindow`），
圆角 `cornerCurve = .continuous`，外描边 1pt `白 10%`。
删除 `HUDBackgroundView` 手绘渐变和 preview 的深色内嵌容器。

**`ui.opacity` 的新语义**：材质之上叠一层纯黑 dim layer，
`dimLayer.opacity = clamp(ui.opacity, 0.5, 1.0) × 0.4`（默认 0.85 → 0.34）。
opacity 越大背景越沉、文字对比越强；调到下限仍保有可读性。
`ui.width` / `ui.height` 字段保留解析但标记废弃（HUD 尺寸由内容决定）。

**几何**：

| 形态 | 触发 | 尺寸 | 圆角 |
| --- | --- | --- | --- |
| 紧凑 | 录音开始、尚无 partial 文本 | 340 × 48 | 24（整高，即胶囊） |
| 展开 | 出现 partial 文本 / transient 消息 | 340 × 最高 104 | 24 |

宽度恒定 340，只有高度变化；窗口锚定**底边中心**，展开时向上生长。
高度动画：`NSAnimationContext` 0.20s easeOut，同步动画 window frame 与内容布局。
**防抖**：由展开收回紧凑需 partial 持续为空 ≥ 600ms，避免文本闪烁时 HUD 抽搐。

### 1.2 布局

```
紧凑：  ( ● ▂▄▆▄▂  录音中              12s )
        └ 状态点 8pt · 波形 56×20 · 状态文字 13pt semibold · spacer · 计时 12pt 等宽

展开：  ( ● ▂▄▆▄▂  录音中              12s )
        (  今天下午三点开会，记得带上上次的评审  )
        └ 顶行同紧凑；下方 preview 14pt regular，左对齐，最多 2 行
```

- 顶行内容全部垂直居中于 48pt 行内；水平 padding 16pt。
- **preview 文本**：左对齐（不再右对齐）、`byTruncatingHead`（保住最新识别的尾部）、
  最多 2 行。空文本时占位 "正在聆听…"（`tertiaryLabelColor`）。
- preview 与顶行之间仅用 8pt 间距分隔，不加分隔线、不加内嵌底色盒。

### 1.3 真实电平驱动的波形

现有 `WaveformView` 的 5 根条改为由麦克风真实 RMS 驱动：

- **采集**：`AudioCaptureService` 在 `append(buffer:)` 中对转换后的 16kHz 样本计算
  RMS，通过新回调 `onLevel: ((Float) -> Void)?` 发出（每次 tap 回调一次，约 20–60ms
  一发，无需额外定时器；HUD 侧节流到 ≥ 30ms 刷新一次）。
- **归一化**：`db = 20·log10(rms)`，clamp 到 [-50, -10] dB → 线性映射 0…1。
- **平滑**：快攻慢放 — `display = max(level, display × 0.82)`，静音时条形几乎平线
  （最低高度 3pt），说话时明显起伏。
- **渲染**：5 根条各配固定权重 `[0.55, 0.85, 1.0, 0.7, 0.9]` 乘以 level，
  加每根 ±10% 的缓变抖动避免机械感；用 CALayer 高度 + 0.08s 隐式动画更新。
- **移除**外框盒（当前波形有自己的圆角边框背景），波形裸置于顶行。

**价值**：不只是好看——用户选错麦克风 / 设备被占用时，平线波形立刻暴露问题。

### 1.4 状态与配色（全部语义色）

| 阶段 | 状态点 | 波形 | 状态文字 | 计时 | 停留 |
| --- | --- | --- | --- | --- | --- |
| 录音中 | `systemRed` 呼吸 | 白 90%，电平驱动 | 录音中 | 显示 | — |
| 识别中 | `systemOrange` 常亮 | `systemOrange`，无电平→柔和顺序脉动 | 识别中 | 冻结 | — |
| 已输入（新增） | `checkmark.circle.fill` `systemGreen` | 隐藏 | 已输入 | 隐藏 | 0.7s 后淡出 |
| 已取消 | 灰点 | 隐藏 | 已取消 | 隐藏 | 1.0s |
| 错误 | `systemRed` 常亮 | 隐藏 | 错误 + 消息占 preview 行（左对齐） | 隐藏 | 2.5s |
| 警告闪现 | 不变 | 不变 | 临时替换 1.2s 后还原 | 不变 | — |

「已输入」为新增成功反馈：当前插入成功零反馈，用户只能靠光标处文字确认。
由 AppCoordinator 在 `inserting → idle` 转换时触发 `showSuccess()`。

### 1.5 动效

| 场景 | 动画 |
| --- | --- |
| 出现 | alpha 0→1 + 上移 10pt，0.18s easeOut |
| 消失 | alpha 1→0，0.22s easeIn |
| 紧凑↔展开 | 高度 + 布局，0.20s easeOut（见 1.1） |
| 阶段切换（录音→识别等） | 状态点/文字 crossfade 0.15s |

transient 提示（错误/取消）复用同一窗口与同一套进出场动画。

### 1.6 计时与位置

- **计时**：elapsed < 1s 不显示（消灭孤零零的 "0s"）；1–59s 显示 `12s`；
  ≥ 60s 显示 `1:12`。刷新间隔 0.5s。
- **位置**：改为**鼠标指针所在屏幕**（`NSEvent.mouseLocation` 匹配
  `NSScreen.screens`，找不到回退 `.main`），水平居中，
  `y = screen.visibleFrame.minY + 80`（`visibleFrame` 自动避开 Dock）。

---

## 2. 菜单栏

### 2.1 状态图标：变色 + 符号动效

`statusItem.button` 内嵌一个铺满的 `NSImageView` 子视图（`NSStatusBarButton` 本身
不支持 `NSSymbolEffect`，`NSImageView` 支持，macOS 14 API），图标仍为 template
以适配菜单栏深浅，通过 `contentTintColor` 做状态着色：

| AppState | 符号 | 着色 / 动效 |
| --- | --- | --- |
| booting | `hourglass` | 默认 |
| setupRequired | `exclamationmark.triangle` | `systemOrange` |
| connecting | `arrow.triangle.2.circlepath` | 默认 + `.pulse` 循环 |
| idle | `mic` | 默认 |
| recording | `mic.fill` | `systemRed` |
| recognizing | `waveform` | 默认 + `.variableColor.iterative` 循环 |
| inserting | `character.cursor.ibeam` | 默认 |
| paused（新增） | `mic.slash` | `secondaryLabelColor` |
| error | `exclamationmark.circle` | `systemRed` |

`AppState.statusIcon`（emoji fallback）删除——SF Symbols 在目标系统上必然存在。

### 2.2 菜单结构

```
┌──────────────────────────────────────┐
│ ● VoiceTyper · 就绪                  │  ← 自定义 header view（NSMenuItem.view）
│   Fn🌐 · 127.0.0.1:6008 已连接        │     高约 52pt，状态点用语义色
├──────────────────────────────────────┤
│ ⏸  暂停听写                          │  ← 新增，toggle；暂停时变"恢复听写"
├──────────────────────────────────────┤
│ ⚙  权限与设置…                 ⌘,    │
│ ↻  重新连接服务                      │
│ 📁 打开配置目录                      │
├──────────────────────────────────────┤
│ ☑  开机自启                          │  ← 新增，SMAppService.mainApp
│ ⓘ  关于 VoiceTyper                   │  ← 新增，版本号从设置窗 footer 移到这里
├──────────────────────────────────────┤
│ ⏻  退出                        ⌘Q   │
└──────────────────────────────────────┘
```

- 三行 disabled 灰字（"启动中 / 热键: - / 服务: 检查中"）合并为一个自定义 header
  view：一行「状态点 + 应用名 + 状态」，副行小字「热键 · 服务端点与连接状态」。
  header 不可点击、不高亮。
- 所有可点菜单项加 `NSMenuItem.image`（SF Symbol）：`gearshape`、`arrow.clockwise`、
  `folder`、`pause.circle`/`play.circle`、`info.circle`、`power`。
- **暂停听写**：新增 `AppState.paused`。暂停 = 停 HotkeyService、停服务轮询、藏 HUD、
  图标 `mic.slash`；恢复 = 走 `reevaluateReadiness()` 完整就绪流程。
  仅在 idle / connecting / error 态可暂停（录音进行中置灰）。
- **开机自启**：`SMAppService.mainApp.register()/unregister()`，勾选态直接查询
  `SMAppService.mainApp.status`，不落 config。
- **关于**：`NSApp.orderFrontStandardAboutPanel`（Info.plist 已含版本信息）。

---

## 3. 设置窗口

### 3.1 骨架：toolbar 分页 + SwiftUI Form 内容

**骨架**保留 AppKit：`SetupWindowController` 继续持有窗口与全部对外回调
（`onSaveConfig` / `onSaveHotwords` / `onRequestPermission` … AppCoordinator 接线不变），
内部换为 `NSTabViewController(tabStyle: .toolbar)` —— 这是 System Settings 同款的
工具栏式分页，纯 AppKit API，无兼容风险。

**每个分页的内容**是一个 `NSHostingController`，承载 SwiftUI `Form` +
`.formStyle(.grouped)`（macOS 14 直接获得系统设置风格的分组卡片、对齐、
深浅模式）。数据经由一个 `@Observable` 的 `SettingsViewModel` 桥接到现有回调。

窗口：640 宽、不可 resize、无底部 footer（删除自定义"关闭"按钮和版本号——
标题栏红点关窗、版本号入"关于"）。窗口标题随分页切换（NSTabViewController 自动）。

### 3.2 分页规划（3 → 5 页）

| 页 | 图标 | 内容 | 保存策略 |
| --- | --- | --- | --- |
| 权限 | `checkmark.shield` | 三项权限 + 服务连通卡 + 汇总横幅 | 即时（状态驱动） |
| 连接 | `network` | scheme/host/port/API Key/流式/LLM 纠错 | **显式**：测试连接 + 保存并应用 |
| 热键 | `keyboard` | 按键录制器 + Fn 预设 | 捕获即保存 |
| 热词 | `text.book.closed` | 等宽 TextEditor + 词条数 + 附加词库说明 | **自动**：停止输入 1s 后写盘 |
| 通用 | `gearshape` | 开机自启、HUD 不透明度滑杆 | 即时 |

**权限页**：状态指示从染色 "●" 文本改为
`checkmark.circle.fill`(绿) / `xmark.circle.fill`(红) / `exclamationmark.circle.fill`(橙)；
已授权行**隐藏**"授权 / 系统设置"按钮（而非置灰）。服务卡与汇总横幅逻辑保持，
"热键 Fn🌐" badge 从服务卡移除（信息归属热键页与菜单 header）。

**连接页**：字段与校验逻辑照搬现状（含 `SetupWindowValidationError` 语义）；
保留"测试连接 / 保存并应用"双按钮——连接配置改错代价高，显式保存合理。
主按钮不再手绘 `bezelColor`，SwiftUI `.buttonStyle(.borderedProminent)` /
默认按钮即获得系统强调色。

**热键页**（体验短板最深的单点，替换 segmented + 4 checkbox + 手填文本框）：

- 核心是 `HotkeyRecorderField`（`NSViewRepresentable` 包一个 AppKit 控件）：
  - 未聚焦：显示当前热键（`⌃⌥Space` / `Fn🌐` 符号化展示）。
  - 点击进入录制态：显示"按下快捷键…"，`NSEvent.addLocalMonitor` 捕获
    `keyDown`（组合键）与 `flagsChanged`（单独 Fn）。
  - Esc 取消录制、Delete 清除。
  - 捕获后经 `HotkeyService.isSupportedKey` 校验，不支持的键内联报错并留在录制态。
  - **录制期间必须暂停全局 HotkeyService**（否则按 Fn 会当场触发录音）——
    进录制态调 coordinator 提供的挂起回调，退出恢复。
- 旁附"使用 Fn 🌐（推荐）"预设按钮一键回默认。
- 捕获成功即走 `onSaveConfig` 落盘生效，页面显示"热键已更新"轻提示。

**热词页**：编辑区改 SwiftUI `TextEditor`（等宽字体、关闭智能替换）；
去掉"重新加载 / 保存并应用"双按钮，改**防抖自动保存**（停止输入 1s），
底部状态行显示"已保存 · 词条数 N"。附加词库数量提示保留。

**通用页**（新增，把 config 里已存在但无 UI 的项暴露出来）：

- 开机自启（与菜单栏项同源，`SMAppService`）。
- HUD 背景不透明度：Slider 0.5–1.0，拖动即写 `ui.opacity` 并让 HUD
  以当前值实时预览（拖动时短暂展示 HUD）。

### 3.3 数据流

```
SwiftUI Form ⇄ SettingsViewModel(@Observable) ⇄ SetupWindowController 既有回调 ⇄ AppCoordinator
```

- ViewModel 持有草稿态；连接页显式提交，其余页即时提交。
- `loadEditableContent` / `updatePermissions` 两个既有入口改为更新 ViewModel，
  SwiftUI 自动刷新——AppCoordinator 一行不用改调用方式。
- 新增回调仅两个：`onSuspendHotkey(Bool)`（录制器用）、
  `onPreviewHUDOpacity(Double)`（通用页滑杆用）。

---

## 4. 涉及的模型/服务层小改动汇总

| 位置 | 改动 | 原因 |
| --- | --- | --- |
| `AppState` | 新增 `.paused`；删除 `statusIcon` emoji | 暂停功能；fallback 无用 |
| `AudioCaptureService` | 新增 `onLevel: ((Float) -> Void)?`，`append` 内算 RMS | 真实波形 |
| `VoiceTyperController` | 透传 `onAudioLevel` 到主线程 | 同上 |
| `AppCoordinator` | 暂停/恢复；`inserting→idle` 触发 `showSuccess`；HUD 透明度预览与热键挂起回调 | 新交互 |
| `UIConfig` | `width`/`height` 标注废弃（保留解析） | HUD 尺寸内容自适应 |
| `HotkeyService` | 暴露挂起/恢复（可复用现有 `stop()`/`start(with:)`） | 录制器互斥 |

配置文件格式**不发生破坏性变更**（跨平台共享，见根目录 CLAUDE.md）。

## 5. 明确不做 / 后续可选

- HUD 可拖动与位置记忆（P3，需临时开启 mouse events + 落 config，独立小项）。
- 识别历史菜单（P3，涉及隐私考量，需单独讨论是否落盘）。
- 浅色 HUD 形态（系统 HUD 惯例即深色，强制 vibrantDark 是有意选择）。
- Windows 客户端同步改造（本 spec 仅 macOS；状态机语义未动，不影响共享协议）。
