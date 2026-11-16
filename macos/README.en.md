# VoiceTyper for macOS

[简体中文](README.md) · **English**

[← Project Home](../README.en.md) · [Download](https://github.com/oyasmi/voice-typer/releases) · [Changelog](CHANGELOG.md) · [Development](DEVELOPMENT.md)

VoiceTyper is a lightweight menu bar voice typing app for Mac: hold <kbd>Fn</kbd> in any window, speak, and release — punctuated text lands right at your cursor. Speech recognition runs 100% locally on your Mac; audio is never uploaded.

## Installation & Requirements

| Item | Requirement |
| --- | --- |
| **Chip** | Apple Silicon (M-series). Intel Macs are not supported |
| **OS** | macOS 14 Sonoma or later |
| **Disk Space** | ~300 MB (app + speech model downloaded on first launch) |
| **Network** | Needed once for the initial model download; works completely offline afterward |

### Install Steps

1. Download the latest `VoiceTyper-<version>-macOS-arm64.dmg` from [Releases](https://github.com/oyasmi/voice-typer/releases).
2. Open the DMG and drag `VoiceTyper.app` into **Applications**.
3. Launch VoiceTyper from Applications.

> [!NOTE]
> Builds are not notarized by Apple yet. If macOS says the developer cannot be verified on first launch, open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** (one-time approval only).

## First Launch

On your first run, a four-step setup guide walks you through **Welcome → Permissions → Speech Model → Try It**.

### 1. Grant Three System Permissions

All three permissions are required; the guide gives you direct shortcuts to each pane:

| Permission | Purpose | If Missing |
| --- | --- | --- |
| **Microphone** | Captures your voice | Pressing the hotkey cannot start recording |
| **Input Monitoring** | Listens for your global hotkey while other apps are active | Pressing the hotkey does nothing |
| **Accessibility** | Inserts text at your cursor and intercepts <kbd>Esc</kbd> cancellation | Live preview works, but text cannot be typed into the target app |

### 2. Automatic Speech Model Setup

While you grant permissions, the ~240 MB speech model downloads and verifies itself in the background. Downloads support automatic retries and resume from interruptions; if your Mac already has a model cache from the legacy VoiceTyper server, it is reused automatically.

### 3. Live Test Dictation

The final step lets you perform a real dictation right inside the guide. Completing it confirms that your hotkey, microphone, model, and text insertion work end-to-end — and pinpoints whichever step needs attention if anything fails. You can reopen this guide anytime from **Setup Guide…** in the menu bar.

## Everyday use

1. Click where you want text to appear.
2. Hold <kbd>Fn</kbd> and start speaking **once the overlay appears**. The overlay streams live text and revises earlier words automatically as more context arrives.
3. Release <kbd>Fn</kbd> to insert the final punctuated transcript into your active app.

### Handy Details

- **Cancel anytime**: Press <kbd>Esc</kbd> while recording or recognizing to discard everything cleanly — without closing foreground dialogs or exiting fullscreen apps.
- **Recover transcripts**: Use **Copy Last Result** or **Recent Results** in the menu bar to retrieve up to your last 10 transcripts (including cancelled ones or ones that couldn't be inserted automatically). Click any item to copy it; entries stay in RAM only and clear when you quit.
- **Long dictation**: Switch the trigger mode in Settings to **press once to start, press again to stop**. Single sessions run up to 120 seconds (with a countdown for the final 15 seconds); taps under 0.3s are ignored as accidental presses; releasing the hotkey captures ~150ms of trailing audio so the final syllable isn't clipped.
- **Mixed Chinese & English**: Speak both languages naturally without switching — spaces between Chinese and English words (`我用 Python 写代码`) and spoken numbers are formatted automatically.
- **Bluetooth headphone friendly**: With the default **Auto** microphone policy, VoiceTyper automatically records from your Mac's built-in mic while Bluetooth headphones are connected, preventing macOS from dropping your headphones into low-quality hands-free call audio or pausing music.
- **Password field protection**: Dictation won't start when a password field is focused; if focus moves into a password field mid-recording, the transcript is copied to the clipboard instead of being typed.
- **Quick menu bar controls**: Switch recognition language, microphone policy, or AI proofreading — or pause dictation — right from the menu bar icon.

## Settings

Everything is configured in the Settings window — no config files to edit by hand:

| Tab | What's Inside |
| --- | --- |
| **Permissions** | Live status and quick links for Microphone, Input Monitoring, and Accessibility |
| **Recognition** | Model status, recognition language (Auto / Chinese / English / Cantonese / Japanese / Korean), memory policy, AI proofreading |
| **Vocabulary** | Custom vocabulary list (names, brands, technical terms) and recognition bias strength |
| **General** | Hotkey, trigger mode (hold vs. toggle), microphone policy, overlay position & opacity, launch at login, UI language |

Key options worth knowing:

- **Flexible hotkeys**: Choose <kbd>Fn</kbd>, a key chord (like <kbd>⌥</kbd><kbd>Space</kbd>), or a **single modifier key** (Right <kbd>⌘</kbd>, Right <kbd>⌥</kbd>, Left <kbd>⌥</kbd>, or Right <kbd>⌃</kbd> — great for external keyboards without <kbd>Fn</kbd>). Holding a single-modifier hotkey together with another key still works as a normal keyboard shortcut.
- **Memory management**: By default, the speech model preloads at launch (~500 MB RAM) so pressing your hotkey responds instantly. To save memory, turn off launch preloading or set an idle unload timer — the model will load while you speak on the next press.
- **Interface language**: Choose Simplified Chinese or English (applied on restart), independent of your recognition language.

### Custom vocabulary

Add frequently misrecognized names, product brands, code names, and jargon in **Settings → Vocabulary** (up to 200 entries, processed 100% locally offline):

- **Spelling**: The exact output you want, e.g., `VoiceTyper`, `Claude`, or `张三丰`.
- **Sounds like** (optional): Common misrecognitions or phonetic approximations (e.g., `克劳德` for `Claude`). Variations that differ only in capitalization, spaces, or hyphens (`voice typer`, `Voice-Typer`) normalize automatically without needing to be listed.
- **Recognition bias**: Choose **Off / Light / Standard / Strong** (`Standard` by default). Higher levels favor your listed terms more aggressively, while clearly different-sounding words remain untouched. Step down one level if ordinary similar-sounding words get pulled toward a custom term.
- **Add from Recent Results**: Spot a misrecognized word? Click **Add from Recent Results…**, highlight the wrong characters, and type the right spelling.
- **Status indicator**: `✓` means the entry participates in both acoustic recognition bias and text replacement; `⚠` marks single characters or single English word pieces, which use text replacement only so everyday words aren't over-biased.

When AI proofreading is enabled, your vocabulary list is also shared with the proofreading prompt.

### AI proofreading (optional, off by default)

Connect any OpenAI-compatible endpoint (including local models via Ollama or LM Studio) to polish homophones, stutters, and punctuation:

- When turned on, **only the recognized text** is sent to your configured endpoint — audio is never transmitted. Your API key is stored in the macOS Keychain.
- Press your hotkey again while proofreading is running to skip it immediately and insert the raw transcript.
- If the request times out or fails, VoiceTyper falls back to the raw transcript automatically and briefly notes why in the overlay.

## Privacy

- Audio stays strictly in RAM — never written to disk and never uploaded.
- Works completely offline once the model is downloaded; no account or usage limits.
- Diagnostic logs never contain recognized text or custom vocabulary; **Recent Results** exist in memory only and are wiped on quit.

## FAQ

### Pressing <kbd>Fn</kbd> opens the Emoji picker or switches input sources?

Open **System Settings → Keyboard** and set **Press 🌐 key to** to **Do Nothing**. Both the setup guide and **Settings → General** detect this conflict and offer a direct shortcut — or simply pick a different hotkey such as Right <kbd>⌘</kbd> or Right <kbd>⌥</kbd>.

### Pressing the hotkey does nothing at all?

1. Verify **Input Monitoring** is enabled in **Settings → Permissions**.
2. **Just updated VoiceTyper?** Because builds are ad-hoc signed rather than Apple-notarized, macOS may invalidate existing permission entries after an update: in **System Settings → Privacy & Security → Input Monitoring** (and **Accessibility**), remove VoiceTyper with `-`, add it back with `+`, then quit and relaunch VoiceTyper.
3. If an app has enabled **Secure Keyboard Entry** (such as Terminal) or a password field is focused, macOS blocks combination hotkeys and notes this in the menu bar.

### The overlay shows recognized text, but nothing is typed into my app?

Check **Accessibility** in **Settings → Permissions** (re-adding it after an app update if needed). A few apps reject simulated input altogether; your text is already on the clipboard and in **Recent Results**, ready to paste with <kbd>⌘</kbd><kbd>V</kbd>.

### The overlay says nothing was recognized?

While recording, the overlay displays the active microphone name. Check that your mic isn't muted, that **System Settings → Sound → Input** points to a working device, and that another app isn't holding exclusive access.

### Model download stalls or fails?

Check your connection to ModelScope, then click retry in **Settings → Recognition** — the download resumes near where it left off.

### Can it coexist with the legacy split client?

Yes, though both default to <kbd>Fn</kbd> and will compete for the hotkey if run simultaneously, so keeping only one running is recommended. On first launch, your hotkey and overlay opacity migrate automatically from the legacy client.

### Known limitations

- Apple Silicon (M-series) Macs only;
- Built around SenseVoice-Small, without switching to other ASR models;
- Local on-device recognition only, with no remote server mode (for Intel Macs, alternative ASR models, or remote GPU servers, use the [client–server edition](../client-server/README.md)).

## Uninstall

1. Quit VoiceTyper and delete `VoiceTyper.app` from **Applications**.
2. Remove settings and downloaded models at `~/Library/Application Support/VoiceTyper/`.
3. Optionally remove VoiceTyper from Microphone, Input Monitoring, and Accessibility under **System Settings → Privacy & Security**.

## Reporting Issues

Open an [issue on GitHub](https://github.com/oyasmi/voice-typer/issues) with what happened, your macOS version, and your Mac model. Including logs helps: open the **Console** app and filter by `com.voicetyper.app` (logs never include recognized text).

## Building from Source

```bash
cd macos
ruby scripts/generate_xcodeproj.rb
./build_xcode.sh
```

See [DEVELOPMENT.md](DEVELOPMENT.md) for code signing, notarization, testing, and log fields, and [DESIGN.md](DESIGN.md) for architecture and design rationale (both in Chinese).
