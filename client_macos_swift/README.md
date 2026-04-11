# VoiceTyper macOS Swift Client

基于 `AppKit` 的原生 macOS 状态栏客户端实现，目标是对标现有 `client_macos`，并优先解决以下问题：

- 权限引导与首启成功率
- 稳定性与系统集成
- 文本注入兼容性
- 分发、签名、公证与自动更新链路

当前阶段已经补齐原生 Xcode 工程，可直接用 Xcode 或 `xcodebuild` 构建与打包。

## 开发要求

- macOS 14+
- Swift 6.2+
- 完整 Xcode 用于最终打包与签名

## 当前实现范围

- 状态栏应用骨架
- 首启权限窗口
- 配置加载与 YAML 兼容
- 输入监控 / 辅助功能 / 麦克风权限中心
- 录音 / 热键 / 识别 / 文本插入的服务接口与首版实现

## 打开工程

```bash
cd client_macos_swift
open VoiceTyper.xcodeproj
```

## 命令行构建

```bash
cd client_macos_swift
./build_xcode.sh
```

构建完成后会生成：

- `dist/VoiceTyper.app`
- `dist/VoiceTyper-macOS.zip`
- `dist/VoiceTyper-macOS.dmg`

其中 `DMG` 内会附带一个极简 `INSTALL.txt`，引导用户完成拖拽安装和首次放行。

## 首次安装

最短路径如下：

1. 打开 `VoiceTyper-macOS.dmg`
2. 将 `VoiceTyper.app` 拖到 `Applications`
3. 从“应用程序”中打开 `VoiceTyper`
4. 如果首次打开被系统拦截，到“系统设置 > 隐私与安全性”点击“仍要打开”

更简洁的对外发布文案见 [docs/install.md](/Users/oyasmi/projects/voice-typer/client_macos_swift/docs/install.md:1)。

## 说明

```bash
ruby scripts/generate_xcodeproj.rb
```

`generate_xcodeproj.rb` 用于重新生成 `.xcodeproj`。如果你修改了源码目录结构或新增文件，重新执行一次即可。
