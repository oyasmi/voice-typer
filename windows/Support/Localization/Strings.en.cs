using System.Collections.Generic;

namespace VoiceTyper.Support;

/// <summary>
/// 英文文案表。键是中文原文（见 <see cref="L10n"/>），值是对等的英文表述。
///
/// 要求与中文一样"把话说清楚"：不做字面直译，也不因为是第二语言就把长句砍成短提示——
/// 中英双语同等程度支持是这张表存在的前提。措辞尽量与 macOS 侧
/// <c>Strings+en.swift</c> 保持一致，两个平台的英文界面应该读起来像同一个产品。
///
/// 新增 <c>L10n.T(...)</c> / <c>L10n.F(...)</c> 调用后必须在这里补一条，
/// 否则 <c>LocalizationTests</c> 会失败。占位符（<c>{0}</c>…）的个数与顺序必须与中文一致。
/// </summary>
internal static partial class L10n
{
    private static readonly Dictionary<string, string> EnglishTable = new()
    {
        // ── 托盘菜单 / 应用级 ────────────────────────────────
        ["启动中"] = "Starting up",
        ["热键：{0}"] = "Hotkey: {0}",
        ["引擎：{0}"] = "Engine: {0}",
        ["检查中"] = "checking",
        ["设置..."] = "Settings...",
        ["暂停听写"] = "Pause Dictation",
        ["恢复听写"] = "Resume Dictation",
        ["打开配置目录"] = "Open Config Folder",
        ["开机自启"] = "Launch at Login",
        ["关于 {0}"] = "About {0}",
        ["退出"] = "Quit",
        ["离线语音输入工具，基于 SenseVoice-Small。"] = "An offline voice input tool powered by SenseVoice-Small.",
        ["VoiceTyper 已经在运行（请检查系统托盘）。"] = "VoiceTyper is already running (check the system tray).",
        ["VoiceTyper 启动失败：\n\n{0}"] = "VoiceTyper failed to start:\n\n{0}",

        // ── 应用状态 ────────────────────────────────────────
        ["需要完成设置"] = "Setup required",
        ["需要下载语音模型"] = "Speech model needs to be downloaded",
        ["下载模型 {0}%"] = "Downloading model {0}%",
        ["模型加载中…"] = "Loading model…",
        ["就绪"] = "Ready",
        ["录音中..."] = "Recording...",
        ["识别中..."] = "Recognizing...",
        ["输入中..."] = "Inserting...",
        ["已暂停"] = "Paused",
        ["错误：{0}"] = "Error: {0}",

        // ── HUD 浮窗 ────────────────────────────────────────
        ["录音中"] = "Recording",
        ["识别中"] = "Recognizing",
        ["已输入"] = "Inserted",
        ["错误"] = "Error",
        ["服务异常"] = "Service error",
        ["已取消"] = "Canceled",
        ["识别提示"] = "Recognition notice",

        // ── 设置窗口：通用 ──────────────────────────────────
        ["{0} 设置"] = "{0} Settings",
        ["识别"] = "Recognition",
        ["热键"] = "Hotkey",
        ["权限"] = "Permissions",
        ["通用"] = "General",
        ["版本 {0}"] = "Version {0}",
        ["保存并应用"] = "Save and Apply",
        ["保存中..."] = "Saving...",
        ["设置已保存并生效。"] = "Settings saved and applied.",
        ["保存失败：{0}"] = "Save failed: {0}",
        ["重新检测"] = "Check Again",
        ["界面语言："] = "Interface language:",
        ["切换后需重启 VoiceTyper 才会全面生效"] = "takes full effect after you restart VoiceTyper",
        ["界面语言已保存。请重启 VoiceTyper 使全部界面文本生效。"] =
            "Interface language saved. Restart VoiceTyper to apply it to all interface text.",
        ["HUD 不透明度："] = "HUD opacity:",
        ["空闲卸载模型："] = "Unload model when idle:",
        ["分钟（0 = 常驻不卸载）"] = "minutes (0 = keep the engine resident)",
        ["预览窗口（进阶）："] = "Preview window (advanced):",
        ["秒（0 = 首次加载后自动按本机性能校准）"] =
            "seconds (0 = calibrated automatically from this machine's performance after the first load)",
        ["开机自启写入失败，其余设置未保存。"] =
            "Could not write the launch-at-login setting; nothing else was saved either.",

        // ── 设置窗口：识别页 ────────────────────────────────
        ["语音模型："] = "Speech model:",
        ["识别语言："] = "Recognition language:",
        ["模型目录：{0}"] = "Model folder: {0}",
        ["取消下载"] = "Cancel Download",
        ["开始下载模型"] = "Download Model",
        ["加载中..."] = "Loading...",
        ["重新加载模型"] = "Reload Model",
        ["重试加载"] = "Retry Loading",
        ["正在下载模型..."] = "Downloading the model...",
        ["需要下载语音模型（约 230 MB）"] = "The speech model needs to be downloaded (about 230 MB)",
        ["模型加载中..."] = "Loading model...",
        ["SenseVoice-Small（int8）· 已就绪"] = "SenseVoice-Small (int8) · ready",
        ["引擎已空闲卸载（下次录音自动重新加载）"] =
            "Engine unloaded after being idle (it reloads automatically on your next recording)",
        ["引擎未加载（空闲卸载后会在下次录音时自动重新加载）"] =
            "Engine not loaded (after an idle unload it reloads automatically on your next recording)",
        ["模型加载失败：{0}"] = "Model failed to load: {0}",
        ["启用智能纠错（LLM）"] = "Enable AI correction (LLM)",
        ["Base URL："] = "Base URL:",
        ["API Key："] = "API key:",
        ["模型："] = "Model:",
        ["温度"] = "Temperature",
        ["最大 Token"] = "Max tokens",
        ["超时(秒)"] = "Timeout (s)",
        ["参数："] = "Parameters:",
        ["测试纠错"] = "Test Correction",
        ["正在测试纠错..."] = "Testing correction...",
        ["纠错测试失败：{0}"] = "Correction test failed: {0}",
        ["纠错测试成功，配置可用。"] = "Correction test succeeded; this configuration works.",
        ["无法读取已保存的 API Key，请重新填写并保存。"] =
            "Could not read the saved API key. Please enter it again and save.",

        // ── 设置窗口：热键页 ────────────────────────────────
        ["修饰键："] = "Modifiers:",
        ["主键："] = "Main key:",
        ["预览："] = "Preview:",
        ["支持的主键示例：a-z、0-9、space、tab、enter、esc、f1-f12、insert、delete、home/end、pageup/pagedown、↑↓←→"] =
            "Supported main keys include: a-z, 0-9, space, tab, enter, esc, f1-f12, insert, delete, home/end, pageup/pagedown, ↑↓←→",
        ["主键不能为空"] = "The main key cannot be empty",
        ["不支持的主键：{0}。可用：字母、数字、F1–F12、Space、Tab、方向键等。"] =
            "Unsupported main key: {0}. Allowed: letters, digits, F1–F12, Space, Tab, arrow keys and the like.",
        ["至少选择一个修饰键（Ctrl/Alt/Shift/Win），否则会拦截普通输入。"] =
            "Select at least one modifier (Ctrl/Alt/Shift/Win); otherwise the hotkey would swallow ordinary typing.",
        ["热键已保存并生效。"] = "Hotkey saved and applied.",

        // ── 设置窗口：权限页 ────────────────────────────────
        ["麦克风："] = "Microphone:",
        ["打开麦克风设置"] = "Open Microphone Settings",
        ["麦克风可用"] = "Microphone available",
        ["麦克风不可用：被系统隐私设置阻止"] = "Microphone unavailable: blocked by system privacy settings",
        ["未检测到麦克风设备"] = "No microphone device detected",
        ["麦克风打开失败：可能被其他应用占用"] = "Could not open the microphone: another app may be using it",
        ["麦克风状态未知"] = "Microphone status unknown",
        ["麦克风权限可能被禁用：请在 Windows 设置 → 隐私和安全 → 麦克风中允许桌面应用访问。"] =
            "Microphone access may be disabled. Allow desktop apps to use the microphone in Windows Settings → Privacy & security → Microphone.",
        ["未检测到麦克风设备：请插入麦克风或在系统声音设置中启用输入设备。"] =
            "No microphone detected. Plug one in, or enable an input device in the system sound settings.",
        ["麦克风设备打开失败：可能被其他应用独占，或驱动异常。"] =
            "The microphone could not be opened: another app may have exclusive use of it, or the driver is failing.",
        ["已知限制：以管理员身份运行的窗口（记事本、终端等）不会响应文本插入，这是 Windows UIPI 安全机制的限制，不是识别故障。识别结果仍会写入剪贴板，可手动粘贴。"] =
            "Known limitation: windows running as administrator (Notepad, terminals, and so on) do not accept injected text. This is the Windows UIPI security mechanism, not a recognition failure. The result is still written to the clipboard so you can paste it manually.",

        // ── 识别语言 ────────────────────────────────────────
        ["自动"] = "Auto",
        ["中文"] = "Chinese",
        ["英文"] = "English",
        ["粤语"] = "Cantonese",
        ["日语"] = "Japanese",
        ["韩语"] = "Korean",

        // ── 听写流程 ────────────────────────────────────────
        ["配置加载失败"] = "Failed to load configuration",
        ["未知错误"] = "Unknown error",
        ["输入设备已变化，本次录音已结束"] = "The input device changed; this recording has ended",
        ["热键监听已失效，正在尝试自动恢复"] = "Hotkey listening stopped working; trying to recover automatically",
        ["上一段听写尚未完成，请稍候再试"] = "The previous dictation is not finished; try again shortly",
        ["上一段听写已完成，结果已复制到剪贴板"] =
            "The previous dictation finished; its result was copied to the clipboard",
        ["上一段听写已完成，但复制到剪贴板失败"] =
            "The previous dictation finished, but copying it to the clipboard failed",
        ["麦克风权限被拒绝，请在 Windows 设置中允许应用访问麦克风"] =
            "Microphone access was denied. Allow apps to use the microphone in Windows Settings.",
        ["开始录音失败"] = "Failed to start recording",
        ["目标窗口已变化，结果已复制到剪贴板，可手动粘贴"] =
            "The target window changed; the result was copied to the clipboard and can be pasted manually",
        ["目标窗口已变化，且复制到剪贴板也失败了，请重新听写"] =
            "The target window changed and copying to the clipboard failed too; please dictate again",
        ["检测到有修饰键按住，未自动粘贴，结果已复制到剪贴板"] =
            "A modifier key was held down, so nothing was pasted automatically; the result is on the clipboard",
        ["检测到有修饰键按住，且复制到剪贴板失败，请重新听写"] =
            "A modifier key was held down and copying to the clipboard failed; please dictate again",
        ["插入失败，且复制到剪贴板也失败了，请重新听写"] =
            "Insertion failed and copying to the clipboard failed too; please dictate again",
        ["目标窗口以管理员身份运行，Windows 安全机制阻止了输入注入，已复制到剪贴板，可手动粘贴"] =
            "The target window runs as administrator and Windows blocked the input injection. The result is on the clipboard and can be pasted manually.",
        ["无法判断目标窗口权限，输入注入未生效，已复制到剪贴板，可手动粘贴"] =
            "The target window's privilege level could not be determined and the input injection had no effect. The result is on the clipboard and can be pasted manually.",
        ["插入失败，已复制到剪贴板，可手动粘贴"] =
            "Insertion failed; the result is on the clipboard and can be pasted manually",
        ["正在录音/识别/输入，请等待当前听写完成后再保存此项设置"] =
            "Recording, recognition or insertion is in progress — wait for the current dictation to finish before saving this setting",
        ["正在录音/识别/输入，请等待当前听写完成后再保存识别设置"] =
            "Recording, recognition or insertion is in progress — wait for the current dictation to finish before saving the recognition settings",
        ["API Key 写入失败，请重试（配置尚未保存）"] =
            "Writing the API key failed; please retry (the configuration was not saved)",
        ["正在听写，请等待当前听写完成后再重新加载模型"] =
            "A dictation is in progress; wait for it to finish before reloading the model",
        ["热键监听失败：{0}"] = "Hotkey listening failed: {0}",

        // ── 引擎 / 下载 / LLM ───────────────────────────────
        ["下载中 {0}%"] = "downloading {0}%",
        ["引擎已就绪"] = "engine ready",
        ["引擎已空闲卸载，下次录音自动加载"] = "engine unloaded after idle, reloads at your next recording",
        ["引擎未加载"] = "engine not loaded",
        ["模型加载失败"] = "Model failed to load",
        ["模型加载失败: {0}"] = "Model failed to load: {0}",
        ["模型下载失败: {0}"] = "Model download failed: {0}",
        ["识别模型缺失，请在设置中下载模型"] = "The speech model is missing; download it in Settings",
        ["文件 {0} 校验失败，可能是下载损坏，请重试。"] =
            "Checksum verification failed for {0}. The download may be corrupted; please retry.",
        ["下载 {0} 失败。"] = "Downloading {0} failed.",
        ["下载 {0} 连接超时（{1:F0}s 未响应），将重试。"] =
            "Downloading {0} timed out while connecting (no response for {1:F0}s); retrying.",
        ["下载 {0} 失败（HTTP {1}）。"] = "Downloading {0} failed (HTTP {1}).",
        ["下载 {0} 停滞（{1:F0}s 无数据），将重试。"] =
            "Downloading {0} stalled ({1:F0}s without data); retrying.",
        ["音频帧长度 {0} 不是 4 的倍数，已丢弃"] = "Audio frame length {0} is not a multiple of 4; it was discarded",
        ["录音已达 {0} 秒上限，自动结束本次听写"] =
            "Reached the {0}-second limit; this dictation ended automatically",
        ["识别超时"] = "Recognition timed out",
        ["识别引擎加载超时，请稍后重试"] = "Loading the speech engine timed out; please try again shortly",
        ["智能纠错未成功，已使用识别原文"] = "AI correction did not succeed; the raw recognition was used",
        ["LLM 请求超时"] = "The LLM request timed out",
        ["LLM 服务连接失败: {0}"] = "Could not connect to the LLM service: {0}",
        ["LLM API 错误 ({0})"] = "LLM API error ({0})",
        ["LLM 响应格式无法解析"] = "The LLM response could not be parsed",
        ["Base URL 格式不合法，请检查协议头（http/https）与地址。"] =
            "The base URL is malformed; check the scheme (http/https) and the address.",
        ["{0} 是公网地址，明文 HTTP 只允许本机或局域网地址，公网请使用 https://。"] =
            "{0} is a public address. Plain HTTP is only allowed for loopback or private networks; use https:// for public hosts.",
        ["麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风"] =
            "Microphone access was denied. Allow apps to use the microphone in Windows Settings.",
        ["未找到可用麦克风设备"] = "No usable microphone device was found",
        ["暂不支持 {0} 声道的输入设备，请在系统声音设置中改用单声道或立体声麦克风"] =
            "Input devices with {0} channels are not supported yet; switch to a mono or stereo microphone in the system sound settings",
        ["启动录音失败: {0}"] = "Failed to start recording: {0}",
        ["不支持的热键主键: {0}"] = "Unsupported hotkey main key: {0}",
        ["安装键盘钩子失败 (Win32 error {0})"] = "Installing the keyboard hook failed (Win32 error {0})",
    };
}
