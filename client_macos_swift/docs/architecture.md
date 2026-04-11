# client_macos_swift 设计文档

## 目标

`client_macos_swift` 是 VoiceTyper 的原生 macOS 客户端实现。它不是演示性重写，而是面向长期维护与正式分发的主力客户端候选。

本实现优先解决四个问题：

1. 权限申请与首启引导
2. 稳定性与系统事件监听可靠性
3. 文本注入兼容性
4. 分发、签名、公证与后续自动更新能力

## 技术取舍

### UI 技术

- 使用 `AppKit`
- 不引入 SwiftUI
- 原因：本应用是典型状态栏工具，窗口极少，交互简单，`AppKit` 更直接、更稳定、更易控

### 架构原则

- KISS，避免过度分层
- 状态统一收敛到单一控制器
- 权限、热键、录音、识别、文本插入各自独立服务化
- 服务端协议保持兼容，不重造接口

## 模块划分

```text
App
  AppDelegate
  AppCoordinator

Core
  AppState
  AppConfig
  ConfigStore
  PermissionCenter
  VoiceTyperController

Services
  HotkeyService
  AudioCaptureService
  ASRClient
  TextInsertionService

UI
  StatusBarController
  SetupWindowController
  RecordingHUDController

Support
  Constants
  Logger
```

## 首启体验设计

应用启动后立即执行：

1. 加载配置
2. 检查权限
3. 检查服务端连通性
4. 若存在未满足项，则弹出 Setup 窗口
5. 所有必要条件满足后进入常驻状态栏模式

### 权限项

- 麦克风：使用系统 API 主动请求
- 辅助功能：使用 `AXIsProcessTrustedWithOptions` 触发系统授权提示
- 输入监控：使用 `CGPreflightListenEventAccess` / `CGRequestListenEventAccess`

### 首启窗口设计

窗口展示四个状态卡片：

- 麦克风权限
- 辅助功能权限
- 输入监控权限
- 识别服务连通状态

每项都有：

- 当前状态
- 一键授权 / 重试按钮
- 失败时打开系统设置按钮

## 热键设计

最终方案采用双后端：

- 标准组合键优先 `Carbon RegisterEventHotKey`
- `Fn` 与特殊场景使用 `CGEventTap`

当前首版代码先落地 `CGEventTap` 后端，并为后续补充 Carbon 后端预留服务边界。

## 录音设计

- 使用 `AVAudioEngine`
- 输入设备格式不做假设
- 使用 `AVAudioConverter` 统一转为服务端要求的：
  - `16kHz`
  - `float32`
  - `mono`

## 识别协议

继续兼容现有服务端：

- `GET /health`
- `POST /recognize`
- `Content-Type: application/octet-stream`
- 请求体直接发送 `16kHz float32` 原始音频字节
- 保留 `Authorization`、`X-Hotwords`、`llm_recorrect`

## 文本注入策略

采用两级策略：

1. `Accessibility` 直写
2. 剪贴板粘贴回退

### 一级：Accessibility 直写

- 获取当前焦点控件
- 若支持 `AXValue` 与 `AXSelectedTextRange`，直接插入或替换选中文本
- 成功时不污染剪贴板

### 二级：剪贴板回退

- 备份整个 Pasteboard 条目
- 写入待插入文本
- 模拟 `Cmd+V`
- 确认未被用户修改后再恢复剪贴板

## 状态栏菜单

建议菜单结构如下：

- 当前状态
- 当前热键
- 服务端状态
- 分隔线
- 开始/暂停
- 权限与设置
- 重新连接服务
- 打开配置目录
- 关于
- 退出

## 分发方案

最终目标：

- `Developer ID Application` 签名
- `Hardened Runtime`
- `notarization`
- `.dmg` 分发
- 后续接入 `Sparkle 2`

不走 Mac App Store，也不默认使用 App Sandbox。

## 配置兼容策略

继续兼容已有：

- `~/.config/voice_typer/config.yaml`
- `hotwords.txt`

这样用户可直接迁移，Python 客户端与 Swift 客户端可共用服务端与词库配置。

## 当前实现阶段

第一阶段目标：

- 建立可运行骨架
- 实现权限中心
- 实现状态栏与首启窗口
- 打通配置、热键、录音、识别、文本注入主链路

第二阶段目标：

- 增补 Carbon 热键后端
- 偏好设置窗口
- 开机启动
- 签名、公证与自动更新
