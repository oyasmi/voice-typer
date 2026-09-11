# VoiceTyper — unified Windows app

[简体中文](README.md) | **English**

[← Back to the project](../README.en.md) · [Design](DESIGN.md) · [Review and repair plan](REVIEW_AND_REPAIR_PLAN.md) · [Split client](../client-server/client_windows_native/README.md)

A single-process Windows desktop app: the SenseVoice recognition pipeline from
[`client-server/server/`](../client-server/server/README.md) rewritten in C# and inlined into the
client. Install and use it — **no** separate Python server to deploy. Current version **3.2.1**, app
name **VoiceTyper**.

**Who this is for**: people who want to use it on their own Windows PC, and people who want to build it
or hack on it. Deeper architectural decisions and measurements (⚠️ some still to be re-verified on real
hardware) are in [`DESIGN.md`](DESIGN.md) (Chinese only).

---

## Contents

- [Features and limits](#features-and-limits)
- [Requirements](#requirements)
- [Installation](#installation)
- [Model download](#model-download)
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
- Guides you through a one-time model download on first launch (~240MB), then works fully offline
- Hold the hotkey (`Ctrl+F2` by default) to record; release to recognize and insert the text
- Live streaming preview: the HUD overlay keeps showing recognized text while you record, and corrects
  itself
- Optional LLM correction, configured right in the settings panel (base URL / API key / model /
  temperature / timeout)
- Recognition language can be set to auto / Chinese / English / Cantonese / Japanese / Korean
- Interface language can be set to Chinese (default) or English; a restart applies it everywhere
- Automatically releases engine memory after an idle period and reloads it in parallel with your next
  recording
- Launch at login, adjustable HUD opacity
- **Both x64 and arm64** (including Snapdragon X series laptops)
- **Windows 10 and Windows 11**

**Not supported / known limits**

- Hold-to-talk only; there is no “press once to start, press again to stop” mode. `Esc` cancels while
  recording
- Hotkey main keys are limited to letters, digits, `space`/`tab`/`enter`/`esc`, `F1`–`F12`, arrow keys
  and similar named keys
- Official releases are not code-signed, so SmartScreen may block the first run — click “More info →
  Run anyway”. Signing is available when you build it yourself, see
  [Building → Signing](#signing-optional)
- SenseVoice-Small only — it cannot be switched to paraformer, and hotwords are removed in both forms
  (if you need paraformer, use the [split client](../client-server/client_windows_native/README.md)
  plus the [server](../client-server/server/README.md))
- No remote or shared server — recognition always runs on this machine
- **UIPI limitation**: windows running as administrator (Notepad, terminals, …) do not accept inserted
  text. That is a Windows security mechanism, not a recognition failure. The result is still written to
  the clipboard so you can press `Ctrl+V`
- No DirectML / NPU acceleration (see [`DESIGN.md`](DESIGN.md) §4.2 for the reasoning); CPU inference
  only

---

## Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows 10 (1809+) or Windows 11 |
| Architecture | x64 or arm64 |
| Disk | About 300MB (the app plus the model downloaded on first launch) |
| Runtime | No separate .NET install — the package is self-contained |
| Network | Only for the first model download; fully offline afterwards |
| .NET SDK | Only needed if you build it yourself (10.0+) |

---

## Installation

### From a release

1. Download the installer for your architecture from
   [Releases](https://github.com/oyasmi/voice-typer/releases):
   `VoiceTyper-<version>-win-x64-setup.exe` (most PCs) or
   `VoiceTyper-<version>-win-arm64-setup.exe` (Snapdragon X and other ARM laptops).
2. Run it. It installs into the current user's directory
   (`%LOCALAPPDATA%\Programs\VoiceTyper`) and does **not** raise a UAC prompt.
3. If “Windows protected your PC” appears: click “More info” → “Run anyway”. This is because the
   installer is not code-signed (an EV certificate is expensive and this project does not buy one yet);
   it does not affect functionality.
4. Open VoiceTyper from the Start menu after installation, or tick “Launch VoiceTyper” to run it right
   away.

A portable build is also provided: `VoiceTyper-<version>-win-x64-portable.zip`. Extract it and run
`VoiceTyper.exe`; configuration and the model still live in the user directory, so it behaves exactly
like the installed version.

### Uninstalling

Settings → Apps → find VoiceTyper → Uninstall; or “Uninstall VoiceTyper” in the Start menu's VoiceTyper
group. Uninstalling removes the installation directory but **not** your configuration and model
(`%APPDATA%\VoiceTyper\`, `%LOCALAPPDATA%\VoiceTyper\`), which you can delete by hand.

### Can it coexist with the split client?

The configuration directories (`%APPDATA%\VoiceTyper\` vs `%APPDATA%\voice_typer\`) are separate, so
technically yes. But both register `Ctrl+F2` by default, so **running them at the same time means
fighting over the hotkey** — keep one. If you previously installed
[the VoiceTyperClient from `client-server/client_windows_native/`](../client-server/client_windows_native/README.md),
your hotkey and HUD opacity are inherited automatically on first launch.

---

## Model download

On first launch the app checks whether a SenseVoice-Small model is already present:

- If this machine has run [`client-server/server/`](../client-server/server/README.md) before (so
  `%USERPROFILE%\.cache\modelscope\` already holds the model), the app reuses it — **zero download**.
- Otherwise the Recognition tab of the settings window shows the model card; click “Download Model” to
  start. You get a progress bar, downloaded/total figures, and the ability to cancel. The four files
  (`config.yaml`, `am.mvn`, `tokens.json`, `model_quant.onnx`) come from ModelScope, each verified by
  sha256 and resumable (if the connection drops, clicking again continues where it stopped).

The model lands in `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\` — deliberately in the
**non-roaming** `LocalAppData` rather than `AppData\Roaming`: in a domain environment the roaming
profile follows the login, and stuffing a 240MB model into it would make domain logins slow.

---

## Usage

1. The VoiceTyper icon appears in the system tray after launch.
2. **Hold the hotkey** (`Ctrl+F2` by default) to start recording; the HUD overlay appears on the screen
   containing the foreground window.
3. Speak. The HUD shows recognized text live and corrects itself as you continue; press `Esc` while
   recording to cancel this dictation.
4. **Release the hotkey**; the local engine re-recognizes the whole segment and the final text is
   inserted at the cursor.
5. A single recording is capped at **120 seconds**: on reaching the cap, the dictation ends and the
   text is inserted normally rather than silently dropped.

Recordings shorter than **0.3 seconds** are treated as accidental taps and discarded. Before inserting,
the app checks that the foreground window is still the one recording started in; if you switched
windows in the meantime, the result is only written to the clipboard and never inserted into an
unexpected window.

Tray icon states:

| State | Meaning |
| --- | --- |
| Ready (microphone icon, no dot) | Waiting for the hotkey |
| Red dot | Recording |
| Yellow dot | Key released, waiting for the local engine / inserting text |
| Orange dot | The speech model needs to be, or is being, downloaded |
| Grey dot | Starting up / loading the model / paused |
| Dark red dot | Error |

Right-click the tray icon for the menu: Settings, **Pause/Resume dictation** (while paused the hotkey
does nothing until you resume from the menu), open the config folder, launch at login, About, Quit.

---

## Settings

The settings window has four tabs, all fully graphical — no YAML editing required:

| Tab | Contents |
| --- | --- |
| **Recognition** | Model status card (download / load / ready / failed, with reload), recognition language, AI correction (toggle + base URL + API key + model + temperature + max tokens + timeout + test) |
| **Hotkey** | Modifier combination (Ctrl/Alt/Shift/Win) plus main key, with a preview |
| **Permissions** | Microphone availability check, shortcut to the Windows privacy settings, explanation of the UIPI limit |
| **General** | Launch at login, HUD background opacity, idle-unload interval, preview window (advanced, 0 = calibrated automatically from this machine's performance), **interface language** |

### Interface language

Chinese is the default. Switching to English on the General tab and saving writes the value
immediately, but the tray menu, the settings window and the overlay are all built with the wording
chosen at launch — so **restart VoiceTyper** to have every piece of text use the new language. The
interface language is independent of the recognition language on the Recognition tab.

Configuration file:

```
%APPDATA%\VoiceTyper\config.yaml
```

Full field list:

```yaml
asr:
  language: "auto"           # auto / zh / en / yue / ja / ko
  threads: 0                 # 0 = automatic (min(4, core count))
  model_dir: ""              # empty = locate automatically (download folder / ModelScope cache)
  preview_window: 0          # seconds; 0 = calibrated automatically after the first load
  idle_unload_minutes: 10    # 0 = stay resident (same default as macOS)
llm:
  enabled: false
  base_url: ""
  model: "gpt-4o-mini"
  temperature: 0
  max_tokens: 800
  timeout: 5
  # api_key is not here — see below
hotkey:
  modifiers: ["ctrl"]
  key: "f2"
ui:
  opacity: 0.85
  interface_language: "zh"   # zh / en; interface language, applied fully after a restart
```

For security reasons the LLM API key is not stored in the configuration file: it is encrypted with
Windows DPAPI (`ProtectedData`, current-user scope) into `%APPDATA%\VoiceTyper\llm_api_key.dat`. Moving
to another user or machine means entering it again in the settings page; if the file is corrupt or
cannot be decrypted, the settings page says so explicitly instead of letting AI correction silently 401
forever.

Out-of-range or invalid values edited into `config.yaml` by hand (`timeout: -1`, a `hotkey.key` with no
modifier, …) are clamped back into range on the next load or save with a warning in the log — never
silently accepted, and never a reason to reject the whole file.

---

## Architecture

```
VoiceTyperController (state machine: Idle→Recording→Recognizing→Inserting, shared with the split client)
  ├── HotkeyService / AudioCaptureService / TextInsertionService (carried over unchanged)
  └── LocalAsrSession ── same interface as the old StreamingASRClient (WebSocket)
         └── AsrService (dedicated serial AsrPump thread)
                └── SenseVoiceEngine
                       ├── FbankFrontend (hand-written 512-point radix-2 FFT, Kaldi-compatible fbank)
                       ├── LfrCmvn
                       ├── InferenceSession (Microsoft.ML.OnnxRuntime, CPU EP)
                       └── CtcDecoder + TextPostprocessor
```

The core design principle is **not inventing a new state machine**: `VoiceTyperController` shares the
same state machine as the split client, with the network client replaced by a local session exposing
the same interface. The recognition pipeline (fbank → LFR/CMVN → CTC decoding) is a **direct
translation of the Swift implementation in [`macos/`](../macos/)** rather than a fresh port from
Python — both platforms' fbank implementations share the same Python golden fixtures, and the Windows
tests in `Tests/VoiceTyper.Tests/` link directly to the reference data committed under
`macos/Tests/VoiceTyperTests/Fixtures/`.

Full design decisions, measurements (⚠️ the parts marked as estimates need re-verification on real
hardware) and module responsibilities are in [`DESIGN.md`](DESIGN.md) (Chinese only).

---

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- (Optional, needed to build the installer) [Inno Setup](https://jrsoftware.org/isdl.php), with
  `ISCC.exe` on PATH

### Development

```bat
cd windows
dotnet run
```

### Building everything from the command line

```bat
cd windows
build.bat
```

Produces `dist/`:

```
VoiceTyper-<version>-win-x64-setup.exe        installer (recommended, per-user, no UAC prompt)
VoiceTyper-<version>-win-x64-portable.zip      portable build
VoiceTyper-<version>-win-arm64-setup.exe
VoiceTyper-<version>-win-arm64-portable.zip
```

Without Inno Setup installed the script skips the installer step and produces only the portable zips.

The model is not bundled with the build; the app guides you through downloading it on first launch. To
stage it offline for testing or to skip the download guide:

```bat
powershell -ExecutionPolicy Bypass -File scripts\fetch_model.ps1
```

### Changing the version number

`<Version>` / `<AssemblyVersion>` / `<FileVersion>` in `VoiceTyper.csproj`.

### Signing (optional)

Without the signing environment variables, `build.bat` behaves exactly as before (unsigned artifacts).
To sign, import the certificate into the machine's certificate store and set:

```bat
set VOICETYPER_SIGN_THUMBPRINT=<certificate SHA1 thumbprint>
set VOICETYPER_TIMESTAMP_URL=http://timestamp.digicert.com
build.bat
```

`build.bat` then uses `signtool.exe` to sign `dist\<rid>\VoiceTyper.exe` and the Inno Setup installer in
turn. Unsigned executables trigger the SmartScreen “unknown publisher” warning — the same class of
problem as an un-notarized Gatekeeper build on macOS, and the optional Developer ID signing and
notarization in `macos/build_xcode.sh` is its symmetric counterpart.

---

## Testing

```bat
cd windows
dotnet test
```

| Test | Covers | Needs the model? |
| --- | --- | --- |
| `FftTests` | The hand-written FFT compared against a naive DFT | No |
| `FbankParityTests` | fbank / LFR+CMVN features compared point by point against the golden output from `client-server/server/` (tolerance 1e-3, reusing the fixtures committed under `macos/`), plus the log floor regression for all-zero input | The LFR/CMVN part does (skipped automatically when missing) |
| `TextPostprocessorTests` | Each rule of the post-CTC text cleanup | No |
| `RecognitionBufferTests` | Sliding-window preview scheduling (fake engine, no real model) | No |
| `ConfigStoreTests` | Config model and YAML round-trip (never touches the real `%APPDATA%`) | No |
| `AppConfigValidationTests` | Clamping out-of-range fields, resetting non-finite floats, falling back for a bare hotkey | No |
| `LocalizationTests` | Bilingual coverage: every `L10n.T(...)` / `L10n.F(...)` in the sources has an English translation, placeholders match, lookups fall back to Chinese, and bootstrap reads `interface_language` | No |
| `LlmCorrectorTests` | Correction client fallbacks (network error / truncation / malformed response all return the original text), `tags-only` responses not losing text, `TestAsync` throwing the real error without the response body | No |
| `LlmEndpointTests` | Structured base URL parsing: scheme/host allowlist, plain HTTP limited to loopback and private networks, `/chat/completions` suffix de-duplication | No |
| `AudioChunkerTests` | Fixed-length framing, carry-over across calls, `Drain` tail, empty input | No |

xUnit 2.x has no clean runtime skip API like macOS's `XCTSkip`; tests missing fixtures or a model
return early instead. The effect is equivalent (they do not block the rest), but they show as “passed”
rather than “skipped” — a known presentation-level difference.

---

## Logs and troubleshooting

Logs are written to `%APPDATA%\VoiceTyper\logs\app.log` and roll over above 2MB (3 backups kept).

```bat
:: Follow live (PowerShell)
Get-Content "$env:APPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50
```

The “Open config folder” menu item takes you straight to the folder containing the logs.

Note that log messages themselves stay in Chinese: they are a diagnostic channel for developers, not
part of the user interface.

### The model download fails

Check that ModelScope is reachable. Downloads resume, so clicking “Download Model” again continues from
where it stopped. If it keeps failing, fetch the files manually with `scripts\fetch_model.ps1`, or point
`asr.model_dir` in `config.yaml` at an existing model folder.

### The hotkey does nothing at all

- Make sure no other program has taken the same combination.
- With some administrator-level windows in the foreground (Task Manager, some security software), a
  low-level keyboard hook from a non-elevated process may be blocked by the system — try switching to
  an ordinary window.
- Check the `hotkey` category in `logs\app.log` to confirm that `SetWindowsHookExW` succeeded.

### Recognition succeeds but the text is not inserted

- Check whether the target window runs as administrator — that is the known Windows UIPI limitation
  (see Features and limits above). The text is already on the clipboard; `Ctrl+V` works.
- If the HUD or tray says the target window changed: the foreground window changed between the start of
  recording and the end of recognition. To avoid inserting into an unexpected window (a password field
  in the worst case), VoiceTyper only writes the result to the clipboard.
- Otherwise, see
  [the corresponding section in the split client documentation](../client-server/client_windows_native/README.md);
  the text insertion implementation was carried over unchanged and behaves identically.

### Esc does nothing while recording / the hotkey stops working after pausing

- `Esc` only applies while **recording** (red dot); once you release the hotkey and recognition starts,
  it no longer cancels that dictation.
- “Pause dictation” in the tray menu stops hotkey listening entirely; you have to click “Resume
  dictation” for the hotkey to respond again. That is intentional, not a fault.

### The global hotkey suddenly stops working, with no error

If a `WH_KEYBOARD_LL` low-level keyboard hook callback takes too long to return, the system may silently
remove the hook without telling the application. VoiceTyper's callback only makes a key decision,
dispatches asynchronously and returns immediately, so this should not happen; there is also a health
timer independent of the hook instance that reinstalls it with exponential backoff when the handle is
lost or callbacks stop arriving, and shows “hotkey listening has failed” in the tray and settings if
reinstallation keeps failing. The `hotkey` log category records all of this. This path has passed code
review but has **not** been verified against a real unhooking event on hardware; if you suspect it,
restarting the app restores service immediately.

---

## Related links

- [VoiceTyper main project](../README.en.md)
- [Design document](DESIGN.md) (Chinese only)
- [Split client (for sharing one server between devices)](../client-server/client_windows_native/README.md)
- [Server](../client-server/server/README.md) (not used by this app, but the recognition pipeline was ported from it)
- [Unified macOS app](../macos/README.en.md) (the sister implementation this one was translated from)
