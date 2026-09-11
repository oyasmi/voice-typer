import Foundation

/// 英文文案表。键是中文原文（见 `L10n`），值是对等的英文表述。
///
/// 要求与中文一样"把话说清楚"：不做字面直译，也不因为是第二语言就把长句砍成短提示——
/// 中英双语同等程度支持是这张表存在的前提。
///
/// 新增 `L(...)` / `LF(...)` 调用后必须在这里补一条，否则 `LocalizationTests` 会失败。
/// 占位符（`%@` / `%d` / `%.2f`）的个数与顺序必须与中文一致。
extension L10n {
    static let englishTable: [String: String] = [
        // MARK: - 菜单栏 / 主菜单
        "暂停听写": "Pause Dictation",
        "恢复听写": "Resume Dictation",
        "权限与设置…": "Permissions & Settings…",
        "使用引导…": "Setup Guide…",
        "检查更新…": "Check for Updates…",
        "开机自启": "Launch at Login",
        "关于 %@": "About %@",
        "退出": "Quit",
        "退出 %@": "Quit %@",
        "编辑": "Edit",
        "撤销": "Undo",
        "重做": "Redo",
        "剪切": "Cut",
        "拷贝": "Copy",
        "粘贴": "Paste",
        "全选": "Select All",
        "无法更改开机自启设置": "Could not change the launch-at-login setting",
        "本地优先的离线语音输入工具": "A local-first, offline voice input tool",
        "音频仅在设备端处理": "Audio is processed entirely on this device",
        "GitHub 项目主页 ↗": "GitHub project page ↗",

        // MARK: - 应用状态
        "启动中": "Starting up",
        "需要完成授权与设置": "Permissions and setup required",
        "需要下载语音模型": "Speech model needs to be downloaded",
        "下载模型 %d%%": "Downloading model %d%%",
        "模型加载中…": "Loading model…",
        "就绪": "Ready",
        "录音中...": "Recording...",
        "识别中...": "Recognizing...",
        "输入中...": "Inserting...",
        "已暂停": "Paused",
        "错误: %@": "Error: %@",

        // MARK: - HUD 浮窗
        "录音中": "Recording",
        "录音中 · %@": "Recording · %@",
        "识别中": "Recognizing",
        "识别中…": "Recognizing…",
        "校对中…": "Proofreading…",
        "已输入": "Inserted",
        "错误": "Error",
        "服务异常": "Service error",
        "没有识别到内容": "Nothing recognized",
        "没听到说话内容，请确认麦克风未静音、输入设备选择正确。":
            "No speech was heard. Check that your microphone is not muted and that the right input device is selected.",
        "已取消": "Canceled",
        "识别提示": "Recognition notice",
        "背景预览": "Background preview",
        "正在聆听…": "Listening…",

        // MARK: - 设置窗口：分页与通用
        "权限": "Permissions",
        "识别": "Recognition",
        "通用": "General",
        "%@ 设置": "%@ Settings",
        "内部错误：测试通道不可用": "Internal error: the test channel is unavailable",
        "保存并应用": "Save and Apply",
        "设置已保存并生效。": "Settings saved and applied.",
        "保存失败：%@": "Save failed: %@",
        "取消": "Cancel",
        "重试": "Retry",
        "重新检测": "Check Again",
        "界面语言": "Interface Language",
        "切换后请重启 VoiceTyper，界面文本才会全部改用新语言。":
            "After switching, restart VoiceTyper so that every piece of interface text uses the new language.",
        "界面语言切换后需要重启 VoiceTyper 才会全面生效。识别语言在「识别」页单独设置。":
            "A new interface language takes full effect after you restart VoiceTyper. The recognition language is set separately on the Recognition tab.",
        "界面语言已保存。请重启 VoiceTyper 使全部界面文本生效。":
            "Interface language saved. Restart VoiceTyper to apply it to all interface text.",
        "界面语言保存失败：%@": "Could not save the interface language: %@",
        "位置": "Position",
        "「跟随光标」把浮窗放在鼠标下方；「不显示」只隐藏录音与预览，出错时仍会提示。":
            "“Follow cursor” places the overlay below the pointer. “Hidden” only hides recording and preview — errors are still shown.",
        "HUD 背景不透明度": "HUD background opacity",
        "悬浮窗": "Overlay",
        "已关闭浮窗。录音、实时预览与「已输入」确认都不再显示；识别失败、没有采到声音这类提示仍会浮出来——那些信息在别处看不到。":
            "The overlay is off. Recording, live preview and the “Inserted” confirmation are no longer shown; failures and “no audio captured” notices still appear, because that information is not available anywhere else.",
        "拖动不透明度时会临时预览浮窗效果，松手后保存生效。":
            "Dragging the opacity slider previews the overlay live; the value is saved when you release it.",

        // MARK: - 设置窗口：权限页
        "授权": "Grant",
        "系统设置": "System Settings",
        "权限检查通过": "Permission check passed",
        "仍需完成授权": "Permissions still missing",
        "权限已就绪；识别引擎的状态请查看「识别」页。":
            "Permissions are ready. See the Recognition tab for the state of the speech engine.",
        "请先完成上方未授权项，处理完成后本窗口会自动更新状态。":
            "Grant the missing permissions above; this window updates itself once you do.",
        "关于更新后重新授权": "Re-granting permissions after an update",
        "当前这份 VoiceTyper 使用本机（ad-hoc）签名，没有稳定的开发者签名标识。系统的权限授权记录以代码签名为键，因此更新到新版本后，上面三项权限可能需要重新授权一次。这是未签名分发的固有限制，不是出了故障。":
            "This build of VoiceTyper is ad-hoc signed on your own machine and has no stable developer signing identity. macOS keys its permission records by code signature, so after updating to a new version you may have to grant the three permissions above again. This is inherent to unsigned distribution, not a malfunction.",
        "麦克风": "Microphone",
        "辅助功能": "Accessibility",
        "输入监控": "Input Monitoring",
        "用于录音": "used for recording",
        "用于插入文本": "used for inserting text",
        "用于监听全局热键（尤其是 Fn 键）": "used for the global hotkey (especially the Fn key)",
        "已授权": "granted",
        "未授权": "denied",
        "待请求": "not requested yet",
        "请在系统设置中允许“输入监控”": "Allow “Input Monitoring” in System Settings",
        "macOS 通常不会直接弹出“输入监控”的授权窗口。点击“打开系统设置”后，请在“隐私与安全性 > 输入监控”中启用 VoiceTyper，然后返回本窗口。":
            "macOS usually does not prompt for “Input Monitoring” on its own. Click “Open System Settings”, enable VoiceTyper under Privacy & Security > Input Monitoring, then come back to this window.",
        "打开系统设置": "Open System Settings",

        // MARK: - 设置窗口：识别页
        "空闲多久后卸载模型": "Unload model after idle time",
        "5 分钟": "5 minutes",
        "10 分钟": "10 minutes",
        "30 分钟": "30 minutes",
        "从不": "Never",
        "启动时预加载模型": "Preload model at launch",
        "关闭时首次按热键才加载模型，加载与录音并行，约 1 秒；开机自启的场景下能省下常驻内存。":
            "When off, the model loads on the first hotkey press, in parallel with recording (about one second). Useful for saving resident memory when VoiceTyper starts at login.",
        "识别语言": "Recognition language",
        "SenseVoice 支持自动判断语种，也可指定为固定语言以提升准确率。":
            "SenseVoice can detect the language automatically, or you can fix it to one language for better accuracy.",
        "语音模型": "Speech model",
        "启用智能校对": "Enable AI proofreading",
        "模型": "Model",
        "温度": "Temperature",
        "超时（秒）": "Timeout (seconds)",
        "测试校对": "Test Proofreading",
        "智能校对": "AI proofreading",
        "用 OpenAI 兼容接口对识别结果做二次校对（修正同音错字、口语填充词等）。留空 Base URL 则不启用。":
            "Runs the recognized text through an OpenAI-compatible endpoint to fix homophone errors, filler words and the like. Leave the base URL empty to keep it off.",
        "SenseVoice-Small · int8 · 已就绪": "SenseVoice-Small · int8 · ready",
        "离线识别引擎已加载，可直接使用。": "The offline speech engine is loaded and ready to use.",
        "重新加载": "Reload",
        "模型已就绪，引擎未常驻内存": "Model ready, engine not resident in memory",
        "下次录音会自动加载（约 1 秒），无需手动操作。":
            "It loads automatically on your next recording (about one second); nothing to do by hand.",
        "语音模型尚未就绪": "Speech model not ready yet",
        "正在准备语音模型": "Preparing the speech model",
        "立即下载": "Download Now",
        "重试下载": "Retry Download",
        "正在检查本地语音模型…": "Checking for a local speech model…",
        "模型加载失败": "Model failed to load",
        "下载中 %d%%": "Downloading %d%%",
        "自动下载失败：%@": "Automatic download failed: %@",
        "SenseVoice-Small · 约 230 MB，完成后会自动校验、加载并进入离线可用状态。":
            "SenseVoice-Small · about 230 MB. It is verified and loaded automatically, after which everything works offline.",
        "SenseVoice-Small · 约 230 MB，下载并校验完成后可完全离线使用。":
            "SenseVoice-Small · about 230 MB. Once downloaded and verified, it works fully offline.",
        "无法从 Keychain 读取已保存的 API Key，请重新填写并保存。":
            "Could not read the saved API key from the Keychain. Please enter it again and save.",
        "请先启用智能校对并填写 Base URL。": "Enable AI proofreading and fill in the base URL first.",
        "正在测试校对…": "Testing proofreading…",
        "校对测试成功：模型正常响应（耗时 %.2f 秒）。返回结果：%@":
            "Proofreading test succeeded: the model responded normally (%.2f s). Result: %@",
        "校对测试失败：%@（耗时 %.2f 秒）。": "Proofreading test failed: %@ (%.2f s).",
        "%@设置未保存。": "%@ Settings were not saved.",
        "正在保存并应用设置…": "Saving and applying settings…",
        "API Key 写入 Keychain 失败，设置未保存。":
            "Writing the API key to the Keychain failed; settings were not saved.",
        "启动预加载设置保存失败：%@": "Could not save the preload-at-launch setting: %@",
        "空闲卸载时长保存失败：%@": "Could not save the idle-unload interval: %@",
        "HUD 透明度保存失败：%@": "Could not save the HUD opacity: %@",
        "浮窗位置保存失败：%@": "Could not save the overlay position: %@",
        "检查中": "checking",

        // MARK: - 设置窗口：热键
        "快捷键": "Shortcut",
        "使用 Fn🌐（推荐）": "Use Fn🌐 (recommended)",
        "触发方式": "Trigger mode",
        "长段口述（写邮件、写文档）时不必一直按住热键，可以改成按一次开始、再按一次结束。":
            "For long dictation (emails, documents) you do not have to hold the hotkey down — switch to press once to start, press again to stop.",
        "热键": "Hotkey",
        "点击右侧输入框后按下想要的快捷键即可捕获。推荐使用 Fn；也可设为组合键，如 ⌃⌥Space。录制期间全局热键会临时暂停。无论哪种触发方式，录音中和识别中都可以按 Esc 取消。":
            "Click the field on the right and press the shortcut you want. Fn is recommended, but a combination such as ⌃⌥Space works too. The global hotkey is suspended while recording a shortcut. In either trigger mode you can press Esc to cancel during recording and recognition.",
        "打开键盘设置": "Open Keyboard Settings",
        "已改好，重新检测": "I changed it, check again",
        "不支持该键，请重试": "That key is not supported, try another",
        "请至少同时按住一个修饰键（⌘/⌃/⌥/⇧）": "Hold at least one modifier key (⌘/⌃/⌥/⇧)",
        "按下快捷键…": "Press a shortcut…",
        "不支持的主键 %@。可用：字母 a–z、数字 0–9、space、tab、enter、F1–F12。":
            "Unsupported main key %@. Allowed: letters a–z, digits 0–9, space, tab, enter, F1–F12.",
        "%@ 必须搭配至少一个修饰键（⌘/⌃/⌥/⇧），否则会在任何应用里把这个键变成录音热键。":
            "%@ needs at least one modifier (⌘/⌃/⌥/⇧); otherwise that key would start recording in every app.",
        "热键已更新为 %@。": "Hotkey changed to %@.",
        "触发方式已改为「%@」。": "Trigger mode changed to “%@”.",
        "正在听写中，请稍后再录制热键。": "A dictation is in progress; record the hotkey again afterwards.",
        "正在录制：按下想要的快捷键（Esc 取消，Delete 恢复默认）。":
            "Recording: press the shortcut you want (Esc to cancel, Delete to restore the default).",
        "按住说话": "Hold to talk",
        "按一次开始，再按一次结束": "Press once to start, again to stop",
        "不执行任何操作": "Do Nothing",
        "更改输入法": "Change Input Source",
        "显示表情与符号": "Show Emoji & Symbols",
        "开始听写": "Start Dictation",
        "未知设置（%d）": "unknown setting (%d)",
        "系统默认": "system default",
        "当前为系统默认设置": "currently the system default",
        "当前是「%@」": "currently “%@”",
        "「系统设置 → 键盘 → 按下🌐键」建议设为「不执行任何操作」（%@）。否则按住 Fn 说话时，系统自己的 Fn 功能也会同时触发——松手的一瞬间可能弹出表情面板或切走输入法。":
            "System Settings → Keyboard → “Press 🌐 key to” should be set to “Do Nothing” (%@). Otherwise, holding Fn to speak also triggers the system's own Fn action — the moment you let go, the emoji panel may pop up or your input source may switch.",

        // MARK: - 识别语言 / HUD 位置
        "自动": "Auto",
        "中文": "Chinese",
        "英文": "English",
        "粤语": "Cantonese",
        "日语": "Japanese",
        "韩语": "Korean",
        "底部居中": "Bottom center",
        "右下角": "Bottom right",
        "跟随光标": "Follow cursor",
        "不显示（仅出错时提示）": "Hidden (errors only)",

        // MARK: - 使用引导
        "%@ 使用引导": "%@ Setup Guide",
        "第 %d 步 / 共 %d 步": "Step %d of %d",
        "上一步": "Back",
        "下一步": "Next",
        "完成": "Done",
        "先跳过": "Skip for now",
        "欢迎": "Welcome",
        "系统权限": "System permissions",
        "试一试": "Try it",
        "按住 %@ 说话，松开就上屏": "Hold %@ to speak; release and the text appears",
        "识别完全在这台 Mac 上完成，音频不会离开设备，也不需要联网。":
            "Recognition happens entirely on this Mac. Audio never leaves the device, and no internet connection is required.",
        "按住热键": "Hold the hotkey",
        "默认是 %@。之后可以在「设置 → 通用」里换成组合键，或改成「按一次开始、再按一次结束」，方便长段口述。":
            "The default is %@. In Settings → General you can change it to a key combination, or switch to press once to start and again to stop, which suits long dictation.",
        "看到浮窗再开口": "Wait for the overlay before speaking",
        "按下热键后麦克风还需要约 0.2 秒才真正开始出声。等屏幕下方的浮窗出现再说话，开头的字就不会被吞掉。":
            "After you press the hotkey the microphone needs about 0.2 seconds to actually start. Wait for the overlay near the bottom of the screen, and your first words will not be cut off.",
        "说错了就按 Esc": "Press Esc if you misspoke",
        "录音中和识别中都可以按 Esc 取消，这次的结果不会被插入到任何地方。":
            "You can press Esc during recording and during recognition; nothing from that attempt gets inserted anywhere.",
        "先改一个系统设置，否则按 Fn 会同时触发系统功能":
            "Change one system setting first, or pressing Fn will also trigger a system action",
        "关于以后更新": "About future updates",
        "这份 VoiceTyper 使用本机（ad-hoc）签名。系统的权限记录以代码签名为键，所以更新到新版本后，下一步的三项权限可能需要重新授权一次。这是未签名分发的固有限制。":
            "This build of VoiceTyper is ad-hoc signed on your own machine. macOS keys permission records by code signature, so after an update you may need to grant the three permissions in the next step again. This is inherent to unsigned distribution.",
        "已完成 %d / %d 项": "%d of %d granted",
        "三项缺一不可。点「授权」，系统会弹出授权窗口或跳到对应设置页；「输入监控」通常不会自动弹窗，需要在系统设置里手动打开开关。":
            "All three are required. Click “Grant” and macOS either prompts you or opens the matching settings page; “Input Monitoring” usually does not prompt, so you have to switch it on manually in System Settings.",
        "权限已齐全": "All permissions granted",
        "授权完成后本页会自动更新；也可以手动重新检测。":
            "This page updates itself once you grant them; you can also check again manually.",
        "SenseVoice-Small，约 230 MB，只需下载这一次。下载与上一步的授权是并行的，不必等它结束再去授权。完成后 VoiceTyper 完全离线可用。":
            "SenseVoice-Small, about 230 MB, downloaded only once. The download runs in parallel with the previous step, so there is no need to wait before granting permissions. Afterwards VoiceTyper works fully offline.",
        "模型已就绪": "Model ready",
        "离线识别引擎可以直接使用。": "The offline speech engine is ready to use.",
        "正在下载 %d%%": "Downloading %d%%",
        "支持断点续传，取消后可以随时继续。": "The download resumes where it left off, so you can cancel and continue any time.",
        "下载失败": "Download failed",
        "尚未下载": "Not downloaded yet",
        "点右侧按钮开始下载。": "Use the button on the right to start the download.",
        "开始下载": "Start Download",
        "正在准备…": "Preparing…",
        "正在检查本地模型并加载识别引擎。": "Checking for a local model and loading the speech engine.",
        "在下面的输入框里试一次": "Try it once in the field below",
        "把光标放进输入框，按住 %@ 说一句话，然后松开。":
            "Place the cursor in the field, hold %@, say a sentence, then release.",
        "把光标放进输入框，按一下 %@ 开始说话，说完再按一下结束。":
            "Place the cursor in the field, press %@ once to start speaking, and press it again when you are done.",
        "这一步会真正跑一遍「热键 → 麦克风 → 本地识别 → 文本插入」，任何一环有问题都会在下面直接指出来。":
            "This step really runs the whole chain — hotkey → microphone → local recognition → text insertion — and points out below exactly which link fails.",
        "识别结果会出现在这里": "The recognized text will appear here",
        "还不能试": "Not ready to try yet",
        "还有系统权限没有授予，请回到上一步完成授权。":
            "Some system permissions are still missing; go back a step and grant them.",
        "语音模型还没准备好，请等待或回到上一步重试下载。":
            "The speech model is not ready yet; wait, or go back a step and retry the download.",
        "按下热键完全没反应？": "Nothing happens when you press the hotkey?",
        "「输入监控」权限没给全——回到上一步确认三项都是绿色。":
            "Input Monitoring is not fully granted — go back a step and check that all three are green.",
        "热键被别的应用抢走了（如 Spotlight ⌘Space、输入法切换 ⌃Space）；可以在「设置 → 通用」里换一个热键。":
            "Another app has taken the hotkey (Spotlight ⌘Space, input-source switching ⌃Space, and so on); pick a different hotkey in Settings → General.",
        "刚更新过版本：本机签名的构建在更新后可能需要重新授权，先到「系统设置 → 隐私与安全性」把 VoiceTyper 移除再重新添加。":
            "You just updated: ad-hoc signed builds may need permissions again after an update. In System Settings → Privacy & Security, remove VoiceTyper and add it back.",
        "再试一次": "Try Again",
        "采到了声音，但没有识别出文字。请靠近麦克风、放慢一点再试一次。":
            "Audio was captured but no text came out. Move closer to the microphone, speak a little slower, and try again.",
        "正在录音…": "Recording…",
        "说一句话，然后%@。": "Say a sentence, then %@.",
        "松开热键": "release the hotkey",
        "再按一次热键": "press the hotkey again",
        "本地引擎正在处理这段音频。": "The local engine is processing this audio.",
        "全部跑通了": "The whole chain works",
        "已插入：%@": "Inserted: %@",
        "没有采到声音": "No audio captured",
        "热键与识别链路是通的，但这段录音几乎是静音。请检查麦克风是否被静音、「系统设置 → 声音 → 输入」里选中的设备是否正确，然后再试一次。":
            "The hotkey and the recognition chain work, but this recording is almost silent. Check whether the microphone is muted and whether the right device is selected in System Settings → Sound → Input, then try again.",
        "按 Esc 可以随时取消，识别结果不会被插入。再试一次吧。":
            "You can press Esc at any time to cancel; nothing gets inserted. Give it another try.",
        "没有成功": "Did not work",
        "还不能开始": "Cannot start yet",

        // MARK: - 听写流程 / 协调器
        "配置加载失败": "Failed to load configuration",
        "配置文件解析失败，请检查 %@": "Could not parse the configuration file; please check %@",
        "应用未就绪": "The app is not ready",
        "上一段听写尚未完成": "The previous dictation is not finished",
        "输入设备已变化，本次录音已结束": "The input device changed; this recording has ended",
        "开始录音失败": "Failed to start recording",
        "没有检测到声音，请检查麦克风与输入设备": "No sound detected — check the microphone and input device",
        "目标窗口已变化，结果已复制到剪贴板": "The target window changed; the result was copied to the clipboard",
        "插入失败，已复制到剪贴板，可手动粘贴":
            "Insertion failed; the result is on the clipboard and can be pasted manually",
        "语音模型还没准备好，无法开始听写。": "The speech model is not ready, so dictation cannot start.",
        "语音模型正在下载（%d%%），完成后即可开始听写。":
            "The speech model is downloading (%d%%); dictation becomes available once it finishes.",
        "识别引擎正在加载，请稍候再试。": "The speech engine is loading; please try again in a moment.",
        "还有准备工作没有完成。": "Some setup is still incomplete.",
        "还缺「%@」权限，暂时无法听写。": "The “%@” permission is still missing, so dictation is unavailable.",
        "热键监听失败: %@": "Hotkey listening failed: %@",
        "模型加载失败: %@": "Model failed to load: %@",
        "模型下载失败: %@": "Model download failed: %@",
        "%@将在 %d 秒后自动重试（第 %d/%d 次）。": "%@Retrying automatically in %d s (attempt %d of %d).",
        "%@已自动重试 %d 次仍未成功，请检查网络后手动重试。":
            "%@Automatic retries failed %d times. Check your network, then retry manually.",
        "Base URL 无法解析为合法请求地址": "The base URL cannot be resolved into a valid request address",
        "无法检查更新": "Could not check for updates",
        "%@\n可以稍后重试，或直接到 GitHub 发布页查看。":
            "%@\nTry again later, or check the GitHub releases page directly.",
        "已是最新版本": "You are up to date",
        "当前版本 %@。": "Current version %@.",
        "有新版本可用": "A new version is available",
        "最新版本 %@，当前版本 %@。": "Latest version %@, current version %@.",
        "打开发布页": "Open Releases Page",
        "无法比较版本号": "Could not compare version numbers",
        "已获取到最新的发布信息，但无法解析版本号。请自行到发布页确认。":
            "The latest release information was fetched, but its version number could not be parsed. Please check the releases page yourself.",
        "打开": "Open",
        "好": "OK",
        "正在录音/识别/输入，请等待当前听写完成后再保存此项设置":
            "Recording, recognition or insertion is in progress — wait for the current dictation to finish before saving this setting",
        "模型下载中 %d%%": "downloading model %d%%",
        "模型下载失败：%@": "model download failed: %@",
        "引擎已就绪": "engine ready",
        "引擎按需加载，下次录音自动就绪": "engine loads on demand, ready at your next recording",
        "引擎未加载": "engine not loaded",

        // MARK: - 识别引擎 / 下载 / LLM
        "文件 %@ 校验失败，可能是下载损坏，请重试。":
            "Checksum verification failed for %@. The download may be corrupted; please retry.",
        "下载 %@ 失败（HTTP %d）。": "Downloading %@ failed (HTTP %d).",
        "下载已取消。": "Download canceled.",
        "录音已达 %d 秒上限，自动结束本次听写": "Reached the %d-second limit; this dictation ended automatically",
        "识别超时": "Recognition timed out",
        "识别引擎尚未就绪": "The speech engine is not ready yet",
        "智能校对未成功，已使用识别原文": "AI proofreading did not succeed; the raw recognition was used",
        "LLM 服务响应无效": "Invalid response from the LLM service",
        "LLM API 错误 (%d)": "LLM API error (%d)",
        "LLM 响应格式无法解析": "The LLM response could not be parsed",
        "Base URL 格式不合法，请检查协议头（http/https）与地址。":
            "The base URL is malformed; check the scheme (http/https) and the address.",
        "%@ 是公网地址，明文 HTTP 只允许本机或局域网地址，公网请使用 https://。":
            "%@ is a public address. Plain HTTP is only allowed for loopback or private networks; use https:// for public hosts.",
        "GitHub 返回 HTTP %d": "GitHub returned HTTP %d",
        "无法解析 GitHub 的响应": "Could not parse GitHub's response",
        "不支持的热键: %@": "Unsupported hotkey: %@",
        "输入监控权限缺失，无法启动热键监听":
            "Input Monitoring permission is missing, so hotkey listening cannot start",
        "热键监听启动超时": "Starting hotkey listening timed out",
        "无法创建热键事件源": "Could not create the hotkey event source",
        "没有可用的音频输入设备，请检查麦克风连接": "No audio input device is available; check your microphone connection",
        "无法创建音频格式转换器": "Could not create the audio format converter",
    ]
}
