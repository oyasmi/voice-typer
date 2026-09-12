# VoiceTyper

[简体中文](README.md) | **English**

VoiceTyper turns speech into something you can reach for anywhere on your computer. Replying to a
message, writing a document, capturing an idea that just showed up — wherever the cursor is, that is
where the recognized text lands.

It sits quietly in the menu bar or the system tray. When you need to type, hold the hotkey and talk;
release it and carry on with what you were doing. No web page to open, no recording to upload to the
cloud, no copying and pasting between a transcript and the window you actually work in.

> **The macOS version now ships as a single, self-contained app.** The recognition engine lives
> inside VoiceTyper — there is no Python to configure and no server to start.
> [Download for macOS](https://github.com/oyasmi/voice-typer/releases) ·
> [Installation guide](macos/README.en.md)

## Voice input the way it should feel

Most transcription tools solve one problem: turning a recording into a document. VoiceTyper solves a
different one: **making speech an input method that any application can use, the way a keyboard is.**

Whether you are writing a long reply in a chat app, filling in a form in the browser, capturing a
thought in a notes app, or writing a paragraph of prose in an editor, you never leave the current
window. Hold, speak, release — the text appears at the cursor.

That ease comes from a few choices that look small and matter a lot:

- **It does not interrupt your train of thought.** VoiceTyper never asks you to create a recording,
  wait for a transcript, and then move the result somewhere else. The whole thing happens inside your
  current workflow.
- **You see the result while you speak.** The HUD keeps updating the text as you talk; when the model
  hears more context, earlier words are corrected too, rather than piling inaccurate fragments on top
  of each other.
- **Letting go means done.** The final recognition adds punctuation and normalizes spoken numbers into
  more natural written forms — “sixty-four megabytes” becomes “64MB”.
- **It does not sit around eating memory.** By default the macOS version loads the engine the first
  time you press the hotkey (in parallel with recording, about one second) and releases it again after
  a period of inactivity; starting at login does not mean keeping hundreds of megabytes resident for a
  dictation that may never happen.
- **It is a complete desktop app.** Model download, language selection, hotkey, permissions, launch at
  login, memory release and optional AI proofreading are all handled in the app. Day-to-day use never
  requires a terminal.

## The first run only asks for permissions

On first launch, VoiceTyper walks you through the system permissions that only you can grant. The
macOS version downloads, verifies and loads the ~240MB speech model in the background — there is no
download button to hunt for, and the model is fetched only once.

macOS turns that preparation into a four-step guide (Welcome → Permissions → Model → Try it), and the
last step has you complete a real dictation inside the guide window: all three permissions being green
does not mean the thing works. Only running the whole chain — hotkey → microphone → recognition →
insertion — proves it, and whichever link fails is called out on the spot.

Once that is done, everyday use is three actions:

1. Put the cursor where you want the text.
2. Hold the hotkey and speak, watching the live recognition in the overlay.
3. Release the hotkey; the final text is written into the current app.

A press shorter than 0.3 seconds is treated as an accidental tap and discarded. `Esc` cancels the
current dictation, so changing your mind leaves nothing behind (on macOS you can cancel during both
recording and recognition; Windows currently allows it during recording). macOS can also switch the
trigger to “press once to start, press again to stop”, so your finger does not have to stay down
through a long passage; the overlay can be moved to the bottom-right corner, follow the cursor, or be
hidden entirely.

While recording, the macOS overlay shows the name of the current input device and warns you if no
sound is coming in — you do not have to finish talking to discover the result was empty.

Default hotkeys:

| Platform | Default hotkey | Status |
| --- | --- | --- |
| macOS | `Fn` / globe key | Available; Apple Silicon, macOS 14 or later |
| Windows | `Ctrl + F2` | The unified version is implemented, but still needs verification on real Windows hardware before a formal release |
| Linux | — | No unified version yet; use the [client–server version](client-server/client_linux/README.md) |

## Why local recognition

Speech usually carries more private information than the text you finally write down: other people's
voices, conversations in the room, and fragments you never intended to keep. Local recognition is
therefore not a decorative selling point but a basic design premise of VoiceTyper.

By default:

- audio is processed in this computer's memory only, and never uploaded to VoiceTyper or any other
  speech service;
- the SenseVoice-Small model runs through the ONNX Runtime embedded in the app;
- once the model is downloaded, recognition works with no network at all;
- there is no account to register, and no quota or subscription;
- API keys are never written into ordinary configuration files — they live in the macOS Keychain or in
  Windows DPAPI-protected storage.

VoiceTyper also offers optional LLM proofreading for homophone errors, spoken filler and trickier
punctuation. It is off by default; only when you turn it on is the **recognized text** sent to the
OpenAI-compatible service you configured, and audio is never sent. If you want to stay fully offline,
point it at a compatible model running on your own machine.

## More than “it recognizes speech”

VoiceTyper aims to be local speech recognition you can keep on your computer for years, not a
technology demo that looks good once.

| Capability | What it means in use |
| --- | --- |
| System-wide input | Not tied to one editor; chat apps, browsers, documents and most text fields work |
| Live, self-correcting preview | No staring at a feedback-free recording state, and microphone or recognition problems surface immediately |
| Automatic punctuation and number normalization | The result is closer to text you can send or keep editing right away |
| Chinese, English, Cantonese, Japanese, Korean | Pick a language or let it decide; mixed Chinese–English needs no switching |
| Native hotkey and overlay | Does not steal focus from the current window, and follows the screen you are working on |
| Automatic idle unload | Frees the memory the engine holds when you are not using it, and restores it on your next recording |
| Optional LLM proofreading | Extra polish when you want it, fully local and simple when you do not |
| Bilingual interface | Menus, settings window and overlay have equivalent Chinese and English wording; Chinese is the default and you can switch in Settings (takes effect after a restart) |

## Installation

### macOS

Requirements: **Apple Silicon (M-series chip)**, macOS 14 Sonoma or later.

1. Download `VoiceTyper-<version>-macOS-arm64.dmg` from
   [Releases](https://github.com/oyasmi/voice-typer/releases).
2. Open the DMG and drag `VoiceTyper.app` into Applications.
3. On first launch, follow the four-step guide to grant microphone, accessibility and input-monitoring
   permissions.
4. The app prepares the speech model at the same time; use the last step of the guide to run one real
   dictation and confirm the whole chain works.

The current package is not notarized by Apple. If macOS blocks the first launch, go to System Settings
→ Privacy & Security and choose “Open Anyway”. For what each permission is used for, how to uninstall
and answers to common questions, see the [full macOS guide](macos/README.en.md).

### Windows

The unified Windows app supports Windows 10/11 on both x64 and arm64. The code and its automated tests
are complete, but compilation, installation, performance and long-running behaviour have not yet been
verified on real Windows hardware — so we do not describe it as a stable release.

If you want to help verify it or build from source, read the
[Windows usage and development guide](windows/README.en.md) and the
[list of risks still to verify](windows/DESIGN.md#11-风险与对策). Known issues before hardware
verification, their fix priority and the steps involved are in the
[Windows review and repair plan](windows/REVIEW_AND_REPAIR_PLAN.md). This status will be updated once
hardware verification is done.

## FAQ

### Is it an input method (IME)?

No. VoiceTyper does not replace your system input method; it writes the recognized text into whatever
input area currently has focus. You can keep your existing typing habits, and it never takes over the
keyboard.

### Does it need an internet connection every time?

No. The network is needed once, to download the speech model. Recognition after that happens locally.
Only LLM proofreading, if you turn it on, talks to the model service you configured.

### Why does it need microphone, accessibility and input-monitoring permissions?

The microphone is for recording, input monitoring is for responding to the global hotkey inside other
apps, and accessibility is for delivering text to the current cursor position. VoiceTyper does not use
these permissions to record anything unrelated to voice input.

### Where can I use it?

Anywhere ordinary pasting works. Some password fields, games, remote desktops and windows running as
administrator block simulated system-level input; in those cases the result usually remains on the
clipboard so you can paste it manually.

### Does it support Intel Macs or Linux?

The current unified macOS app is Apple Silicon only, and there is no unified Linux version yet. For
Intel Macs, Linux, remote recognition, or sharing one ASR server between several machines, use the
[client–server implementation](client-server/README.md) kept in this repository.

### Can I switch the speech model or use hotwords?

The unified app focuses on SenseVoice-Small and cannot be switched to paraformer. If you really need
that, use the client–server version and change the model on the server. Hotwords have been removed
from both forms: early versions had the feature, but it never actually took effect in the current
recognition pipeline. A clear, dependable default matters more than exposing a pile of unpolished
options.

## For developers

The macOS and Windows apps share the same product architecture: the hotkey starts recording, a local
session keeps producing a full preview, and releasing the key finishes recognition and inserts the
text. The Windows pipeline is a port of the verified macOS Swift implementation, and both platforms
share one set of Python golden fixtures to check fbank, LFR/CMVN and text post-processing for
consistency.

```text
voice-typer/
├── macos/             # Swift + AppKit/SwiftUI unified app
├── windows/           # .NET 10 + WinForms unified app
└── client-server/     # The maintained legacy architecture: Python server plus three platform clients
```

### Building and testing on macOS

```bash
cd macos
ruby scripts/generate_xcodeproj.rb
open VoiceTyper.xcodeproj

xcodebuild -project VoiceTyper.xcodeproj \
  -scheme VoiceTyper \
  -destination 'platform=macOS' test
```

### Building and testing on Windows

```bat
cd windows
dotnet restore
dotnet run
dotnet test
```

Further reading:

- [macOS usage and development](macos/README.en.md) · [Architecture](macos/DESIGN.md)
- [Windows usage and development](windows/README.en.md) · [Architecture](windows/DESIGN.md)
- [Client–server version](client-server/README.md) · [Protocol](client-server/PROTOCOL.md)

## Acknowledgements

VoiceTyper's local recognition is built on
[SenseVoice](https://github.com/FunAudioLLM/SenseVoice) and
[ONNX Runtime](https://github.com/microsoft/onnxruntime). Thanks to these projects for making
high-quality, offline-capable speech recognition something an ordinary desktop app can actually ship.

> Design documents (`DESIGN.md`) and the client–server documentation are currently maintained in
> Chinese only.

## License

This project is released under the [Apache License 2.0](LICENSE).

Third-party components (such as the SenseVoice model and ONNX Runtime) remain under their own
original licenses.
