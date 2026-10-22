# VoiceTyper for Windows

[简体中文](README.md) | **English**

[← Project home](../README.en.md) · [Download](https://github.com/oyasmi/voice-typer/releases) · [Changelog](CHANGELOG.md) · [Development](DEVELOPMENT.md)

VoiceTyper is a system tray app: hold the right-hand `Ctrl` key in any app and speak, release it, and the
text appears at the cursor. Speech recognition runs entirely on your PC; recordings are never uploaded.

> **Status**: the Windows app has feature parity with macOS, and dictation and model download have been
> run on real Windows hardware. Systematic real-device testing — especially arm64 devices, long-running
> sessions and multi-monitor DPI — is still in progress. Please [report](#reporting-issues) anything odd.

## Requirements

| Item | Requirement |
| --- | --- |
| System | Windows 10 (1809 or later) or Windows 11 |
| Architecture | x64 or arm64 (including Snapdragon X laptops) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (installed separately) |
| Disk | About 300MB (app + speech model downloaded on first launch) |
| Network | Only for the first model download |

## Installation

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0): pick
   **Desktop Runtime** for your PC's architecture (most PCs are x64). The plain .NET Runtime or .NET 8/9
   won't do; if you already have the .NET 10 SDK, you're set.
2. Download the installer from [Releases](https://github.com/oyasmi/voice-typer/releases):
   `VoiceTyper-<version>-win-x64-setup.exe` (most PCs) or `VoiceTyper-<version>-win-arm64-setup.exe`
   (ARM laptops).
3. Run it. It installs into your user folder and needs no administrator rights.
4. If you see “Windows protected your PC”, click “More info → Run anyway”.

> The installer is not code-signed, which triggers SmartScreen; this does not affect how the app works. If
> the .NET runtime is missing, the installer tells you where to get it. Prefer not to install? Download the
> portable `*-portable.zip`, unzip it and run `VoiceTyper.exe`.

## First launch

A four-step guide opens: **Welcome → Microphone → Speech model → Try it**.

- **Microphone**: pick your input device and click “Test microphone” to see a live level meter. If the
  microphone can't be opened, check that Settings → Privacy & security → Microphone allows desktop apps.
  No other system permissions are needed on Windows.
- **The speech model** (about 240MB) starts downloading in the background on first launch and is verified
  automatically. Downloads resume after interruptions, retry on failure and fall back to mirror sources.
- **Try it**: complete one real dictation inside the guide window. Only a successful run proves that the
  hotkey, microphone, recognition and text insertion all work; if one of them fails, the guide tells you
  which. You can reopen it from the tray menu (“Setup Guide...”).

VoiceTyper lives in the system tray at the bottom right of the taskbar. Windows 11 tucks new icons behind
the “^” arrow; drag it out to keep it visible.

## Everyday use

1. Put the cursor where you want the text.
2. Hold **Right Ctrl**. The overlay first shows a grey “Starting microphone…”; **start speaking once it
   turns red**. The overlay shows live results and corrects earlier words as you keep talking.
3. Release Right Ctrl. The final, punctuated text is written into the current app.

Right Ctrl only starts dictation when held on its own; holding it while pressing another key (such as
Right Ctrl + C) is still a normal shortcut, and that recording is silently discarded. If your keyboard has
no Right Ctrl (many new laptops replace it with a Copilot key), choose the second preset, **Ctrl + Tab**,
or record a custom combination in Settings.
Ctrl + Tab will replace tab switching in other apps. Right Alt can activate menus and move focus away
from the input field, so it is no longer recommended.

Useful details:

- **Cancel**: press `Esc` while recording or recognizing; nothing is written.
- **Recover results**: “Copy last transcription” still copies the latest result. “Recent transcriptions...”
  shows the last 20 final results, including the latest, newest first. Select an entry to read its full text
  and copy it, or use “Copy all (oldest first)” to join all retained entries with newlines in speaking order.
  History is kept in memory only and cleared on exit. Wait for active dictation to finish before copying.
- **Long dictation**: switch the trigger to “press once to start, press again to stop” in Settings.
- **Length**: up to 120 seconds per dictation, with a countdown for the last 15 seconds; at the limit the
  dictation ends and is inserted normally. Presses shorter than 0.3 seconds are ignored as accidental.
- **Mixed Chinese and English**: no switching needed; a space is inserted between Chinese and English words.
- **No surprise typing**: if you switch to another window while dictating, the result is only copied to
  the clipboard, never typed into a window you didn't expect.

A small dot on the tray icon shows the current state: red while recording, yellow while recognizing or
inserting, orange while the model downloads, grey while loading or paused, dark red on errors.
Right-click the tray icon for Settings, Pause Dictation, Check for Updates and more.

## Settings

Everything is configurable in the Settings window; there is no config file to edit.

The window adapts to display scaling and available space. In narrow windows, fields stack vertically and
pages scroll while Save and Discard remain at the bottom. Microphone, service URL and model name fields
use the full content width for longer values.

| Page | Contents |
| --- | --- |
| Dictation | Hotkey, trigger mode, microphone and “Test microphone”, recognition language (auto / Chinese / English / Cantonese / Japanese / Korean) |
| Speech Model | Model status and download, memory |
| Text correction | Service URL, API key, model name, test |
| Appearance and general | Overlay position and opacity, launch at login, interface language |
| Diagnostics and help | Microphone check, timings of the last 20 dictations, log and config folders |

- **Hotkey**: **Right Ctrl** is the default and first recommendation, followed by **Ctrl + Tab**. Both have
  preset buttons in Settings. You can also click “Record Hotkey”
  and press any combination you want (such as `Ctrl + F2`); you'll be warned if it collides with a common
  shortcut such as `Ctrl + C` or `Alt + Tab`.
- **Microphone**: “Auto” by default — when both the default input and output are Bluetooth headphones,
  the built-in microphone is used instead, so the headphones don't drop into low-quality call mode. You can
  also pin a specific device or follow the system default.
- **Memory**: by default the model loads at launch and stays resident, so the hotkey responds instantly.
  If memory is tight, turn off preloading or release the model after a period of inactivity; it then
  loads while you record.
- **Interface language**: Chinese or English, applied after restarting the app. It is independent of the
  recognition language.

### AI text correction (optional, off by default)

Connect any OpenAI-compatible service (including a model running on your own machine) to fix homophones,
slips of the tongue and punctuation. When enabled, only the **recognized text** is sent to the service you
configured — never audio. To cut waiting time, a connection to the service is opened when recording starts
(carrying no key and no text). The API key is encrypted with Windows DPAPI so only your user account can
read it.

Press the hotkey again during correction to skip it and insert the original text; if correction fails,
the original text is used automatically.

## Privacy

- Audio is processed in memory only — never written to disk, never uploaded.
- Once the model is downloaded, VoiceTyper works fully offline; “Check for Updates” only goes online
  when you click it.
- No account, no usage limits.
- Logs never contain recognized text; the last transcription and the last 20 history entries are kept
  in memory only and cleared on quit.

## FAQ

### The hotkey does nothing?

Make sure the VoiceTyper icon is in the tray, dictation isn't paused, and no other program uses the same
hotkey (for example the older split client running at the same time). When an elevated window such as Task
Manager is in front, Windows may block the hotkey; switch to a normal window and try again.

### Recognition works, but the text doesn't appear?

If the target window runs as administrator (e.g. an elevated terminal), Windows blocks ordinary programs
from typing into it. The result is already on the clipboard — press `Ctrl + V`. Some games, remote desktops
and password fields also reject simulated input.

### The overlay says no sound was detected?

While recording, the overlay shows which microphone is in use. Make sure it isn't muted, the right device
is selected, and Windows' microphone privacy settings allow access. “Test microphone” in Settings →
Dictation helps narrow it down.

### The model download fails?

Open Settings → Speech Model → “Download details” to see why each source failed. TLS or certificate errors
usually come from the system clock, a proxy or a corporate network. Fix that and click “Retry Download”;
it resumes where it stopped.

### It says the .NET runtime is missing?

Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — **Desktop
Runtime**, matching your PC's architecture.

### Can it coexist with the older split client?

Yes, but keeping only one is recommended. When migrating, your hotkey (the old default was `Ctrl + F2`)
and overlay opacity carry over automatically; if both use the same hotkey, they will fight over it when run
together.

### Known limitations

CPU inference only; only the SenseVoice-Small model, with no model switching; custom vocabulary (hotwords) works in
recognition but has no settings page yet (the macOS app has one; Windows will follow — advanced users can write it in the
config file as described in [DEVELOPMENT.md](DEVELOPMENT.md#配置文件) and restart); Left Alt, Win
and Shift can't be used alone as the hotkey; recognition always runs locally, with no remote server option
(use the [client–server version](../client-server/README.md) for that).

## Uninstall

Uninstall VoiceTyper from Settings → Apps, or use “Uninstall VoiceTyper” in the Start menu. Settings and
the model are kept; to remove everything, delete `%APPDATA%\VoiceTyper\` and `%LOCALAPPDATA%\VoiceTyper\`.

## Reporting issues

Open an [issue](https://github.com/oyasmi/voice-typer/issues) with what happened, your Windows version and
architecture. Diagnostics help: Settings → Diagnostics and help can copy diagnostic information and open the
log folder (`%LOCALAPPDATA%\VoiceTyper\logs\`). Neither contains recognized text.

## Building from source

```bat
cd windows
dotnet run
build.bat
```

Requires the .NET 10 SDK. Cross-compiling, signing, tests and log fields are covered in
[DEVELOPMENT.md](DEVELOPMENT.md); architecture and design decisions in [DESIGN.md](DESIGN.md). Both are in
Chinese.
