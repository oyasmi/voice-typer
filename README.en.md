<div align="center">

# VoiceTyper

**Hold a hotkey, speak, release — text lands right at your cursor.**

Local, offline voice typing for macOS & Windows · Audio never leaves your device · Works alongside any input method

[![Release](https://img.shields.io/github/v/release/oyasmi/voice-typer)](https://github.com/oyasmi/voice-typer/releases)
[![License](https://img.shields.io/github/license/oyasmi/voice-typer)](LICENSE)
![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-blue)
![ASR](https://img.shields.io/badge/ASR-100%25%20Local%20Offline-success)

[简体中文](README.md) · **English**

[Download](#download) · [Features](#features) · [Quick Start](#quick-start) · [Privacy](#privacy) · [FAQ](#faq) · [Contributing](#contributing)

</div>

---

VoiceTyper lives quietly in your macOS menu bar or Windows system tray. Reply to messages, draft documents, write code comments, or capture a fleeting thought — no web page to open, no audio to upload, and no copying and pasting between a transcription window and your active app. It never replaces your system input method; **anywhere you can type, you can speak**.

## Download

| Platform | System Requirements | Default Hotkey | Downloads & Guides |
| --- | --- | --- | --- |
| **macOS** | Apple Silicon (M-series), macOS 14+ | <kbd>Fn</kbd> | [Download DMG](https://github.com/oyasmi/voice-typer/releases) · [macOS User Guide](macos/README.en.md) |
| **Windows** | Windows 10 (1809+) / 11, x64 or arm64<br>Requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | Right <kbd>Ctrl</kbd> | [Installer / Portable](https://github.com/oyasmi/voice-typer/releases) · [Windows User Guide](windows/README.en.md) |
| **Linux / Intel Mac** | No standalone local app yet | — | Use the [client–server edition](client-server/README.md) |

> [!TIP]
> Builds are not yet Apple-notarized or Windows code-signed. On first launch, if the OS blocks the app: on macOS open **System Settings → Privacy & Security** and click **Open Anyway**; on Windows click **More info → Run anyway** (one-time only).

## Features

- **Type anywhere without switching IMEs**: Chat apps, browsers, docs, terminals, IDEs — text goes straight to your active cursor while your regular keyboard layout and input method stay untouched.
- **Live preview with self-correction**: A lightweight overlay streams your words as you speak and revises earlier homophones automatically as more context arrives.
- **Polished output out of the box**: Adds punctuation automatically, formats spoken numbers as digits, and inserts clean spacing between Chinese and English words (`我用 Python 写代码`).
- **100% local, offline, and free**: Powered on-device by SenseVoice. No account, no subscriptions, no usage caps, and works without internet access.
- **Multilingual & code-switching friendly**: Supports Chinese, English, Cantonese, Japanese, and Korean with automatic language detection — mix Chinese and English naturally without flipping switches.
- **Custom vocabulary (hotwords)**: Add names, product brands, and domain jargon (with optional phonetic hints) so on-device recognition favors your exact spellings.
- **Thoughtful everyday ergonomics**:
  - Never steals window focus; press <kbd>Esc</kbd> anytime to discard;
  - Uses your built-in microphone automatically when Bluetooth headphones are connected so music won't pause or drop into low-bitrate call audio;
  - Automatically skips password fields on macOS. Transcripts from cancelled or unsuccessful dictation can be recovered from recent history.
  - Supports both **hold-to-talk** and **press-to-toggle** for longer passages.
- **Optional AI proofreading**: Off by default. Connect any OpenAI-compatible endpoint (including local models via Ollama or LM Studio) to clean up slips of the tongue and homophones.

## Quick Start

1. **One-time setup**: Launch VoiceTyper and follow the four-step setup guide — grant permissions (Microphone, Input Monitoring, and Accessibility on macOS; Microphone check on Windows), let the ~240 MB speech model download automatically in the background, and run a live test dictation right inside the guide.
2. **Hold and speak**: Click where you want text to appear, **hold your hotkey, and speak** while watching live feedback in the overlay.
3. **Release to insert**: Let go of the hotkey and punctuated text is typed at your cursor (taps shorter than 0.3s are ignored as accidental presses).

> For platform-specific hotkey presets, recent transcription history, and menu bar / tray shortcuts, check the [macOS User Guide](macos/README.en.md#everyday-use) and [Windows User Guide](windows/README.en.md#everyday-use).

## Privacy

Spoken audio carries far more personal context than final written text. **On-device recognition is VoiceTyper's foundational constraint, not an optional mode:**

- **Audio never leaves your computer**: Recordings are processed strictly in memory — never written to disk and never uploaded anywhere.
- **Works completely offline**: Once the speech model is downloaded on first launch, no internet connection is needed. Update checks happen only when you click them manually.
- **Zero transcript logging by default**: Standard diagnostic logs never contain recognized text or custom vocabulary entries; optional proofreading trace logs (off by default, for local quality evaluation) are saved only locally and never uploaded. Recent history lives in RAM only and vanishes when you quit.
- **Clear boundaries for AI proofreading**: Off by default. When enabled, **only the recognized text** (never audio) is sent to the endpoint you configure, and your API key is stored encrypted in the macOS Keychain or Windows DPAPI. Point it at a local LLM to stay 100% offline.

## FAQ

### Is VoiceTyper an input method (IME)?

No. VoiceTyper does not register as a system IME or alter how you type on your keyboard — it simply inserts the finished transcript at your active cursor when you finish speaking.

### Why does it use a few hundred MB of RAM, and can I reduce it?

By default, VoiceTyper keeps the speech model loaded in memory (~500 MB measured on macOS) so pressing the hotkey starts recording with zero delay. If you dictate only occasionally, disable preloading at launch or enable **unload after inactivity** in Settings — the model will load on demand while you speak.

### Where might automatic text insertion not work?

Almost anywhere you can paste works seamlessly. Password fields, certain fullscreen games, remote desktop clients, and elevated (Run as Administrator) windows on Windows may block simulated input; in those cases, your transcript stays on the clipboard and in recent history so you can paste manually.

### Can I add hotwords or swap out the speech model?

**Hotwords — yes**: Add frequently misrecognized names and terms in **Settings → Vocabulary**, processed entirely on-device (see the [macOS guide](macos/README.en.md#custom-vocabulary) and [Windows guide](windows/README.en.md#custom-vocabulary)).  
**Model switching — no**: The standalone desktop apps focus on making SenseVoice-Small fast and dependable out of the box rather than exposing uncalibrated model choices. If you need alternative models or remote GPU inference, use the [client–server edition](client-server/README.md).

### How mature is the Windows version?

The Windows app uses native system tray and WinForms controls, and core dictation and model downloading have been verified on real Windows hardware. Recent UX refinements, arm64 laptops, extended sessions, and multi-monitor DPI scaling are still undergoing systematic hardware acceptance testing. Please [open an issue](https://github.com/oyasmi/voice-typer/issues) if anything behaves unexpectedly.

## Contributing

```text
voice-typer/
├── macos/           # Standalone macOS app: Swift + AppKit/SwiftUI + ONNX Runtime
├── windows/         # Standalone Windows app: .NET 10 + WinForms + ONNX Runtime
└── client-server/   # Legacy client–server edition for Linux, Intel Macs & shared servers
```

Both desktop platforms share the same state-machine design and ASR pipeline, verified for numerical parity against shared golden fixtures. Use the root `Makefile` for common tasks:

```bash
make          # Build for the current host OS (macOS or Windows)
make test     # Run automated tests for the current platform
make help     # List all build, test, and clean targets
```

Developer & architecture docs (primarily in Chinese):

- **macOS**: [Development](macos/DEVELOPMENT.md) · [Architecture & Design](macos/DESIGN.md) · [Changelog](macos/CHANGELOG.md)
- **Windows**: [Development](windows/DEVELOPMENT.md) · [Architecture & Design](windows/DESIGN.md) · [Changelog](windows/CHANGELOG.md)
- **Client–server**: [Overview](client-server/README.md) · [Protocol](client-server/PROTOCOL.md)
- **Model evaluation**: [Fun-ASR-Nano Evaluation & Deferral Notes](FUN_ASR_NANO_EVALUATION.md)

Bug reports and feature ideas are welcome in [GitHub Issues](https://github.com/oyasmi/voice-typer/issues).

## Acknowledgements

On-device speech recognition is built on [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) and [ONNX Runtime](https://github.com/microsoft/onnxruntime). Thank you to their authors and maintainers for making fast, accurate offline speech recognition practical on everyday computers.

## License

Licensed under the [Apache License 2.0](LICENSE). Third-party components (including the SenseVoice model and ONNX Runtime) remain subject to their respective licenses.
