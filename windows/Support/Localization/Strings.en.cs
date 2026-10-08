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
        // ── 设置窗口与下载诊断 ────────────────────────────────
        ["API Key 已更新，但配置保存失败，请重试保存。"] = "The API key was updated, but saving the configuration failed. Please save again.",
        ["配置保存失败，请检查配置目录的写入权限。"] = "Saving the configuration failed. Check write access to the configuration folder.",
        ["启用智能纠错时必须填写模型名称。"] = "Enter a model name before enabling text correction.",
        ["安全连接失败，请检查系统时间、代理和证书信任。"] = "Secure connection failed. Check the system clock, proxy, and certificate trust.",
        ["按住说话，或按一次开始、再按一次结束。"] = "Hold to speak, or press once to start and again to finish.",
        ["帮助与诊断"] = "Help and diagnostics",
        ["保存后重启应用，使全部界面文本生效。"] = "Restart the app after saving to apply the language throughout the interface.",
        ["不透明度"] = "Opacity",
        ["查看下载详情"] = "Download details",
        ["超时（秒）"] = "Timeout (seconds)",
        ["超时后仍使用原识别文本。"] = "Use the original transcription if the request times out.",
        ["撤销更改"] = "Discard changes",
        ["触发方式"] = "Activation mode",
        ["打开日志目录"] = "Open log folder",
        ["代理连接失败，请检查系统代理设置。"] = "Proxy connection failed. Check the system proxy settings.",
        ["调整浮窗显示和应用启动方式。"] = "Customize the dictation overlay and app startup.",
        ["服务地址"] = "Service URL",
        ["服务器返回了不匹配的下载范围，请重试。"] = "The server returned an unexpected download range. Please retry.",
        ["浮窗位置"] = "Overlay position",
        ["高级设置"] = "Advanced settings",
        ["兼容 OpenAI 的 API 地址。"] = "An OpenAI-compatible API endpoint.",
        ["检查麦克风访问状态，查看故障与使用说明。"] = "Check microphone access, troubleshoot problems, and find help.",
        ["界面语言"] = "Interface language",
        ["可选功能：仅发送识别后的文字，不发送音频。"] = "Optional: sends only recognized text, never audio.",
        ["空闲卸载（分钟）"] = "Unload when idle (minutes)",
        ["快捷键"] = "Shortcut",
        ["麦克风访问"] = "Microphone access",
        ["密钥由 Windows 加密保存。"] = "The key is encrypted by Windows before storage.",
        ["模型保存在本机，准备完成后即可离线听写。"] = "The model is stored on this computer for offline dictation.",
        ["模型名称"] = "Model name",
        ["启动时预加载模型"] = "Preload the model on startup",
        ["请处理上述问题后手动重试。"] = "Address the issues above, then retry manually.",
        ["请录制有效快捷键；组合键至少需要一个修饰键。"] = "Record a valid shortcut; combinations require at least one modifier.",
        ["如果无法向以管理员身份运行的应用输入文字，请让两个应用以相同权限运行。"] = "If text cannot be inserted into an app running as administrator, run both apps with the same privileges.",
        ["设为 0 时保持模型常驻。"] = "Set to 0 to keep the model loaded.",
        ["设为 0 时自动按本机性能校准。"] = "Set to 0 to calibrate automatically for this computer.",
        ["设置"] = "Settings",
        ["设置已保存，但开机自启更新失败，请重试。"] = "Settings were saved, but updating startup registration failed. Please retry.",
        ["识别语言"] = "Recognition language",
        ["手动编辑快捷键"] = "Edit shortcut manually",
        ["数值越低，纠错结果越稳定。"] = "Lower values produce more consistent corrections.",
        ["所有更改已保存"] = "All changes saved",
        ["提前准备识别引擎，减少首次听写的等待。"] = "Prepare the engine ahead of time to reduce the wait for the first dictation.",
        ["填写服务提供方支持的模型名称。"] = "Enter a model name supported by your provider.",
        ["听写"] = "Dictation",
        ["听写浮窗"] = "Dictation overlay",
        ["通常使用自动识别，也可以指定语言。"] = "Auto detection works in most cases; you can also choose a language.",
        ["外观与通用"] = "Appearance and general",
        ["文字纠错"] = "Text correction",
        ["无法解析下载服务器地址，请检查网络和 DNS。"] = "Cannot resolve the download server. Check the network and DNS.",
        ["无法写入模型目录，请检查目录权限。"] = "Cannot write to the model folder. Check folder permissions.",
        ["下载 {0} 失败，请查看各下载源的结果。"] = "Downloading {0} failed. Review the results for each source.",
        ["下载连接中断，请检查网络后重试。"] = "The download connection was interrupted. Check the network and retry.",
        ["下载诊断详情"] = "Download diagnostics",
        ["限制纠错回复的长度。"] = "Limit the length of the correction response.",
        ["修改时立即预览；撤销后恢复。"] = "Preview changes immediately; discard to restore the saved value.",
        ["选择输入方式，按下快捷键即可开始说话。"] = "Choose your input preferences and press the shortcut to start speaking.",
        ["选择听写反馈在屏幕上的位置。"] = "Choose where dictation feedback appears on screen.",
        ["已下载 {0:F1} / {1:F1} MB · {2}%"] = "Downloaded {0:F1} / {1:F1} MB · {2}%",
        ["应用"] = "Application",
        ["有未保存的更改"] = "Unsaved changes",
        ["语音输入"] = "Voice input",
        ["预览窗口（秒）"] = "Preview window (seconds)",
        ["运行方式"] = "Model lifecycle",
        ["诊断与帮助"] = "Diagnostics and help",
        ["智能纠错"] = "Text correction",
        ["主键"] = "Primary key",
        ["字母、数字、F1–F12、右 Ctrl 或右 Alt。"] = "Letters, digits, F1–F12, Right Ctrl, or Right Alt.",
        ["自动模式优先避免蓝牙通话模式影响声音。"] = "Auto mode avoids Bluetooth call mode when possible.",
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
        ["离线、安全、快捷的语音输入工具。"] = "An offline, secure, and fast voice input tool.",
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
        ["版本 {0}"] = "Version {0}",
        ["保存并应用"] = "Save and Apply",
        ["保存中..."] = "Saving...",
        ["设置已保存并生效。"] = "Settings saved and applied.",
        ["保存失败：{0}"] = "Save failed: {0}",
        ["重新检测"] = "Check Again",
        ["界面语言已保存。请重启 VoiceTyper 使全部界面文本生效。"] =
            "Interface language saved. Restart VoiceTyper to apply it to all interface text.",

        // ── 设置窗口：识别页 ────────────────────────────────
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
        ["温度"] = "Temperature",
        ["最大 Token"] = "Max tokens",
        ["测试纠错"] = "Test Correction",
        ["正在测试纠错..."] = "Testing correction...",
        ["纠错测试失败：{0}"] = "Correction test failed: {0}",
        ["纠错测试成功，配置可用。"] = "Correction test succeeded; this configuration works.",
        ["无法读取已保存的 API Key，请重新填写并保存。"] =
            "Could not read the saved API key. Please enter it again and save.",

        // ── 设置窗口：热键页 ────────────────────────────────
        ["至少选择一个修饰键（Ctrl/Alt/Shift/Win），否则会拦截普通输入。"] =
            "Select at least one modifier (Ctrl/Alt/Shift/Win); otherwise the hotkey would swallow ordinary typing.",

        // ── 设置窗口：权限页 ────────────────────────────────
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
            ["没有可用的模型下载地址：{0}"] = "No model download address is available for {0}.",
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
        ["已使用识别原文（纠错未成功）"] = "Used the raw recognition (correction did not succeed)",
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
        // ── 首启引导 ────────────────────────────────────────
        ["欢迎使用 {0}"] = "Welcome to {0}",
        ["上一步"] = "Back",
        ["下一步"] = "Next",
        ["先跳过"] = "Skip for now",
        ["完成"] = "Done",
        ["第 {0} 步，共 {1} 步"] = "Step {0} of {1}",
        ["欢迎"] = "Welcome",
        ["麦克风"] = "Microphone",
        ["语音模型"] = "Speech Model",
        ["试一试"] = "Try It",
        ["按住 {0} 说话，松开后识别结果会出现在光标所在的位置。"] =
            "Hold {0} and speak; when you let go, the recognized text appears at the cursor.",
        ["按一次 {0} 开始说话，再按一次结束，识别结果会出现在光标所在的位置。"] =
            "Press {0} once to start speaking and again to stop; the recognized text appears at the cursor.",
        ["VoiceTyper 是一个离线语音输入工具：识别完全在这台电脑上完成，音频不会上传。"] =
            "VoiceTyper is an offline voice input tool: recognition happens entirely on this computer and audio is never uploaded.",
        ["接下来用两分钟确认麦克风、语音模型都准备好，并亲自试说一句话。"] =
            "Next, take two minutes to confirm the microphone and speech model are ready, and try speaking a sentence yourself.",
        ["引导之后也可以从托盘菜单的「使用引导...」再次打开。"] =
            "You can reopen this guide later from \"Setup Guide...\" in the tray menu.",
        ["麦克风已打开，但只收到静音"] = "The microphone opened, but only silence was received",
        ["请确认 Windows 设置 → 隐私和安全性 → 麦克风中「让桌面应用访问你的麦克风」已打开，并且麦克风没有被静音（包括键盘 / 耳机上的静音键）。也可能只是当前环境太安静——可以直接进入下一步试说一句话。"] =
            "Make sure \"Let desktop apps access your microphone\" is on under Windows Settings → Privacy & security → Microphone, and that the microphone is not muted (including mute keys on the keyboard or headset). It may also just be very quiet here — you can continue and try speaking.",
        ["麦克风已打开但只收到静音：请确认「让桌面应用访问你的麦克风」已打开，且麦克风没有被静音。"] =
            "The microphone opened but only silence was received. Make sure \"Let desktop apps access your microphone\" is on and that the microphone is not muted.",
        ["正在检测麦克风..."] = "Checking the microphone...",
        ["Windows 没有单独的授权弹窗；能打开麦克风就说明桌面应用被允许访问了。"] =
            "Windows has no separate permission prompt; if the microphone opens, desktop apps are allowed to use it.",
        ["请在 Windows 设置 → 隐私和安全性 → 麦克风中，打开「麦克风访问」和「让桌面应用访问你的麦克风」，然后点「重新检测」。"] =
            "In Windows Settings → Privacy & security → Microphone, turn on \"Microphone access\" and \"Let desktop apps access your microphone\", then click \"Check Again\".",
        ["请插入麦克风，或在 Windows 设置 → 系统 → 声音 → 输入中启用一个输入设备，然后点「重新检测」。"] =
            "Plug in a microphone, or enable an input device in Windows Settings → System → Sound → Input, then click \"Check Again\".",
        ["请关闭正在独占麦克风的应用（会议软件、录音软件等），然后点「重新检测」。"] =
            "Close apps that are holding the microphone exclusively (conferencing or recording software), then click \"Check Again\".",
        ["正在下载模型 {0}%"] = "Downloading model {0}%",
        ["模型约 230 MB，只需下载一次，之后完全离线使用。可以先点「下一步」，下载在后台继续。"] =
            "The model is about 230 MB and only needs to be downloaded once; afterwards everything works offline. You can click \"Next\" now and the download continues in the background.",
        ["语音模型已就绪"] = "Speech model is ready",
        ["可以到设置 → 语音模型中重新加载，或重新下载模型。"] = "Reload it in Settings → Speech Model, or download the model again.",
        ["重试下载"] = "Retry Download",
        ["点一下下面的输入框，然后试说一句话："] = "Click the box below, then try speaking a sentence:",
        ["麦克风还不可用，请回到上一步处理。"] = "The microphone is not available yet. Go back one step to fix it.",
        ["语音模型还没准备好，请等待或回到上一步重试下载。"] = "The speech model is not ready yet. Wait, or go back one step and retry the download.",
        ["采到了声音，但没有识别出文字。请靠近麦克风、放慢一点再试一次。"] =
            "Sound was captured but no text was recognized. Move closer to the microphone, speak a little slower, and try again.",
        ["正在录音…"] = "Recording…",
        ["说一句话，然后{0}。"] = "Say a sentence, then {0}.",
        ["松开热键"] = "release the hotkey",
        ["再按一次热键"] = "press the hotkey again",
        ["识别中…"] = "Recognizing…",
        ["本地引擎正在处理这段音频。"] = "The local engine is processing this audio.",
        ["全部跑通了"] = "The whole chain works",
        ["已插入：{0}"] = "Inserted: {0}",
        ["没有采到声音"] = "No audio captured",
        ["热键与识别链路是通的，但这段录音几乎是静音。请检查麦克风是否被静音、「设置 → 系统 → 声音 → 输入」里选中的设备是否正确，然后再试一次。"] =
            "The hotkey and recognition chain work, but this recording was almost silent. Check that the microphone is not muted and that the right device is selected under Settings → System → Sound → Input, then try again.",
        ["按 Esc 可以随时取消，识别结果不会被插入。再试一次吧。"] =
            "Press Esc at any time to cancel; the result will not be inserted. Give it another try.",
        ["没有成功"] = "Did not work",
        ["还不能开始"] = "Cannot start yet",

        // ── 热键 / 通用设置新增项 ───────────────────────────
        ["使用右 Ctrl"] = "Use Right Ctrl",
        ["使用右 Alt"] = "Use Right Alt",
        ["录制热键"] = "Record Hotkey",
        ["停止录制"] = "Stop Recording",
        ["正在听写，请等这一段结束后再录制热键。"] = "A dictation is in progress; wait for it to finish before recording a hotkey.",
        ["请按下要使用的快捷键（Esc 取消）……"] = "Press the shortcut you want to use (Esc to cancel)...",
        ["已取消录制。"] = "Recording canceled.",
        ["这个键不能用作热键，请换一个（字母、数字、F1–F12、方向键等）。"] = "That key can't be used as a hotkey. Try another (letters, digits, F1–F12, arrow keys, ...).",
        ["已录制：{0}，点「保存并应用」生效。"] = "Recorded: {0}. Click \"Save and Apply\" to use it.",
        ["右 Ctrl"] = "Right Ctrl",
        ["右 Alt"] = "Right Alt",
        ["按住说话"] = "Hold to talk",
        ["按一次开始，再按一次结束"] = "Press once to start, again to stop",
        ["底部居中"] = "Bottom center",
        ["右下角"] = "Bottom right",
        ["跟随光标"] = "Follow cursor",
        ["不显示（仅出错时提示）"] = "Hidden (errors only)",
        ["自动（戴蓝牙耳机时改用内置麦克风）"] = "Automatic (use the built-in microphone when a Bluetooth headset is in use)",
        ["跟随系统默认输入"] = "Follow the system default input",
        ["（已保存的设备，当前未连接）"] = "(saved device, not connected)",

        // ── 托盘菜单 / 更新检查 ─────────────────────────────
        ["使用引导..."] = "Setup Guide...",
        ["检查更新..."] = "Check for Updates...",
        ["已是最新版本"] = "You are up to date",
        ["当前版本 {0}。"] = "Current version {0}.",
        ["有新版本可用"] = "A new version is available",
        ["最新版本 {0}，当前版本 {1}。"] = "Latest version {0}; you have {1}.",
        ["是否打开发布页？"] = "Open the releases page?",
        ["无法比较版本号"] = "Could not compare version numbers",
        ["已获取到最新的发布信息，但无法解析版本号。请自行到发布页确认。"] =
            "Fetched the latest release info but could not parse its version number. Please check the releases page yourself.",
        ["无法检查更新"] = "Could not check for updates",
        ["{0}\n可以稍后重试，或直接到 GitHub 发布页查看。"] =
            "{0}\nTry again later, or check the GitHub releases page directly.",
        ["GitHub 返回 HTTP {0}"] = "GitHub returned HTTP {0}",
        ["无法解析 GitHub 的响应"] = "Could not parse GitHub's response",

        // ── HUD 新增状态 ────────────────────────────────────
        ["纠错中…"] = "Correcting…",
        ["没有识别到内容"] = "Nothing recognized",
        ["没听到说话内容，请确认麦克风未静音、输入设备选择正确。"] =
            "No speech was heard. Make sure the microphone is not muted and the right input device is selected.",
        ["没有检测到声音，请检查麦克风与输入设备"] = "No sound detected — check the microphone and input device",
        ["还不能听写"] = "Cannot dictate yet",

        // ── 就绪门禁 / 下载重试 ─────────────────────────────
        ["语音模型正在下载（{0}%），完成后即可开始听写。"] =
            "The speech model is downloading ({0}%). You can dictate once it finishes.",
        ["语音模型还没有下载，无法开始听写。"] = "The speech model has not been downloaded yet, so dictation is unavailable.",
        ["语音模型加载失败：{0}"] = "Failed to load the speech model: {0}",
        ["识别引擎正在加载，请稍候再试。"] = "The speech engine is loading; please try again in a moment.",
        ["还有准备工作没有完成。"] = "Setup is not finished yet.",
        ["{0} 将在 {1} 秒后自动重试（第 {2}/{3} 次）。"] = "{0} Retrying automatically in {1} s (attempt {2} of {3}).",
        ["{0} 已自动重试 {1} 次仍未成功，请检查网络后手动重试。"] =
            "{0} Still failing after {1} automatic retries; check the network and retry manually.",

        ["下载 {0} 失败（连接提前结束）。"] = "Downloading {0} failed (the connection ended early).",

        // ── LLM 探测 ────────────────────────────────────────
        ["该服务不支持关闭深度思考的参数，已自动改为不带该参数请求。"] =
            "This service does not accept the parameter that turns off deep thinking; requests now omit it automatically.",
        ["已带上关闭深度思考的参数（服务接受）。"] = "The parameter that turns off deep thinking was sent (accepted by the service).",
    };
}
