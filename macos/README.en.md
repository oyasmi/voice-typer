# VoiceTyper for macOS

[简体中文](README.md) | **English**

[← Project home](../README.en.md) · [Download](https://github.com/oyasmi/voice-typer/releases) · [Changelog](CHANGELOG.md) · [Development](DEVELOPMENT.md)

VoiceTyper is a menu bar app: hold `Fn` in any app and speak, release it, and the text appears at the
cursor. Speech recognition runs entirely on your Mac; recordings are never uploaded.

## Requirements

| Item | Requirement |
| --- | --- |
| Chip | Apple Silicon (M-series). Intel Macs are not supported |
| System | macOS 14 Sonoma or later |
| Disk | About 300MB (app + speech model downloaded on first launch) |
| Network | Only for the first model download |

## Installation

1. Download `VoiceTyper-<version>-macOS-arm64.dmg` from [Releases](https://github.com/oyasmi/voice-typer/releases).
2. Open the DMG and drag `VoiceTyper.app` into Applications.
3. Open VoiceTyper. If macOS says the developer cannot be verified, go to System Settings → Privacy &
   Security and click “Open Anyway”.

> The app is not notarized by Apple yet, so the first launch needs this one-time approval. It does not
> affect how the app works.

## First launch

A four-step guide opens: **Welcome → Permissions → Speech model → Try it**.

**Grant three permissions.** The app cannot work without all of them; the guide takes you to each one:

| Permission | Used for | Without it |
| --- | --- | --- |
| Microphone | Recording | Pressing the hotkey cannot start recording |
| Input Monitoring | Responding to the hotkey in other apps | The hotkey does nothing at all |
| Accessibility | Writing text at the cursor | Recognition works but no text appears |

**The speech model** (about 240MB) downloads and verifies itself in the background while you grant
permissions. Downloads resume after interruptions and retry automatically.

**Try it**: complete one real dictation inside the guide window. Only a successful run proves that the
hotkey, microphone, recognition and text insertion all work; if one of them fails, the guide tells you
which. You can reopen the guide any time from the menu bar (“Setup Guide…”).

## Everyday use

1. Put the cursor where you want the text.
2. Hold `Fn` and start speaking **once the overlay appears**. The overlay shows live results and corrects
   earlier words as you keep talking.
3. Release `Fn`. The final, punctuated text is written into the current app.

Useful details:

- **Cancel**: press `Esc` while recording or recognizing; nothing is written.
- **Recover results**: “Recent Results” in the menu bar keeps the last 10 (including cancelled ones and
  ones that failed to insert). Click to copy. Kept in memory only and cleared when you quit.
- **Long dictation**: switch the trigger to “press once to start, press again to stop” in Settings.
- **Length**: up to 120 seconds per dictation, with a countdown for the last 15 seconds. Presses shorter
  than 0.3 seconds are ignored as accidental.
- **Mixed Chinese and English**: no switching needed; a space is inserted between Chinese and English words.
- **Password fields**: dictation does not start while a password field has focus.

The menu bar also lets you switch recognition language, microphone and AI proofreading, or pause dictation.

## Settings

Everything is configurable in the Settings window; there is no config file to edit.

| Page | Contents |
| --- | --- |
| Permissions | Status of the three permissions and shortcuts to grant them |
| Recognition | Model status, recognition language (auto / Chinese / English / Cantonese / Japanese / Korean), memory, AI proofreading |
| Vocabulary | Custom vocabulary (names, product names, terms) and recognition bias strength |
| General | Hotkey, trigger mode, microphone, overlay position and opacity, launch at login, interface language |

- **Hotkey**: `Fn`, a key combination (such as `⌥Space`), or a single Right ⌘ / Right ⌥ / Left ⌥ / Right ⌃
  (handy on external keyboards without Fn). With a single modifier, holding it and pressing another key
  still works as a normal shortcut.
- **Microphone**: “Auto” by default — when you wear Bluetooth headphones, the built-in microphone is used
  instead, so the headphones don't drop into low-quality call mode and pause your music. You can also
  always follow the system default input.
- **Memory**: by default the model loads at launch and stays resident, so the hotkey responds instantly.
  If memory is tight, turn off preloading or release the model after a period of inactivity; it then
  loads while you record.
- **Interface language**: Chinese or English, applied after restarting the app. It is independent of the
  recognition language.

### Custom vocabulary

Names, product names, project code names and terms that keep getting misrecognized can be listed in
Settings → Vocabulary. Everything runs on your Mac; nothing goes online.

- **Spelling**: the correct spelling, such as `VoiceTyper`, `Claude` or `张三丰`.
- **Sounds like** (optional): what it tends to come out as, e.g. `克劳德` for `Claude`. Variants that differ only
  in spaces, hyphens or letter case (`voice typer`, `Voice-Typer`) don't need to be listed — they are normalized
  to your spelling automatically.
- **Recognition bias**: Off / Light / Standard / Strong, Standard by default. Stronger settings recognize listed
  words more readily, but are also more likely to turn similar-sounding ordinary words into them; words that sound
  clearly different are never changed. If you see unwanted changes, go down one level.
- **Add from Recent Results**: when you spot a misrecognition, select the wrong part in a recent result and type
  the correct spelling.
- **Status column**: ✓ means the entry takes part in recognition bias; ⚠ means it is a single character (or a single
  English word piece) — such entries are only used for replacement.

Up to 200 entries. Without any vocabulary, recognition results are exactly the same as before. When AI proofreading
is on, the list is also given to the proofreading service.

### AI proofreading (optional, off by default)

Connect any OpenAI-compatible service (including a model running on your own machine) to fix homophones,
slips of the tongue and punctuation. When enabled, only the **recognized text** is sent to the service you
configured — never audio. The API key is stored in the macOS Keychain.

Press the hotkey again during proofreading to skip it and insert the original text. If proofreading fails
(timeout, authentication error, …), the original text is used and the overlay explains why.

## Privacy

- Audio is processed in memory only — never written to disk, never uploaded.
- Once the model is downloaded, VoiceTyper works fully offline.
- No account, no usage limits.
- Logs never contain recognized text or your custom vocabulary; Recent Results live in memory and are cleared on quit.

## FAQ

### Pressing Fn opens the emoji picker or switches input source?

In System Settings → Keyboard, set “Press 🌐 key to” to “Do Nothing”. The guide and Settings → General
detect this conflict and offer a shortcut. Alternatively, choose a different hotkey.

### The hotkey does nothing?

Check Input Monitoring first. After updating VoiceTyper, macOS may treat the old permission as invalid
(a known issue with non-notarized builds): remove VoiceTyper from the list in System Settings, add it
again, then fully quit and restart the app. Also, when an app turns on Secure Input (e.g. Terminal's
Secure Keyboard Entry or a password field), macOS blocks combination hotkeys; the menu bar tells you so.

### Recognition works, but the text doesn't appear?

Check Accessibility. A few apps refuse external input; the text is then on the clipboard or in Recent
Results, ready to paste.

### The overlay says nothing was recognized?

While recording, the overlay shows which microphone is in use. Make sure it isn't muted, that System
Settings → Sound → Input has the right device, and that no other app holds it exclusively. The overlay also
warns you when no sound is coming in.

### The model download fails?

Make sure you can reach ModelScope, then retry in Settings → Recognition; the download resumes where it
stopped. If this Mac has run the VoiceTyper server before, its model cache is reused automatically.

### Can it coexist with the older split client?

Yes, but both default to `Fn` and will fight over the hotkey if run together, so keep only one. When
migrating, your hotkey and overlay opacity carry over automatically.

### Known limitations

Apple Silicon only; only the SenseVoice-Small model, with no model switching or hotwords; recognition
always runs locally, with no remote server option (use the
[client–server version](../client-server/README.md) for that).

## Uninstall

Delete `VoiceTyper.app` from Applications and the folder `~/Library/Application Support/VoiceTyper/`
(settings and model). To clear permission records, remove VoiceTyper from the corresponding lists in
System Settings → Privacy & Security.

## Reporting issues

Open an [issue](https://github.com/oyasmi/voice-typer/issues) with what happened, your macOS version and
Mac model. Logs help: open the Console app and search for `com.voicetyper.app`. Logs never contain
recognized text.

## Building from source

```bash
cd macos
ruby scripts/generate_xcodeproj.rb
./build_xcode.sh
```

Signing, tests and log fields are covered in [DEVELOPMENT.md](DEVELOPMENT.md); architecture and design
decisions in [DESIGN.md](DESIGN.md). Both are in Chinese.
