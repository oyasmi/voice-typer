# VoiceTyper for Windows

[简体中文](README.md) · **English**

[← Project Home](../README.en.md) · [Download](https://github.com/oyasmi/voice-typer/releases) · [Changelog](CHANGELOG.md) · [Development](DEVELOPMENT.md)

VoiceTyper is a lightweight system tray voice typing app for Windows: hold the right-hand <kbd>Ctrl</kbd> key in any window, speak, and release — punctuated text lands right at your cursor. Speech recognition runs 100% locally on your PC; audio is never uploaded.

> [!NOTE]
> **Current status**: The Windows edition is ready for everyday dictation and stays in feature parity with macOS across core capabilities. Inference currently runs on CPU only; if you run into anything unexpected on arm64 laptops or multi-monitor DPI setups, please [report an issue](#reporting-issues).

## Installation & Requirements

| Item | Requirement |
| --- | --- |
| **OS** | Windows 10 (version 1809 or later) or Windows 11 |
| **Architecture** | x64 (most PCs) or arm64 (including Snapdragon X laptops) |
| **Runtime** | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (installed once separately) |
| **Disk Space** | ~300 MB (app + speech model downloaded on first launch) |
| **Network** | Needed once for the initial model download; works completely offline afterward |

### Install Steps

1. **Install the .NET 10 Desktop Runtime**: Visit the [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0) and download **.NET Desktop Runtime** matching your PC's architecture (usually `x64`).
   - Note: The base console `.NET Runtime` or `.NET 8 / 9` will not work; if you already have the `.NET 10 SDK` installed, you're all set.
2. **Download VoiceTyper** from [Releases](https://github.com/oyasmi/voice-typer/releases):
   - Most PCs: `VoiceTyper-<version>-win-x64-setup.exe`
   - ARM laptops: `VoiceTyper-<version>-win-arm64-setup.exe`
   - Prefer no installer? Grab the `*-portable.zip` archive, extract it to a fresh folder, and run `VoiceTyper.exe`.
3. **Run the installer**: It installs into your user profile with no administrator (UAC) prompt. If Windows SmartScreen shows “Windows protected your PC”, click **More info → Run anyway** (builds are not yet commercially code-signed).

## First Launch

On your first run, a four-step setup guide walks you through **Welcome → Microphone → Speech Model → Try It**:

- **Microphone check**: Pick your input device and click **Test microphone** to verify live audio levels. If the mic fails to open, check **Settings → Privacy & security → Microphone** in Windows to make sure desktop apps are allowed access. No other Windows permissions are required.
- **Automatic speech model setup**: The ~240 MB speech model downloads and verifies itself in the background, supporting resume after interruptions, automatic retries, and fallback mirrors.
- **Live test dictation**: Complete one real dictation inside the guide window to confirm your hotkey, microphone, model, and text insertion all work together. You can reopen this guide anytime via **Setup Guide...** in the tray menu.

> **Finding the tray icon**: VoiceTyper sits in the system tray at the bottom right of your taskbar (Windows 11 tucks new icons behind the `^` overflow arrow — drag it onto the taskbar to keep it visible). The colored badge dot indicates state: **Red** = recording, **Yellow** = recognizing / inserting, **Orange** = downloading model, **Grey** = loading / paused, **Dark red** = error.

## Everyday use

1. Click where you want text to appear.
2. Hold **Right <kbd>Ctrl</kbd>**: The overlay briefly shows a grey “Starting microphone…” — **begin speaking once it turns red**. Live results stream in the overlay and revise earlier words automatically as you continue speaking.
3. Release Right <kbd>Ctrl</kbd> to insert the final punctuated text into your active window.

### Hotkey Tips

- **Normal shortcuts stay intact**: Right <kbd>Ctrl</kbd> triggers dictation only when held by itself. Pressing another key while holding it (such as Right <kbd>Ctrl</kbd> + <kbd>C</kbd>) acts as an ordinary shortcut and silently discards the recording.
- **No Right <kbd>Ctrl</kbd> key?** Many recent laptops replace Right <kbd>Ctrl</kbd> with a Copilot key. In **Settings → Dictation**, click the **<kbd>Ctrl</kbd> + <kbd>Tab</kbd>** preset button (note: this overrides tab switching in other apps), or click **Record Hotkey** to set any custom chord like <kbd>Ctrl</kbd> + <kbd>F2</kbd>. Right <kbd>Alt</kbd> is not recommended because it can activate app menu bars and steal focus from input fields.

### Handy Details

- **Cancel anytime**: Press <kbd>Esc</kbd> while recording or recognizing to cancel immediately without typing anything.
- **Recover transcripts**: Right-click the tray icon for two recovery options (kept in RAM only and cleared on exit):
  - **Copy last transcription**: Copies your latest finished transcript in one click;
  - **Recent transcriptions...**: Browse up to your last 20 entries newest-first (including cancelled or partial transcripts, clearly labeled). Select any entry to inspect and copy it, click **Copy all (oldest first)** to join every retained entry with newlines in speaking order, or **Clear history** anytime.
- **Long dictation & clean endings**: Switch the trigger mode in Settings to **press once to start, press again to stop**. Sessions run up to 120 seconds (with a countdown during the final 15 seconds, inserting normally at the limit); taps shorter than 0.3s are ignored as accidental; releasing the hotkey normally captures ~150ms of extra audio so your last syllable isn't cut off.
- **Mixed Chinese & English**: Speak both languages without switching — clean spacing between Chinese and English words (`我用 Python 写代码`) and spoken numbers are formatted automatically.
- **Keep your transcript when switching windows**. If the foreground window at insertion differs from the one where dictation started, the app copies the transcript to the clipboard for manual pasting.
- **Quick tray controls**: Right-click the tray icon to switch recognition language, toggle text correction, change microphone policy (Auto vs. System Default), pause dictation, or check for updates.

## Settings

Everything is configured in the Settings window — no config files to edit. The window adapts smoothly to display scaling and narrower screens while keeping **Save** and **Discard** pinned at the bottom:

| Page | What's Inside |
| --- | --- |
| **Dictation** | Hotkey presets & recorder, trigger mode (hold vs. toggle), microphone device & **Test microphone**, recognition language (Auto / Chinese / English / Cantonese / Japanese / Korean) |
| **Speech Model** | Model status, download manager & source diagnostics, memory policy |
| **Vocabulary** | Custom vocabulary list (names, brands, jargon), recognition bias strength, live replacement preview |
| **Text correction** | OpenAI-compatible base URL, API key, model name, connection test |
| **Appearance and general** | Overlay position & opacity (with live sample preview), launch at login, interface language |
| **Diagnostics and help** | Microphone check, stage timings for the last 20 dictations, one-click diagnostic copy, log & config folders |

Key options worth knowing:

- **Shortcut conflict warnings**: Recording a custom hotkey warns you if it collides with everyday shortcuts like <kbd>Ctrl</kbd> + <kbd>C</kbd> or <kbd>Alt</kbd> + <kbd>Tab</kbd>.
- **Bluetooth headphone protection**: Microphone defaults to **Auto** — when both system input and output point to Bluetooth headphones, VoiceTyper automatically records from your PC's built-in microphone so your headphones don't drop into muffled hands-free call audio. You can also pin a specific microphone or follow the Windows default.
- **Memory management**: By default, the model loads at launch so your hotkey responds instantly. To save RAM, disable launch preloading or set an idle unload timeout — the model will load while you speak on your next dictation.
- **Interface language**: Choose Simplified Chinese or English (applied on restart), independent of your recognition language.

### Custom vocabulary

Add frequently misrecognized names, brands, project code names, and jargon in **Settings → Vocabulary** (up to 200 entries; click **Save and Apply** to activate from your next dictation, 100% offline):

- **Spelling**: The exact output you want, e.g., `VoiceTyper`, `Claude`, or `张三丰`.
- **Sounds like** (optional): Common misrecognitions or phonetic approximations (e.g., `克劳德` for `Claude`; separate multiple hints with commas). Variations differing only in case, spaces, or hyphens (`voice typer`, `Voice-Typer`) normalize automatically without needing to be listed.
- **Recognition bias**: Choose **Off / Light / Standard / Strong** (`Standard` by default). Stronger settings favor listed terms more readily, while clearly different-sounding words are never altered. Step down one level if ordinary similar-sounding words get pulled toward a custom entry.
- **Add from recent results & Try replacement**: Highlight a misrecognized span from a recent dictation to add it directly, or type a sample sentence into **Try replacement** to preview text replacements right away (acoustic bias is experienced during live dictation).
- **Status indicator**: Once saved, `✓` means the entry participates in both acoustic recognition bias and text replacement; `⚠` marks entries like single characters or single English word pieces that apply text replacement only.

When AI text correction is enabled, your vocabulary list is also included in the correction context.

### AI text correction (optional, off by default)

Connect any OpenAI-compatible endpoint (including local models via Ollama or LM Studio) to fix homophones, stutters, and punctuation:

- When turned on, **only the recognized text** (along with your custom vocabulary and low-confidence acoustic alternatives) is sent to your configured endpoint — audio is never transmitted. To minimize latency, VoiceTyper pre-warms the network connection when recording begins (sending neither your key nor any text). Your API key is encrypted with Windows DPAPI under your user account.
- Press your hotkey again during correction to skip waiting and insert the raw transcript immediately (for single-modifier hotkeys like Right <kbd>Ctrl</kbd>, tap and release the key by itself; chords like Right <kbd>Ctrl</kbd> + <kbd>C</kbd> won't accidentally skip).
- If correction times out or errors out, the raw transcript is inserted automatically.
- To evaluate correction quality, you can optionally enable **Record proofreading logs (for evaluation)** (off by default); entries stay strictly on your PC in the temporary folder or a custom path, are never uploaded, and can be cleared anytime from Settings.

## Privacy

- Audio stays strictly in RAM — never written to disk and never uploaded.
- Works completely offline once the speech model is downloaded; **Check for Updates** connects only when you click it manually.
- No account and no usage limits.
- Standard logs and copied diagnostics never contain recognized text or custom vocabulary (proofreading records are only saved locally if you explicitly enable **Record proofreading logs**, which is off by default); the last transcription and the 20-entry history live in RAM only and clear on exit.

## FAQ

### Pressing the hotkey does nothing?

1. Make sure VoiceTyper is running in the system tray and **Pause Dictation** is unchecked.
2. Check that no other app has registered the same global hotkey (such as the legacy split client).
3. If an **elevated (Run as Administrator)** window like Task Manager is in the foreground, Windows may block non-elevated apps from receiving hotkey events — switch to a standard window and try again.

### Recognition succeeded, but nothing was typed into my window?

When the target window is running as Administrator (e.g., an elevated terminal), Windows User Interface Privilege Isolation (UIPI) blocks standard apps from sending simulated keystrokes to it. If VoiceTyper says it copied the transcript, press <kbd>Ctrl</kbd> + <kbd>V</kbd> to paste it manually. Some fullscreen games, remote desktop clients, and password fields may also reject simulated paste. You can copy the transcript from recent history.

### The overlay says no sound was detected?

While recording, the overlay shows which microphone is active. Check that it isn't muted, that the right device is selected, and that Windows microphone privacy settings permit desktop apps. Use **Test microphone** in **Settings → Dictation** to verify input levels.

### Model download stalls or fails?

Open **Settings → Speech Model → Download details** to see per-mirror error messages. TLS or certificate errors usually point to an inaccurate system clock, a proxy, or corporate HTTPS inspection. Once resolved, click **Retry Download** to resume from where it stopped.

### Launch error saying the .NET runtime is missing?

Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — make sure to pick **Desktop Runtime** matching your PC's architecture (`x64` or `arm64`).

### Can it coexist with the legacy split client?

Yes, though running only one at a time is recommended so they don't compete for the same hotkey. On first launch, your hotkey (the legacy default was <kbd>Ctrl</kbd> + <kbd>F2</kbd>) and overlay opacity carry over automatically.

### Known limitations

- CPU inference only;
- Built around SenseVoice-Small, with no model switching;
- Left <kbd>Alt</kbd>, <kbd>Win</kbd>, and <kbd>Shift</kbd> cannot be used alone as single-key hotkeys;
- Dictation can start in password fields, and the transcript may be inserted if the target app accepts the paste.
- Local on-device recognition only, with no remote server mode (for remote GPU servers or alternative ASR models, use the [client–server edition](../client-server/README.md)).

## Uninstall

Uninstall VoiceTyper from **Settings → Apps → Installed apps** in Windows (or via **Uninstall VoiceTyper** in the Start menu). Your configuration and downloaded speech model are left intact by default; to remove everything completely, delete both folders:

- `%APPDATA%\VoiceTyper\`
- `%LOCALAPPDATA%\VoiceTyper\`

## Reporting issues

Open an [issue on GitHub](https://github.com/oyasmi/voice-typer/issues) describing what happened along with your Windows version and architecture (`x64` / `arm64`). Diagnostics help: in **Settings → Diagnostics and help**, click **Copy diagnostics** or open the log folder (`%LOCALAPPDATA%\VoiceTyper\logs\`) — neither contains recognized text.

## Building from Source

```bat
cd windows
dotnet run
build.bat
```

Requires the .NET 10 SDK. See [DEVELOPMENT.md](DEVELOPMENT.md) for cross-compilation, installer packaging, code signing, testing, and log fields, and [DESIGN.md](DESIGN.md) for architecture and design rationale (both in Chinese).
