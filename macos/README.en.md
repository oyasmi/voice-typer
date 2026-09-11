# VoiceTyper — unified macOS app

[简体中文](README.md) | **English**

[← Back to the project](../README.en.md) · [Design](DESIGN.md) · [Changelog](CHANGELOG.md) · [Split client](../client-server/client_macos_swift/README.md)

A single-process macOS menu bar app: the SenseVoice recognition pipeline from
[`client-server/server/`](../client-server/server/README.md) rewritten in Swift and inlined into the
client. Drag it into Applications and it works — **no** separate Python server to deploy. Current
version **3.4.0**, app name **VoiceTyper**, bundle ID `com.voicetyper.app`.

**Who this is for**: people who want to use it on their own Mac, and people who want to build it or
hack on it. Deeper architectural decisions, measured numbers and the trade-offs behind them are in
[`DESIGN.md`](DESIGN.md) (Chinese only).

---

## Contents

- [Features and limits](#features-and-limits)
- [Requirements](#requirements)
- [Installation](#installation)
- [Permissions and model download](#permissions-and-model-download)
- [Usage](#usage)
- [Settings](#settings)
- [Architecture](#architecture)
- [Building](#building)
- [Testing](#testing)
- [Logs and troubleshooting](#logs-and-troubleshooting)

---

## Features and limits

**Supported**

- Runs in one process; the recognition engine (SenseVoice-Small) runs inside the app and connects to
  no server
- Downloads and installs the model once on first launch (~240MB), then works fully offline
- Hold the hotkey (`Fn` by default) to record; release to recognize and insert the text. You can also
  switch to “press once to start, press again to stop”
- `Esc` cancels during recording *and* during recognition (after releasing, before the result lands),
  and nothing gets inserted
- A four-step guide on first launch (Welcome → Permissions → Model → Try it); the last step runs one
  real dictation to verify the whole chain
- Live streaming preview: the HUD overlay keeps showing recognized text while you record, and corrects
  itself
- Optional LLM proofreading, configured right in the settings panel (base URL / API key / model /
  temperature / timeout)
- Recognition language can be set to auto / Chinese / English / Cantonese / Japanese / Korean
- Interface language can be set to Chinese (default) or English; a restart applies it everywhere
- Optional preloading at launch (on by default) and automatic release of engine memory after an idle
  period
- The overlay shows the current input device while recording, and warns you if no sound is arriving
- Overlay position: bottom center / bottom right / follow cursor / hidden, with adjustable opacity
- Launch at login
- The menu bar can pause dictation, reopen the setup guide, and check for updates manually (there is no
  background check)

**Not supported / known limits**

- **Apple Silicon only** (arm64); Intel Macs are not supported
- Hotkey main keys are limited to letters, digits, `space`/`tab`/`enter`, `F1`–`F12`, and the standalone
  `fn`
- The app is not signed or notarized; the first launch needs a manual approval in System Settings →
  Privacy & Security
- SenseVoice-Small only — it cannot be switched to paraformer, and hotwords are removed in both forms
  (if you need paraformer, use the [split client](../client-server/client_macos_swift/README.md) plus
  the [server](../client-server/server/README.md))
- No remote or shared server — recognition always runs on this machine

---

## Requirements

| Item | Requirement |
| --- | --- |
| OS | macOS 14.0 (Sonoma) or later |
| Architecture | **Apple Silicon only** (M-series chips) |
| Disk | About 300MB (the app plus the model downloaded on first launch, with room to spare) |
| Network | Only for the first model download; fully offline afterwards |
| Xcode | Only needed if you build it yourself |

---

## Installation

### From a release

1. Download `VoiceTyper-<version>-macOS-arm64.dmg` from
   [Releases](https://github.com/oyasmi/voice-typer/releases).
2. Open the DMG and drag `VoiceTyper.app` into Applications.
3. Open VoiceTyper from Applications.
4. If macOS blocks the first launch, go to System Settings → Privacy & Security and click “Open
   Anyway”.

Step 4 is needed because the app is only ad-hoc signed — it has no Apple Developer signature and no
notarization. The DMG ships a short `INSTALL.txt` describing the same steps.

### Uninstalling

Delete `/Applications/VoiceTyper.app`, then the configuration and model directory
`~/Library/Application Support/VoiceTyper/`. The permission grants remain recorded in System Settings
and can be removed there.

### Can it coexist with the split client?

The bundle IDs (`com.voicetyper.app` vs `com.voicetyper.client`) and configuration directories are
separate, so technically yes. But both register the same default hotkey (`Fn`), so **running them at
the same time means fighting over it** — keep one. If you previously installed
[the VoiceTyperClient from `client-server/client_macos_swift/`](../client-server/client_macos_swift/README.md),
your hotkey and HUD opacity are inherited automatically on first launch.

---

## Permissions and model download

There are two independent preparation tracks on first launch; neither blocks the other:

1. **Three system permissions** (microphone, accessibility, input monitoring) — all three are required,
   and the setup guide walks you through them one by one (if you have been through the guide already,
   the Permissions tab of the settings window opens instead).
2. **Automatic model preparation** — the app downloads, verifies and loads the model in the background,
   and keeps going even when permissions are still missing. A new installation only needs you to grant
   permissions; once both tracks are ready, you can start dictating.

| Permission | Used for | What happens without it |
| --- | --- | --- |
| **Microphone** | Recording | Pressing the hotkey cannot start recording |
| **Accessibility** | Text insertion (direct AX writes + simulated paste) | Recognition succeeds but the text cannot be inserted |
| **Input Monitoring** | The global hotkey, especially the Fn key | The hotkey does nothing at all |

As long as Input Monitoring is granted, the hotkey keeps listening even when other permissions are
missing: pressing it tells you exactly what is still missing, instead of doing nothing.

> **An update may require granting permissions again**: ad-hoc signatures have no stable Team ID, and
> macOS keys permission records by code signature. The app detects whether it is an ad-hoc build and
> says so on the guide page and the Permissions tab; the notice disappears once it is distributed with
> a Developer ID signature.

The model download is visible both in the setup guide and on the Recognition tab: it shows a
percentage and can be canceled. Failures retry automatically three times with 5s / 20s / 60s backoff
(resume data is already on disk, so a retry continues rather than starting over); only after three
failures does it stop and wait for a manual retry. The four downloaded files (`config.yaml`, `am.mvn`,
`tokens.json`, `model_quant.onnx`) come from ModelScope, each verified by sha256 and resumable; the
241MB weights file is fetched in four parallel segments when the server supports range requests, and
falls back to a single connection when it does not. If this machine has run
[`client-server/server/`](../client-server/server/README.md) before (so `~/.cache/modelscope/` already
holds the model), the app reuses it and downloads nothing.

To reset a specific permission completely:

```bash
tccutil reset Microphone com.voicetyper.app
tccutil reset Accessibility com.voicetyper.app
tccutil reset ListenEvent com.voicetyper.app
```

---

## Usage

The **setup guide** opens automatically on first launch, in four steps: Welcome (including the warning
about Fn conflicting with a system setting) → three system permissions → speech model download →
**Try it**. The last step has you complete a real dictation in the guide's own text field — three green
permissions do not mean it works; only running the whole chain (hotkey → microphone → local
recognition → text insertion) does, and any broken link is named right there. You can reopen the guide
any time from the menu bar (“Setup Guide…”).

Everyday use:

1. The VoiceTyper icon appears in the menu bar after launch.
2. **Hold the hotkey** (`Fn` / globe key by default) to start recording; the HUD overlay appears.
   **Wait for the overlay before you speak** — there is an inherent ~0.2s delay between pressing the
   key and the microphone actually producing sound.
3. Speak. The HUD shows recognized text live and corrects itself as you continue.
4. **Release the hotkey**; the local engine re-recognizes the whole segment and the final text is
   inserted at the cursor. With proofreading enabled, the overlay shows “Proofreading…” once local
   recognition is done, so the two phases stay distinguishable.
5. Press **Esc** any time during recording or recognition to cancel; no text is inserted.

While recording, the overlay shows the current input device (for example “Recording · MacBook
Microphone”). If no sound arrives after recording starts, the overlay tells you to check the
microphone and input device — you do not have to finish talking to discover the result was empty.

Switch the trigger to “press once to start, press again to stop” (Settings → General → Trigger mode)
and step 2 becomes a single press, with another press to finish — handy for dictating emails or
documents.

Recordings shorter than **0.3 seconds** are treated as accidental taps and discarded.

### Fn and conflicting system behaviour

The default hotkey is `Fn`🌐, and VoiceTyper's event tap **does not swallow events** (swallowing them
would break normal typing). So if System Settings → Keyboard → “Press 🌐 key to” is not set to “Do
Nothing”, holding Fn to speak also triggers the system's own Fn action the moment you let go — the
emoji panel pops up, or your input source switches. The guide page and Settings → General detect this
and offer a button that opens the relevant system settings.

Menu bar icon states:

| State | Icon | Meaning |
| --- | --- | --- |
| Ready | `mic` | Waiting for the hotkey |
| Recording | `mic.fill` | Recording |
| Recognizing | `waveform` | Key released, waiting for the local engine |
| Inserting | `character.cursor.ibeam` | Inserting text |
| Model needed | Down arrow (orange) | First launch, model not downloaded yet |
| Downloading | Down arrow + animation | Downloading the model |
| Loading model | Rotating arrows | Permissions ready, loading the model into memory |
| Permissions needed | Warning triangle | Some permission is missing |
| Paused | `mic.slash` | Paused from the menu |

---

## Settings

The settings window has three tabs, all fully graphical — no YAML editing required:

| Tab | Contents |
| --- | --- |
| **Permissions** | The three permission states, grant buttons, shortcuts into System Settings |
| **Recognition** | Model status card (auto download / load / ready / failed, with retry and reload), idle-unload interval, preload at launch, recognition language, AI proofreading (toggle + base URL + API key + model + temperature + timeout + test) |
| **General** | Hotkey (Fn or a combination, with key recording), trigger mode (hold / toggle), Fn conflict detection, launch at login, **interface language**, overlay position and background opacity |

### Interface language

Chinese is the default. Switching to English on the General tab saves immediately, but the menu bar,
the settings window and the overlay are all built with the wording chosen at launch — so **restart
VoiceTyper** to have every piece of text use the new language. The interface language is independent
of the recognition language on the Recognition tab: you can run an English interface while dictating
in Chinese, or the other way around.

### Configuration file (internal storage, editing by hand is discouraged)

Every setting has a control in the settings window. `config.yaml` is the app's **internal storage**,
not a configuration entry point: the menu bar deliberately does **not** offer “Open config folder”, the
app does not watch the file while running (changes need a restart), and editing it by hand bypasses the
validation the UI does (for example “a non-Fn main key must have a modifier”). The fields are listed
here only so the file is readable while troubleshooting.

```
~/Library/Application Support/VoiceTyper/config.yaml
```

Full field list:

```yaml
asr:
  language: "auto"           # auto / zh / en / yue / ja / ko
  threads: 0                 # 0 = automatic (min(4, core count))
  model_dir: ""              # empty = locate automatically (download folder / ModelScope cache)
  idle_unload_minutes: 0     # 0 = stay resident (default; 5/10/30 minutes also possible)
  preload_on_launch: true    # load the model at launch; default true
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key is not here — it lives in the Keychain (service com.voicetyper.app, account llm_api_key)
hotkey:
  modifiers: []
  key: "fn"
  mode: "hold"               # hold = hold to talk; toggle = press once to start, again to stop
ui:
  opacity: 0.85
  hud_position: "bottom_center"   # bottom_center / bottom_right / near_cursor / hidden
  interface_language: "zh"        # zh / en; interface language, applied fully after a restart
```

For security reasons the LLM API key is not stored in the configuration file but in the system
Keychain. One-off application state such as “has the guide been completed” lives in `UserDefaults` and
likewise does not appear in the configuration file.

---

## Architecture

```
VoiceTyperController (state machine: Idle→Recording→Recognizing→Inserting)
  ├── HotkeyService / AudioCaptureService / TextInsertionService
  └── LocalASRSession (in-process recognition session, replacing the old cross-process WebSocket client)
         └── ASRService (serial asrQueue)
                └── SenseVoiceEngine
                       ├── FbankFrontend (Accelerate/vDSP, Kaldi-compatible fbank)
                       ├── LFRCMVN
                       ├── ORTSession (onnxruntime-swift-package-manager)
                       └── CTCDecoder + TextPostprocessor
```

The recognition pipeline (fbank → LFR/CMVN → CTC decoding) is a Swift port of
`client-server/server/voice_typer_server/recognizer.py`, kept numerically aligned point by point with
golden tests (see `Tests/VoiceTyperTests/Fixtures/`). The other modules are independent
implementations for the unified architecture and no longer share code or a protocol with the old split
client.

The full design decisions, measurements (performance, memory, model I/O contract) and module
responsibilities are in [`DESIGN.md`](DESIGN.md) (Chinese only).

---

## Building

### Xcode

```bash
cd macos
ruby scripts/generate_xcodeproj.rb   # (re)generate the project; required after adding files
open VoiceTyper.xcodeproj
```

Dependencies: [Yams](https://github.com/jpsim/Yams) (YAML parsing) and
[onnxruntime-swift-package-manager](https://github.com/microsoft/onnxruntime-swift-package-manager)
(pinned exactly to `1.24.2`), both through SwiftPM.

### Command line

```bash
cd macos
./build_xcode.sh
```

Only the **arm64** variant is produced (Intel Macs are not supported). Artifacts land in `dist/`:

```
VoiceTyper-<version>-macOS-arm64.zip / .dmg
```

The model is not bundled with the build; the app downloads and installs it on first launch. To stage it
offline for testing or to skip the automatic download:

```bash
./scripts/fetch_model.sh
```

### Signing and notarization (optional)

By default (no environment variables set) the build is **ad-hoc signed on this machine**, which is the
path almost every contributor should use. Ad-hoc signing has two known costs:

- **Every update may require the user to grant the three permissions again** — ad-hoc signatures have
  no stable Team ID, the cdhash changes on every build, and TCC records are keyed by code signing
  identity.
- It cannot be distributed through notarization, so users must approve the first launch manually
  (“Open Anyway”) in System Settings.

With a paid Apple Developer account you can enable **Developer ID signing + hardened runtime +
notarization** through environment variables:

```bash
# 1. List the Developer ID identities available on this machine
security find-identity -v -p codesigning

# 2. (Once) store notarization credentials in the local Keychain
xcrun notarytool store-credentials "voicetyper-notary" \
  --apple-id "you@example.com" --team-id "TEAMID1234" --password "app-specific password"

# 3. Sign + notarize + staple in one run
VOICETYPER_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID1234)" \
VOICETYPER_NOTARY_PROFILE="voicetyper-notary" \
./build_xcode.sh
```

Setting only `VOICETYPER_SIGN_IDENTITY` (without `VOICETYPER_NOTARY_PROFILE`) performs Developer ID
signing without submitting for notarization — useful for verifying the signature locally first.

Implementation notes (see the comments in `build_xcode.sh`): the embedded `onnxruntime.framework` is
signed separately with the same identity (hardened runtime, `--options runtime`) instead of a blanket
`--deep` pass — that is Apple's documented recommendation and one of the most common causes of
notarization failures. The project has been verified to build, run and pass all unit tests (including
real ONNX inference) with `ENABLE_HARDENED_RUNTIME=YES`, but **the Developer ID signing and
notarization flow itself has not been verified on real hardware** — this repository's automated
environment has no paid developer account or usable credentials, so a maintainer who has one needs to
run it once and update this section.

Automatic updates (Sparkle and friends) are out of scope for now: a certificate solves “reinstall and
it just works”, but continuous update checks and delivery need extra release infrastructure (appcast
hosting, signing key management), which is left until distribution needs demand it.

### Changing the version number

`MARKETING_VERSION` in `scripts/generate_xcodeproj.rb` (in two places: `build_configuration_list` and
the per-target configuration).

---

## Testing

```bash
xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper -destination 'platform=macOS' test
```

| Test | Covers |
| --- | --- |
| `FbankParityTests` | fbank / LFR+CMVN features compared point by point against the golden output from `client-server/server/` (tolerance 1e-3), including the log floor regression for all-zero input |
| `EndToEndRecognitionTests` | The full recognition chain against real speech samples (edit-distance tolerance, see below) |
| `LFRCMVNTests` | LFR tail-padding branch and the per-element CMVN affine transform (pure logic, no model needed) |
| `TextPostprocessorTests` | Each rule of the post-CTC text cleanup |
| `RecognitionBufferTests` | Sliding-window preview scheduling (fake engine, no real model) |
| `AudioChunkerTests` | Fixed-length framing, carry-over across calls, tail flushing (pure logic, no microphone) |
| `LocalASRSessionTests` | Session behaviour: preview re-entry skipping, finalize watchdog, the 120-second cap warning and wrap-up, suppressing late callbacks after close |
| `ASRServiceTests` | Engine load mutual exclusion, idle unload not re-triggering a preload, language changes during loading replayed onto the new engine |
| `VoiceTyperControllerTests` | State machine behaviour: overlapping presses rejected instead of overwriting, Esc cancellation, clean wrap-up after a device change, short recordings discarded, and so on |
| `AppCoordinatorReadinessTests` | Readiness priority (paused > missing permissions > model download > engine state) |
| `ConfigStoreTests` | YAML round-trip, missing fields falling back to defaults |
| `ConfigMigratorTests` | Legacy config migration inheriting only the hotkey and HUD opacity |
| `AppConfigTests` | Config validation: clamping out-of-range values, resetting NaN/Inf, falling back for a bare non-fn key |
| `LocalizationTests` | Bilingual coverage: every `L(...)` / `LF(...)` in the sources has an English translation, placeholders match, and lookups fall back to Chinese |
| `LLMCorrectorTests` | Proofreading client fallbacks (network error / truncation / malformed response all return the original text) |
| `LLMEndpointTests` | Structured parsing of the proofreading base URL (scheme/host allowlist, plain HTTP limited to loopback and private networks, `/chat/completions` suffix de-duplication) |
| `ModelDownloaderTests` | sha256 verification, file manifest consistency, download order and the single automatic trigger policy |
| `TextInsertionServiceTests` | AX insertion range validity (negative, out-of-bounds and overflowing ranges are all rejected) |

Apart from the first two golden-fixture groups, all of these are pure logic unit tests requiring
neither a model nor the network.

`FbankParityTests` / `EndToEndRecognitionTests` need a model present on this machine (any
`ModelLocator` priority is fine) and `XCTSkip` automatically when it is missing. Fixtures are generated
by `scripts/dump_reference_fixtures.py` (which needs the Python environment of
`client-server/server/`).

`EndToEndRecognitionTests` accepts results by edit distance rather than byte equality: Python and macOS
use two independently compiled ONNX Runtime binaries, and on genuinely ambiguous tokens the different
floating-point summation order can flip the result (English word capitalization, for example). That is
benign cross-platform floating-point non-determinism, not a logic bug. See §8 of
[`DESIGN.md`](DESIGN.md) for the measured conclusion.

---

## Logs and troubleshooting

Logging goes through the unified logging system (`os.Logger`) rather than a separate `.log` file. The
subsystem is `com.voicetyper.app`, with the categories `app` / `permissions` / `hotkey` / `audio` /
`asr` / `llm` / `model`.

**Command line** (`log` is a built-in tool):

```bash
# Follow live
log stream --predicate 'subsystem == "com.voicetyper.app"' --level debug
log stream --predicate 'subsystem == "com.voicetyper.app" AND category == "asr"'

# Look at the last 10 minutes
log show --predicate 'subsystem == "com.voicetyper.app"' --last 10m
```

**GUI**: open Console (`/Applications/Utilities/`), select this Mac in the sidebar, and filter on
`com.voicetyper.app`; you can narrow further by category (`llm`, `hotkey`, …).

Note that log messages themselves stay in Chinese: they are a diagnostic channel for developers, not
part of the user interface.

### The model download fails

Check that ModelScope is reachable. Downloads resume, so retry from the Recognition tab. If it keeps
failing, use `scripts/fetch_model.sh` to fetch the files manually into the default model directory.

### Recognition succeeds but the inserted text is empty / the overlay says “Nothing recognized”

The hotkey and the recognition chain work, but that recording contained no recognizable speech. Check
whether the microphone is muted, whether the right device is selected under System Settings → Sound →
Input, and whether another app has exclusive use of the microphone. The device name shown in the
overlay while recording is the device actually in use, so you can confirm it directly.

### The hotkey does nothing at all

See [the corresponding section in the split client documentation](../client-server/client_macos_swift/README.md#热键完全没反应)
— the `CGEventTap` implementation is identical and the diagnosis is the same (substitute this app's
bundle ID, `com.voicetyper.app`).

### Recognition succeeds but the text is not inserted

Likewise, see [the split client documentation](../client-server/client_macos_swift/README.md#识别成功但文字没插进去);
the text insertion implementation was carried over unchanged and behaves identically.

---

## Related links

- [VoiceTyper main project](../README.en.md)
- [Design document](DESIGN.md) (Chinese only)
- [Split client (for sharing one server between devices)](../client-server/client_macos_swift/README.md)
- [Server](../client-server/server/README.md) (not used by this app, but the recognition pipeline was ported from it)
