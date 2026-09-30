# Windows 分发体积检查与瘦身记录

日期：2026-09-30。用户明确选择使用电脑上已安装的 .NET 10 Desktop Runtime，不将共享运行时
打包到 VoiceTyper 中。下面的体积均不包含语音识别模型；MiB = 1,048,576 字节。

## 原产物为什么有 135 MiB

检查对象：`windows/bin/preview-win-x64`，共 287 个文件、142,175,593 字节（135.59 MiB）。
按当前代码重新采用原默认配置发布，得到完全相同的文件总大小。

| 内容 | 体积 | 判断 |
| --- | ---: | --- |
| .NET / WinForms 共享运行时及语言资源 | 117.04 MiB | 原配置 `SelfContained=true` 导致随包携带；现在交由系统安装的运行时提供 |
| ONNX Runtime 原生库、托管 API 与导入库 | 14.32 MiB | 本地识别必需；其中原生 `onnxruntime.dll` 单独就有 13.49 MiB |
| NAudio 全部模块 | 1.16 MiB | 引入了没用到的 ASIO、MIDI、WinMM、音频界面与总包门面 |
| YamlDotNet | 0.75 MiB | 配置、迁移及模型前端参数解析实际在用，保留 |
| System.Numerics.Tensors | 0.92 MiB | ONNX Runtime 的依赖，保留 |
| 应用本身、启动器、清单、PDB、图标与提示词 | 1.40 MiB | 主程序集原为 0.90 MiB；关闭预编译后为 0.42 MiB |

原目录压成 ZIP 为 58,947,363 字节（56.22 MiB）。135 MiB 是解压后的目录大小，不能直接当成
用户下载大小。框架 DLL 看起来有些并未被业务直接使用，但不能按文件名手动删除来代替可靠的部署方式。

## 实际对照与最终产物

| 配置 | 文件数 | 解压后字节数 | 解压后 MiB | ZIP MiB |
| --- | ---: | ---: | ---: | ---: |
| 原 x64：自包含、ReadyToRun、全部语言 | 287 | 142,175,593 | 135.59 | 56.22 |
| 仅关闭 ReadyToRun | 287 | 139,343,081 | 132.89 | 55.01 |
| 自包含、关闭 ReadyToRun、仅简体资源、不分发 PDB | 238 | 130,924,905 | 124.86 | 52.71 |
| 初步依赖框架实验，保留原 NAudio 总包 | 20 | 16,487,359 | 15.72 | 5.96 |
| **最终 x64：依赖系统框架、精简音频引用、无 PDB / `.lib`** | **13** | **16,299,594** | **15.54** | **5.88** |
| **最终 arm64：同上** | **13** | **18,961,070** | **18.08** | **6.56** |

最终 x64 目录比原产物缩小 **88.54%**。ZIP 使用与 `build.bat` 相同的 `Compress-Archive`
Optimal 压缩；x64 ZIP 为 6,167,740 字节，arm64 ZIP 为 6,874,308 字节。
安装器采用另一种压缩算法，尚未编译实测，不能把 ZIP 数字当作安装器大小。

最终产物位于 `windows/dist/win-x64`、`windows/dist/win-arm64`，便携 ZIP 位于同级目录。
确认旧预览进程已退出后，`windows/bin/preview-win-x64` 也已替换为相同的轻量版，发布验收通过。
模型、用户配置与密钥目录不属于此次发布清理范围。

## 已落实的发布规则

- `VoiceTyper.csproj`、`build.bat` 与 CI 使用依赖框架发布（`SelfContained=false`）。
- 默认关闭 ReadyToRun：额外携带 IL 与预编译代码的启动收益此前尚未实测；不影响 ORT 原生推理实现。
- NAudio 总包改为 `NAudio.Wasapi`，继续传递引用 `NAudio.Core`；不改采集与重采样代码。
- 移除编译器提示冗余的 ProtectedData 包引用，DPAPI 使用 WindowsDesktop 共享框架提供的程序集。
- 发布不含调试 PDB 或原生链接用 `.lib`，保留运行必需的 DLL、图标和 LLM 提示词。
- `scripts/verify_publish.ps1` 校验框架依赖声明和必需文件，拒绝携带共享运行时、无用音频模块、
  模型、测试程序集或开发用文件的产物。构建与 CI 共用该检查。
- 安装器按目标架构检查官方安装器登记的稳定版 .NET 10.0.x Core 和 Desktop 框架；缺少时
  显示下载地址并停止，不捆绑或自动下载运行时。便携版使用 .NET 启动器的原生缺失框架提示。
- 旧自包含版本升级时，安装器按 `installer/legacy-self-contained-files.iss` 中的 274 个已知
  历史文件名清理多余文件，不用通配符删除用户额外文件。便携版请解压到空目录，避免混入旧包。

运行前提是与应用架构一致的 **.NET 10 Desktop Runtime**。普通 .NET Runtime、ASP.NET Core
Runtime、.NET 8/9 或其他架构的运行时不能代替；.NET 10 SDK 已包含桌面运行时。
若用户缺少运行时，首次使用还需要安装该共享组件；轻量应用包没有消除这项外部下载需求。

## 验证证据与边界

- Windows x64 本机，SDK 10.0.401、共享运行时 10.0.12；x64 / arm64 均发布成功，13 个文件的
  目录检查均通过。额外验证了旧自包含包会被验收脚本拒绝。
- 正式测试工程：**288 通过、0 失败、2 跳过**。跳过项是可选设置预览渲染和真实网络下载；
  真实模型语音夹具识别及分段预览一致性测试已运行通过。
- 对实际 x64 发布文件的副本单独执行冒烟：WinForms 创建窗体、DPAPI 往返、WASAPI 类型加载、
  真实模型推理均成功；确认 WinForms 加载路径为
  `C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.12\System.Windows.Forms.dll`。
  测试没有打开麦克风、触发热键、修改用户配置或调用真实 LLM 服务。
- 当前机器未安装 Inno Setup，**未编译安装器，也未验证安装器的缺失框架提示、历史文件清理或升级行为**；
  脚本只完成代码审查与 API 文档核对。arm64 只完成交叉发布，不代表 arm64 真机验证。
- NuGet 漏洞数据源无法联网，保留 NU1900 提示；缓存依赖可用，编译与测试成功。另有原有
  HudTextLayoutTests 的 xUnit2000 提示，不属于此次发布修改。

手工验收：

1. 退出旧版，运行轻量 x64 版：测试右 Ctrl 按住/切换听写、设备切换、最终文字插入与剪贴板恢复。
2. 在应用设置中启用 LLM，用原配置进行一次纠错并确认密钥读取正常。
3. 在没有 .NET 10 Desktop Runtime 的干净机器测试便携启动提示；分别测试仅装 Core Runtime、
   .NET 8/9、错误架构、正确 Desktop Runtime 的情况，不在开发机卸载已有运行时来模拟。
4. 安装 Inno Setup 后用 `build.bat` 编译两个安装器，检查中英文缺失框架提示和静默安装失败行为。
5. 从旧自包含安装升级：确认旧运行时文件被移除、用户额外文件保留、配置/模型/密钥仍可使用，
   并确认安装目录体积与新发布目录接近。arm64 在真实 arm64 电脑完成相同步骤。

## 后续还能缩减什么

当前 x64 包中原生 ONNX Runtime 已占约 87% 的解压体积。若要让**解压后的应用小于 10 MiB**，
仅清理零碎文件不足，需要专门编译裁减算子的 ORT；这会增加维护成本，并需要复测动态量化模型、
图优化产生的算子及两个平台的金标准。当前保持与 macOS 一致的 ORT 1.24.2，不做未经验证的替换。

不尝试 WinForms IL 裁剪或以改写 UI 栈来追求更小产物；当前改用共享框架已达到数十 MiB 范围。

依据：[微软运行时说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、
[ReadyToRun 取舍](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)、
[WinForms 裁剪限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)、
[ORT 算子裁减](https://onnxruntime.ai/docs/reference/operators/reduced-operator-config-file.html)、
[Inno Setup 注册表枚举 API](https://jrsoftware.org/ishelp/topic_isxfunc_reggetvaluenames.htm)。
