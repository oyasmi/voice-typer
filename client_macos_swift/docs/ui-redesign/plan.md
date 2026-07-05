# VoiceTyper macOS 客户端 UI 现代化 — 实施计划

> 设计规格见 [design.md](design.md)。本文档定义分阶段落地顺序、文件级改动、
> 验证方式与风险。每个阶段独立可构建、可发布，随时可停在任一阶段边界。

## 前置事实（已核实）

- 部署目标 **macOS 14.0** → `NSSymbolEffect`、SwiftUI `Form(.grouped)`、
  `@Observable`、`NSHostingController` sizing 改进全部可用。
- `VoiceTyper.xcodeproj` 使用**显式文件引用**（非 Xcode 16 synchronized group）
  → **每个新增 `.swift` 文件必须手动登记 `project.pbxproj`**
  （`PBXBuildFile` + `PBXFileReference` + group children + Sources phase 四处）。
- 无单元测试 target → 验证以 `./build_xcode.sh`（或 `xcodebuild build`）+
  手动清单为准。
- `AudioCaptureService` 无电平输出；`NSStatusBarButton` 不支持 symbol effect
  （需内嵌 `NSImageView`）。

## 阶段总览

| 阶段 | 内容 | 对应优先级 | 预估规模 |
| --- | --- | --- | --- |
| 0 | 模型/服务层地基（paused 态、电平回调） | 支撑 P0 | 小 |
| 1 | 菜单栏翻新（图标动效、header、暂停、自启、关于） | P0 | 中 |
| 2 | HUD 行为升级（真实波形、动效、成功反馈、定位、计时） | P0 | 中 |
| 3 | HUD 胶囊化重构（形态、单层材质、动态高度） | P1 | 中 |
| 4 | 设置窗骨架迁移（toolbar 分页 + SwiftUI Form，移植权限/连接/热词页） | P1/P2 | 大 |
| 5 | 热键录制器 + 通用页 | P1/P2 | 中 |
| 6 | （可选）HUD 拖动记忆位置、识别历史 | P3 | — |

依赖关系：1、2 只依赖 0，彼此独立；3 依赖 2；5 依赖 4。
建议单独分支 + 每阶段一个 commit。

---

## 阶段 0：地基

**改动**

1. `Core/AppState.swift`
   - 新增 `case paused`（menuTitle "已暂停"、symbol `mic.slash`）。
   - 删除 `statusIcon` emoji 属性（同步删 `StatusBarController` 里的 fallback 分支）。
2. `Services/AudioCaptureService.swift`
   - 新增 `var onLevel: ((Float) -> Void)?`。
   - `append(buffer:)` 中对 `newSamples` 计算 RMS（一次 reduce，样本已是 16k float32），
     在锁外发出。注意：回调在音频线程，消费方负责切主线程。
3. `Core/VoiceTyperController.swift`
   - 新增 `var onAudioLevel: ((Float) -> Void)?`，在创建/绑定 capture service 时透传，
     `DispatchQueue.main.async` 切主线程后发出（与现有 onChunk 绑定点同处）。

**验证**：构建通过；临时在 coordinator 打日志确认说话时 level 起伏、静音时趋近 0。

## 阶段 1：菜单栏翻新

**改动（均在 `UI/StatusBarController.swift` + `App/AppCoordinator.swift`，
新增 `UI/StatusMenuHeaderView.swift`）**

1. **图标宿主**：`statusItem.button` 内嵌铺满的 `NSImageView`
   （`button.image = nil`），按 design §2.1 的表驱动更新：
   symbol + `contentTintColor` + `addSymbolEffect`/`removeAllSymbolEffects`
   （connecting → `.pulse`，recognizing → `.variableColor.iterative`）。
   状态未变化时不重设 image，避免动效重启。
2. **Header view**：新文件 `StatusMenuHeaderView.swift`——两行布局
   （状态点 + "VoiceTyper · 状态"，副行 "热键 · 服务状态"），
   挂到首个 `NSMenuItem.view`；替换原三个 disabled 项。
   `update(state:hotkeyDisplay:serverStatus:)` 签名不变，内部改为刷 header。
3. **菜单项图标**：`NSMenuItem.image = NSImage(systemSymbolName:)`，
   映射见 design §2.2。
4. **暂停听写**：新 `NSMenuItem` + `onTogglePause` 回调。
   `AppCoordinator` 增加 `private var isPaused = false` 与 `togglePause()`：
   - 暂停：`cancelServerPolling()`、`voiceTyperController?.stop()`、
     `recordingHUDController?.hideHUD()`、`currentState = .paused`、刷 UI。
   - 恢复：`isPaused = false` 后走 `reevaluateReadiness()`。
   - `reevaluateReadiness()` 开头若 `isPaused` 直接维持 paused（防轮询/权限回调复活）。
   - 录音/识别/插入进行中菜单项置灰。
5. **开机自启**：`import ServiceManagement`；菜单项 state 读
   `SMAppService.mainApp.status == .enabled`，点击 register/unregister，
   失败以 `NSAlert` 提示（沙箱外 app 正常可用）。
6. **关于**：`NSApp.orderFrontStandardAboutPanel(nil)` +
   `NSApp.activate(ignoringOtherApps: true)`。

**pbxproj**：登记 `StatusMenuHeaderView.swift`。

**验证清单**：状态图标随录音/识别变色与动效；暂停后按热键无反应、恢复后正常；
开机自启勾选态与系统设置 > 登录项一致；header 在深/浅色菜单栏下均可读。

## 阶段 2：HUD 行为升级（不动整体形态）

**改动（`UI/RecordingHUDController.swift` + `App/AppCoordinator.swift`）**

1. **真实波形**：`WaveformView` 增加 `func update(level: Float)`（快攻慢放平滑 +
   权重条形渲染，design §1.3），删除 canned `startAnimating()` 的伪随机参数动画；
   `setRecognizing()` 改为柔和顺序脉动（无电平输入时的确定型动画）。
   coordinator 绑定 `voiceTyperController.onAudioLevel → hud.updateLevel`
   （HUD 内部节流 ≥30ms）。
2. **进出场动画**：`showHUD`/`hideHUD`/transient 统一走
   `NSAnimationContext`（fade + 10pt 位移，参数见 design §1.5）。
   hide 需处理动画期间再次 show 的竞态（取消未完成动画）。
3. **成功反馈**：新增 `showSuccess()`（绿色对钩 + "已输入"，0.7s 淡出，
   复用 `showTransient` 机制）。coordinator 的 `onStateChange` 中，
   前态为 `.inserting` 且新态为 `.idle` 时调用（替代当前直接 `hideHUD()`）。
4. **计时**：<1s 隐藏；`12s` / `1:12` 格式；间隔 0.5s。
5. **定位**：抽 `targetScreen()`（鼠标所在屏，fallback main），
   y 改为 `visibleFrame.minY + 80`；`showHUD` 与 transient 共用。
6. **preview 文本**：左对齐 + 占位符 "正在聆听…"（截断策略保持 head）。

**验证清单**：说话时波形随音量起伏、静音平线、换麦克风后仍正确；
HUD 出现/消失平滑；插入成功可见绿钩；副屏鼠标下 HUD 出现在副屏；
Esc 取消、服务断连错误提示均正常。

## 阶段 3：HUD 胶囊化重构

**改动（重写 `RecordingHUDController` 的 buildUI 与窗口管理，行为接口不变）**

1. 窗口改 `NSPanel(.nonactivatingPanel)` + `appearance = .vibrantDark`。
2. 背景：删 `HUDBackgroundView` 与 preview 深色内嵌容器；单层
   `NSVisualEffectView(.hudWindow)` + dim layer（`ui.opacity` 映射见 design §1.1）
   + 1pt 描边 + `cornerCurve = .continuous`。
3. 布局按 design §1.2 重排：340 宽恒定、紧凑 48 高、展开 ≤104 高；
   底边中心锚定，高度经 `window.animator().setFrame` 动画；
   展开→紧凑 600ms 防抖。
4. 颜色全部换语义色（vibrantDark 下的 `labelColor` 族 + `systemRed/Orange/Green`）。
5. `UIConfig.width/height` 在 `AppConfig.swift` 注释标注废弃。

**验证清单**：紧凑↔展开随 partial 出现平滑生长、无抖动；两行长文本不溢出；
`ui.opacity` 改 0.5 / 1.0 观感符合预期；错误/取消/成功三种 transient 形态正确；
浅色模式下（HUD 仍深色）对比正常。

## 阶段 4：设置窗骨架迁移

**新文件（全部需登记 pbxproj）**

```
UI/Settings/SettingsViewModel.swift        @Observable 桥接层
UI/Settings/PermissionsSettingsView.swift  权限页
UI/Settings/ConnectionSettingsView.swift   连接页
UI/Settings/HotwordsSettingsView.swift     热词页
```

**改动**

1. `SetupWindowController` 保留类名与全部对外回调/入口
   （`loadEditableContent` / `updatePermissions` / `selectTab` / 各 `onXxx`），
   内部替换为 `NSTabViewController(tabStyle: .toolbar)`，每页
   `NSHostingController(rootView:)`。窗口 640 宽、不可 resize、删 footer。
   `SetupTab` 扩为 5 case（permissions/connection/hotkey/hotwords/general），
   本阶段先挂前三页 + 热词页（热键页暂时保留旧控件塞进 hosting 前的过渡页，
   或直接留到阶段 5 一起上——**选后者**：阶段 4 结束时热键配置暂缺 UI 入口，
   故阶段 4、5 必须在同一个发布内完成；开发中可接受）。
2. `SettingsViewModel`：持有 `AppConfig` 草稿、权限快照、热词文本、
   连接测试/保存状态机；对外暴露与旧回调一一对应的闭包属性，由
   `SetupWindowController` 注入。
3. **权限页**：SF Symbol 状态指示、已授权隐藏按钮、服务卡 + 汇总横幅
   （design §3.2）。
4. **连接页**：字段/校验/双按钮照搬（校验错误文案沿用
   `SetupWindowValidationError`，逻辑挪进 ViewModel）。
5. **热词页**：`TextEditor`（等宽、关智能替换需包一层
   `NSViewRepresentable` 或用 `.autocorrectionDisabled` 等 modifier，凡 SwiftUI
   覆盖不到的行为回落 `NSTextView` representable）+ 1s 防抖自动保存 +
   "已保存 · 词条数 N" 状态行。删除 reload/save 按钮路径后，
   `onSaveHotwords` 回调复用，`handleReloadHotwords` 逻辑删除。

**验证清单**：五个 tab 工具栏切换、窗口标题跟随；权限授权流程（首启弹窗、
授权后自动刷新）不回归；测试连接/保存并应用/校验错误路径全通；
热词编辑停顿 1s 自动落盘且 controller 重载生效；深浅模式切换无违和。

## 阶段 5：热键录制器 + 通用页

**新文件**：`UI/Settings/HotkeyRecorderField.swift`（AppKit 控件 +
`NSViewRepresentable` 包装）、`UI/Settings/HotkeySettingsView.swift`、
`UI/Settings/GeneralSettingsView.swift`（登记 pbxproj）。

**改动**

1. **录制器**（design §3.2 热键页）：
   - 录制态用 `NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged])`；
     `flagsChanged` 中 `.function` 置位且无其他修饰 → 捕获为 Fn。
   - keyCode → 配置字符串的映射复用 `HotkeyService.keyCode(for:)` 的逆向
     （新增 `HotkeyService.keyName(for keyCode:)` 静态方法，与现有表同源）。
   - **互斥**：进录制态回调 `onSuspendHotkey(true)` → coordinator 调
     `voiceTyperController?.stop()`（或仅 hotkeyService.stop）；退出录制
     `onSuspendHotkey(false)` → `reevaluateReadiness()` 恢复。
     必须处理窗口关闭/切 tab 时仍在录制态的清理（`viewWillDisappear` 兜底）。
   - 捕获合法即调 `onSaveConfig`（只改 hotkey 字段）落盘生效，
     Esc 取消、Delete 回默认 Fn，"使用 Fn🌐（推荐）"预设按钮。
2. **通用页**：开机自启 Toggle（与菜单项同源读写 `SMAppService`）；
   HUD 不透明度 Slider —— 新回调 `onPreviewHUDOpacity(Double)`
   （coordinator 转 `recordingHUDController.previewOpacity()`，HUD 需增加
   临时展示 + 实时改 dim layer 的方法），松手时经 `onSaveConfig` 落盘 `ui.opacity`。
3. 移除 `SetupWindowController` 中旧热键控件、`applyPrimaryButtonStyle` /
   `applyBadgeStyle` 等手绘样式辅助的全部残留。

**验证清单**：录制 `⌃⌥Space`、单独 Fn、非法键（如 `§`）三条路径；
录制期间按 Fn 不触发录音、退出后恢复；录制态直接关窗无泄漏（再开窗状态正常）；
透明度滑杆拖动 HUD 实时预览、松手落盘、重启后生效。

## 阶段 6（可选，另行排期）

- HUD 拖动 + 位置记忆（`ui` 配置新增 `position` 字段，需与 Windows 客户端
  确认配置兼容性后再动）。
- 识别历史菜单（隐私敏感，先讨论再设计）。

---

## 横切事项

**pbxproj 维护**：每阶段新增文件后，在 `project.pbxproj` 补
PBXFileReference / PBXBuildFile / group children / Sources build phase 四处；
构建失败时优先怀疑漏登记。

**验证方式**：每阶段 `cd client_macos_swift && ./build_xcode.sh`（或
`xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper build` 快速迭代），
再按该阶段"验证清单"手测。回归基线（每阶段都要过）：
Fn 按住说话 → partial 预览 → 松键 → 文本插入；Esc 取消；服务端关掉后的
error → connecting → 自动恢复链路；首启权限流程。

**版本与文档**：全部落地后 bump `Support/Constants.swift` 版本与
`Info.plist`；若阶段 6 动了配置格式，需同步根目录 `CLAUDE.md` 的配置示例
（阶段 0–5 不改配置格式，仅 `ui.width/height` 标注废弃，无需改）。

**风险与对策**

| 风险 | 对策 |
| --- | --- |
| `NSSymbolEffect` 在 status bar 内嵌 `NSImageView` 上的行为（模板图 + tint + 动效叠加）有未知边角 | 阶段 1 最先做一个最小验证；不行则退化为"仅变色、无动效"，不阻塞其余项 |
| 无边框窗口动画 window frame 时阴影/闪烁问题 | 动画中 `invalidateShadow()`；仍异常则退化为固定展开高度（放弃动态生长，保留其余改动） |
| `NSHostingController` 在 `NSTabViewController` 中的尺寸自适应 | 固定窗口尺寸 + Form 内滚动，避开自适应高度这一最脆弱路径 |
| 热键录制器与全局 event tap 冲突 | 录制期间挂起 HotkeyService（已入设计），并做关窗兜底清理 |
| 阶段 4/5 拆分导致中间态热键无 UI | 两阶段绑定在同一发布；开发分支上可分 commit |
