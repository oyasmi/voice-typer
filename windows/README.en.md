# VoiceTyper — unified Windows app

[简体中文](README.md) | **English**

[← Back to the project](../README.en.md) · [Design](DESIGN.md) · [Changelog](CHANGELOG.md) · [Review and repair plan](REVIEW_AND_REPAIR_PLAN.md) · [Split client](../client-server/client_windows_native/README.md)

A single-process Windows desktop app: the SenseVoice recognition pipeline from
[`client-server/server/`](../client-server/server/README.md) rewritten in C# and inlined into the
client. Install and use it — **no** separate Python server to deploy. Current version **3.5.1** (feature parity with macOS 3.5.1,
see the [changelog](CHANGELOG.md)), app name **VoiceTyper**.

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
- **Four-step first-run guide** (welcome → microphone → speech model → try it): the last step runs a
  real dictation inside the guide window, verifying the whole chain "hotkey → microphone → recognition
  → text insertion" and pointing at the exact link that fails
  (The microphone step lets you pick the input device and press "Test microphone" for a live level meter.)
- One-time model download on first launch (~240MB; the large file downloads in four parallel segments,
  resumes, and retries automatically with backoff; progress shows downloaded size, speed and time
  remaining), then works fully offline
- After lock, sleep/resume or a remote-session reconnect the keyboard hook is reinstalled and the input
  device refreshed right away, without waiting for the self-heal check
- Hold the hotkey (`Ctrl+F2` by default) to record; release to recognize and insert the text. You can
  also switch to "press once to start, again to stop", or use the **Right Ctrl** or **Right Alt** key
  alone as the hotkey (only a clean tap triggers it; holding it while pressing another key or clicking
  the mouse remains a normal shortcut)
- When not ready (model downloading / loading / failed), pressing the hotkey is no longer silent: the
  HUD says what is missing
- Live streaming preview: the HUD overlay keeps showing recognized text while you record (up to two
  lines, keeping the newest tail), and corrects itself; it shows a live waveform and the input device
  name, warns to check the microphone if nothing is heard after 1.5 s, and says so explicitly when
  nothing was recognized
- HUD position: bottom center / bottom right / follow cursor / hidden (errors still surface); scaled to
  the current screen's DPI. "Follow cursor" prefers the text insertion point you are typing at, and falls
  back to the mouse position when the app does not report one (Chrome, Electron and UWP draw their own
  text boxes)
- Bluetooth-headset friendly: when both the default input and the playback device are Bluetooth, the
  built-in microphone is used instead, so the headset is not forced into phone-call quality
- `Esc` cancels a dictation both while recording and while recognizing
- Optional LLM correction, configured right in the settings panel (base URL / API key / model /
  temperature / timeout). When enabled, recording start sends the correction service a key-less,
  text-less `HEAD /` request to open the connection ahead of time (at most once per 30 s), so the
  correction request after recognition reuses it
- Recognition language can be set to auto / Chinese / English / Cantonese / Japanese / Korean
- Interface language can be set to Chinese (default) or English; a restart applies it everywhere
- Automatically releases engine memory after an idle period and reloads it in parallel with your next
  recording
- Launch at login, adjustable HUD opacity; "Check for Updates..." in the tray menu queries the latest
  GitHub release on demand (no background network activity)
- Every dictation leaves one log line of timings containing only numbers and enums (never any
  recognized text) for diagnosing "why was that slow"
- **Both x64 and arm64** (including Snapdragon X series laptops)
- **Windows 10 and Windows 11**

**Not supported / known limits**

- Hotkey main keys are limited to letters, digits, `space`/`tab`/`enter`/`esc`, `F1`–`F12`, arrow keys
  and similar named keys (the settings page can "Record Hotkey" for you). A lone modifier key is
  supported for **Right Ctrl** and **Right Alt** — for Right Alt, the app injects a pair of no-op keys
  at the start of a clean tap so releasing it does not activate the window's menu bar; AltGr compose
  characters and Right Alt+Tab are unaffected. Left Alt (same menu-bar activation, much larger
  mis-touch surface), Win (opens the Start menu) and Shift (toggles Chinese/English in Chinese IMEs)
  remain unsupported, and a low-level keyboard hook cannot swallow modifier events
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
| Disk | About 300MB (app and downloaded model), plus the installed .NET Desktop Runtime |
| Runtime | .NET 10 Desktop Runtime matching the app's architecture; .NET is not bundled |
| Network | Only for the first model download; fully offline afterwards |
| .NET SDK | Only needed if you build it yourself (10.0+) |

---

## Installation

### From a release

Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for x64 or
arm64, matching the app. The regular .NET Runtime, ASP.NET Core Runtime, .NET 8/9, or a runtime for
another architecture is insufficient. The .NET 10 SDK already includes the Desktop Runtime.
The installer checks the runtime registered by Microsoft's installer and stops with download
instructions if it is missing; it does not bundle or download .NET.

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

A portable build is also provided: `VoiceTyper-<version>-win-x64-portable.zip`. Install the Desktop
Runtime described above, then extract the archive and run
`VoiceTyper.exe`; configuration and the model still live in the user directory, so it behaves exactly
like the installed version.
If the runtime is missing, the .NET app host displays installation instructions.

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
- Otherwise the Speech Model page of the settings window shows the model card; click “Download Model” to
  start. You get a progress bar, downloaded/total figures, and the ability to cancel. The four files
  (`config.yaml`, `am.mvn`, `tokens.json`, `model_quant.onnx`) come from ModelScope, each verified by
  sha256 and resumable (if the connection drops, clicking again continues where it stopped).

Failed downloads switch to Hugging Face / HF Mirror fallback addresses. Timeouts, interrupted transfers,
and HTTP 429/5xx responses are retried; HTTP 403 and TLS failures move to the next source without
repeating the same request. “Download details” provides redacted exception chains and error codes.
System TLS, certificate trust, and the default .NET proxy configuration remain in use.

The model lands in `%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\` — deliberately in the
**non-roaming** `LocalAppData` rather than `AppData\Roaming`: in a domain environment the roaming
profile follows the login, and stuffing a 240MB model into it would make domain logins slow.

---

## Usage

1. The VoiceTyper icon appears in the system tray after launch (Windows 11 tucks new icons under the “^”
   overflow by default — drag it out to keep it visible). The first launch opens the four-step guide.
2. **Hold the hotkey** (`Ctrl+F2` by default) to start recording; the HUD overlay appears on the screen
   containing the foreground window. It first shows a grey “Starting microphone…”, then turns red with
   “Recording” once the microphone is live — **start speaking when it turns red**; releasing before
   that tells you nothing was recorded. In toggle mode, press once to start and again to stop.
3. Speak. The HUD shows recognized text live and corrects itself as you continue; press `Esc` while
   recording or recognizing to cancel this dictation (cancelling during recognition does not interrupt
   the inference already running, but its result is discarded and never inserted).
4. **Release the hotkey**; the local engine re-recognizes the whole segment (with AI correction enabled,
   the HUD shows “Correcting…”) and the final text is inserted at the cursor.
5. A single recording is capped at **120 seconds**: on reaching the cap, the dictation ends and the
   text is inserted normally rather than silently dropped.

Recordings shorter than **0.3 seconds** are treated as accidental taps and discarded. Before inserting,
the app checks that the foreground window is still the one recording started in; if you switched
windows in the meantime, the result is only written to the clipboard and never inserted into an
unexpected window. Pressing the hotkey again while the previous dictation is still recognizing
shows “A dictation is still in progress” instead of stacking a new session; while it is waiting for AI correction,
pressing it again gives up waiting and inserts the raw recognition. If the hotkey includes Alt / Shift / Win and it is
still held when recognition finishes, the app waits up to about 0.4 s before pasting, and only then falls back to
copying to the clipboard.

Tray icon states:

| State | Meaning |
| --- | --- |
| Ready (app icon, no dot) | Waiting for the hotkey |
| Red dot | Recording |
| Yellow dot | Key released, waiting for the local engine / inserting text |
| Orange dot | The speech model needs to be, or is being, downloaded |
| Grey dot | Starting up / loading the model / paused |
| Dark red dot | Error |

Right-click the tray icon for the menu: Settings, **Pause/Resume dictation** (while paused the hotkey
does nothing until you resume from the menu), **Copy last transcription** (a sent paste keystroke does
not guarantee the target app accepted the text, so this is the last way to get it back; the text is kept
in memory only — never written to disk or logs — and dropped when the app exits), launch at login, Setup Guide (reopens the first-run guide),
Check for Updates, About, Quit.

---

## Settings

The settings window has five sidebar pages. Edits remain in a shared draft until you choose
“Save and Apply” or “Discard changes”. Closing preserves the draft and restores the saved overlay position and opacity (changing either shows a sample overlay for about two seconds).

| Page | Contents |
| --- | --- |
| **Dictation** | Record a shortcut (recording a common one such as Ctrl+C / Ctrl+V, Alt+F4 or Ctrl+Space shows a conflict warning, but is not blocked), Right Ctrl / Right Alt, activation mode, microphone with "Test microphone" (live level meter, 20 seconds at most, no audio is kept) and recognition language; manual shortcut editing is collapsed |
| **Speech Model** | Download, retry, cancel, reload, per-source diagnostics, preload and idle unload; preview tuning is collapsed |
| **Text correction** | Enable, service URL, encrypted API key, model name and test; advanced parameters are collapsed |
| **Appearance and general** | Overlay position and opacity, startup registration and interface language |
| **Diagnostics and help** | Microphone probe (tests the input device selected in Settings), Windows privacy settings, UIPI guidance, timings of the last 20 dictations with "Copy diagnostics" (no recognized text), log and configuration folders |

### Interface language

Chinese is the default. Switching to English on the Appearance and general page and saving writes the value
immediately, but the tray menu, the settings window and the overlay are all built with the wording
chosen at launch — so **restart VoiceTyper** to have every piece of text use the new language. The
interface language is independent of the recognition language on the Dictation page.

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
  idle_unload_minutes: 0     # 0 = stay resident (default, same as macOS)
  preload_on_launch: true    # load the model into memory at launch; false = load on the first hotkey press (in parallel with recording)
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
  key: "f2"                  # may also be "right_ctrl" / "right_alt" (the Right Ctrl / Right Alt key alone; leave modifiers empty)
  mode: "hold"               # hold = hold to talk; toggle = press once to start, again to stop
audio:
  input_device: "auto"       # auto = use the built-in mic during Bluetooth call mode; system = strictly follow the system default; or an audio endpoint ID
ui:
  opacity: 0.85
  hud_position: "bottom_center"  # bottom_center / bottom_right / near_cursor / hidden
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

Both architectures publish framework-dependent directories (`--self-contained false`), with
ReadyToRun enabled (precompiled code, less JIT before first use) and IL trimming disabled. Only the WASAPI audio modules are included; debug symbols
and native import libraries are excluded. Builds and CI run `scripts/verify_publish.ps1` to ensure
required files are present and reject bundled .NET runtimes, models, and test assemblies.
See the [package size audit](PACKAGE_SIZE_AUDIT.md) (Chinese) for measured sizes and manual checks.

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
| `LlmCorrectorTests` / `LlmThinkingTests` | Correction client fallbacks (network error / truncation / malformed response all return the original text), `tags-only` responses not losing text, `TestAsync` throwing the real error without the response body; deep thinking is turned off by default, and when a service rejects that field the request is resent once without it and the answer is cached per URL + model | No |
| `LlmEndpointTests` | Structured base URL parsing: scheme/host allowlist, plain HTTP limited to loopback and private networks, `/chat/completions` suffix de-duplication | No |
| `AudioChunkerTests` | Fixed-length framing, carry-over across calls, `Drain` tail, empty input | No |
| `VoiceTyperControllerTests` | The controller state machine: hold / toggle / lone-modifier triggers, the not-ready gate (including "the gate must not swallow the release of a dictation already in progress"), Esc cancel (while recording and while recognizing), silent discard of combo gestures, rejecting overlapping presses, empty recognition, insertion failure and elevation hints, recording start failure, silence probes, idempotent finishing | No |
| `ModifierOnlyHotkeyTests` / `HotkeyStateMachineTests` / `HotkeyRecordingTests` | The hotkey state machine (including Right Ctrl clean-tap detection, mouse invalidation, the Esc acceptance window) and the settings page's hotkey-recording rules | No |
| `AudioInputDeviceTests` | Input device selection policy and Bluetooth / USB / built-in endpoint classification | No |
| `ModelDownloaderTests` | Single connection / four parallel segments / falling back when the server ignores Range / segment resume / checksum failure / cancel (an in-memory Range server, no network) | No |
| `OnboardingModelTests` / `UpdateCheckerTests` / `DictationMetricsTests` / `HudTextLayoutTests` / `ConfigParityTests` | Guide flow and trial state, version parsing, timing summary (must never carry user text), HUD preview layout, round-trip and validation of the new config fields | No |
| `EndToEndRecognitionTests` | The whole pipeline (fbank → LFR/CMVN → real ONNX → CTC → post-processing) on the same real speech clip: edit distance ≤ 2 against the Python reference, and chunked preview flow converging to the same final text | Yes (skipped automatically when missing) |

xUnit 2.x has no built-in runtime skip; tests missing fixtures or a model use `Xunit.SkippableFact`
(`Skip.If`) so they show as “skipped” with the reason, rather than as a false pass.

---

## Logs and troubleshooting

Logs are written to `%LOCALAPPDATA%\VoiceTyper\logs\app.log` and roll over above 2MB (3 backups kept).

```bat
:: Follow live (PowerShell)
Get-Content "$env:LOCALAPPDATA\VoiceTyper\logs\app.log" -Wait -Tail 50
```

The “Diagnostics and help” page in Settings provides buttons for the log and config folders.

Each dictation ends with one `[metrics]` line containing only numbers and enums — never recognized text,
device names or window titles — for example:

```
dictation session=3fa1 outcome=inserted mode=hold hotkey=combo input=builtin capture_start=420 first_buffer=436
key_lag=0 dispatch=1 start_queue=0 dev_resolve=6 dev_cached=1 activate=31 init=380 hud_shown=18 hud_ready=452 audio=3.4s
release_to_finalize=8 engine_wait=0 final_wait=2 asr=310 llm=- llm_result=off llm_retry=- insert=24 mod_wait=0 backup_wait=0 release_to_done=352 previews=5 previews_skipped=1 preview_max=290 preview_avg=240 preview_abort=0 tail_speech=0 qos=1 cold=0
```

(It is a single line, wrapped here for layout; the numbers are illustrative.) A trailing `*` as in
`input=bluetooth*` means the "automatic" policy switched away from the system default input. The fields
from `key_lag` to `hud_ready` break down the time from pressing the hotkey to being able to speak; see
[DESIGN.md §18.3](DESIGN.md#183-新增耗时字段) for their meaning; the newer fields from `final_wait` on (final queueing, average preview latency, whether a preview was aborted, …) are in
[DESIGN.md §23.5](DESIGN.md#235-新增耗时字段). The "Diagnostics and help" page lists the last 20 dictations in a compact form.

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

- `Esc` cancels both while recording and while recognizing (cancelling during recognition does not
  interrupt the inference already running; it only discards its result). With no dictation in progress,
  `Esc` goes to the foreground app as usual.
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
