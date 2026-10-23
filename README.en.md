# VoiceTyper

[简体中文](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/oyasmi/voice-typer)](https://github.com/oyasmi/voice-typer/releases)
[![License](https://img.shields.io/github/license/oyasmi/voice-typer)](LICENSE)
![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-lightgrey)

**Hold a hotkey, speak, release — the text appears at your cursor.** Recognition runs on your own
computer; recordings never leave it.

VoiceTyper sits quietly in the menu bar or system tray. Reply to a message, write a document, capture an
idea the moment it shows up — no web page to open, no recording to upload, no copying and pasting between a
transcript and the window you're working in. It doesn't replace your input method and works anywhere you
can type.

## Download

| Platform | Requirements | Default hotkey | |
| --- | --- | --- | --- |
| **macOS** | Apple Silicon, macOS 14+ | `Fn` | [Download](https://github.com/oyasmi/voice-typer/releases) · [Guide](macos/README.en.md) |
| **Windows** | Windows 10 (1809+) / 11, x64 or arm64, [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | Right `Ctrl` | [Download](https://github.com/oyasmi/voice-typer/releases) · [Guide](windows/README.en.md) |
| Linux / Intel Mac | No standalone app yet | — | Use the [client–server version](client-server/README.md) |

## Highlights

- **Works everywhere**: chat apps, browsers, documents, editors — text goes wherever the cursor is.
- **See it as you speak**: the overlay shows live results and corrects earlier words as it hears more
  context.
- **Ready to send**: automatic punctuation, spoken numbers written as digits, and spacing between Chinese
  and English words.
- **Local, offline, free**: on-device recognition with the SenseVoice model; no account, no usage limits.
- **Multilingual**: Chinese, English, Cantonese, Japanese and Korean, with automatic detection; mixed
  Chinese and English needs no switching.
- **Stays out of the way**: never steals focus; press `Esc` to cancel at any time; recover what you said
  from the menu.
- **Knows your words**: add names, product names and terms that keep getting misrecognized to a custom
  vocabulary, and recognition leans toward those spellings — all on your machine.
- **Optional AI proofreading**: connect any OpenAI-compatible service to fix homophones and slips of the
  tongue. Off by default.

## Getting started

1. Download and install the app for your platform (see the platform guide).
2. On first launch, a four-step guide gets you ready: on macOS you grant microphone, Input Monitoring and
   Accessibility permissions; on Windows you just confirm the microphone works. The ~240MB speech model
   downloads automatically in the background, once. The guide ends with a real test dictation.
3. From then on:
   - put the cursor where you want the text;
   - hold the hotkey and speak, watching live results in the overlay;
   - release the hotkey, and the text is typed in.

Presses shorter than 0.3 seconds are ignored as accidental. For long passages, switch the trigger to
“press once to start, press again to stop”.
On Windows, Right Ctrl is recommended, with Ctrl + Tab as a second preset. The tray menu’s
“Recent transcriptions” shows the last 20 results, with individual copying or copying all in speaking
order, separated by newlines.

## Privacy

Speech often carries more private information than the text you end up writing, so local recognition is a
design premise of VoiceTyper, not an add-on.

- Audio is processed in memory on your computer and never uploaded anywhere;
- once the model is downloaded, it works fully offline;
- logs never contain recognized text;
- AI proofreading is off by default. When enabled, only the **recognized text** is sent to the service you
  configured — never audio — and the API key is kept in the macOS Keychain or Windows DPAPI-encrypted
  storage. To stay fully offline, point it at a model running on your own machine.

## FAQ

### Is it an input method (IME)?

No. VoiceTyper doesn't replace your input method; it just writes the recognized text into the focused
field, so your typing habits stay the same.

### Does it need the internet every time?

No. It only goes online to download the model once, when you check for updates, and when you turn on AI
proofreading.

### Does it use a lot of memory?

By default the model loads at launch and stays resident so the hotkey responds instantly, at the cost of a
few hundred MB (about 500MB measured on macOS). If you dictate rarely, turn off preloading or release the
model after inactivity in Settings; it then loads while you record.

### Where doesn't it work?

It works almost anywhere you can paste. Some password fields, games, remote desktops and windows running
as administrator reject simulated input; the result then stays on the clipboard and can be recovered from
the menu, so you can paste it manually.

### Why does the system block the first launch?

The packages aren't notarized by Apple or code-signed for Windows yet. On macOS, go to System Settings →
Privacy & Security and click “Open Anyway”; on Windows, click “More info → Run anyway”. You only need to do
this once.

### Can I switch models or add hotwords?

You can add hotwords: list the words that keep getting misrecognized in Settings → Vocabulary; the macOS and
Windows apps work the same way (see the [macOS guide](macos/README.en.md#custom-vocabulary) and the
[Windows guide](windows/README.en.md#custom-vocabulary)). The standalone apps focus on SenseVoice-Small and do not
support switching models — a clear, dependable default matters more than a pile of unpolished options. For other
models, use the [client–server version](client-server/README.md).

### Is the Windows version stable?

It has feature parity with macOS, and dictation and model download have been run on real Windows
hardware. arm64 devices, long-running sessions and multi-monitor DPI are still being tested
systematically. Please [open an issue](https://github.com/oyasmi/voice-typer/issues) if you hit a problem.

## Contributing

```text
voice-typer/
├── macos/           # macOS app: Swift + AppKit/SwiftUI + ONNX Runtime
├── windows/         # Windows app: .NET 10 + WinForms + ONNX Runtime
└── client-server/   # Legacy client–server architecture for Linux, remote and shared-server setups
```

Both platforms share the same product architecture and recognition pipeline, checked for numerical
consistency against one set of golden fixtures. From the repository root, `make` builds for the host OS
(falling back to macOS when it can't tell); `make macos`, `make windows`, `make test` and `make clean` are
explicit targets, and `make help` lists them all. Build, test and architecture docs (in Chinese):

- macOS: [Development](macos/DEVELOPMENT.md) · [Design](macos/DESIGN.md) · [Changelog](macos/CHANGELOG.md)
- Windows: [Development](windows/DEVELOPMENT.md) · [Design](windows/DESIGN.md) · [Changelog](windows/CHANGELOG.md)
- Client–server: [Overview](client-server/README.md) · [Protocol](client-server/PROTOCOL.md)

Bug reports and suggestions are welcome in [Issues](https://github.com/oyasmi/voice-typer/issues).

## Acknowledgements

Local recognition is built on [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) and
[ONNX Runtime](https://github.com/microsoft/onnxruntime). Thanks to these projects for bringing
high-quality offline speech recognition to ordinary desktop apps.

## License

[Apache License 2.0](LICENSE). Third-party components (the SenseVoice model, ONNX Runtime, etc.) remain
under their own licenses.
