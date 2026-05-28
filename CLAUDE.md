# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VoiceTyper is a **cross-platform offline speech-to-text input tool** built on FunASR (Alibaba's speech recognition toolkit). It uses a **client-server architecture**: a per-platform desktop client captures audio and inserts recognized text, while a local (or shared) ASR server runs the speech models.

There are three clients, each in its own directory:

| Platform | Directory | Stack | Status |
| --- | --- | --- | --- |
| macOS | `client_macos_swift/` | Swift + AppKit | Native, recommended |
| Windows | `client_windows_native/` | .NET 8 + WinForms | Native, recommended |
| Linux | `client_linux/` | Python + GTK4 + evdev (Wayland) | Maintained |

> The two native clients (macOS Swift, Windows .NET) share the same state machine and a **streaming-first** architecture. The Linux client is Python and runs in non-streaming (HTTP) mode.

### Architecture

```
┌─────────────────────┐   WebSocket (streaming, default)   ┌───────────────────┐
│   Platform Client   │  ───────────────────────────────▶ │   ASR Server      │
│                     │   or HTTP POST (--no-streaming)    │   (FunASR/ONNX)   │
│  - Hotkey listener  │  ◀──────────────────────────────  │                   │
│  - Audio recording  │              JSON / partials       │  - Speech model   │
│  - Status HUD / UI  │                                    │  - Punctuation    │
│  - Text insertion   │                                    │  - Hotwords       │
└─────────────────────┘                                    │  - LLM correction │
                                                           └───────────────────┘
```

**Key Design Patterns:**
- **Single controller state machine**: each client funnels all state into one controller — `Idle → Recording → Recognizing → Inserting`.
- **Service decomposition**: hotkey, audio capture, ASR client, and text insertion are independent services.
- **Streaming dual-channel** (native clients): a partial preview channel feeds the HUD live while a final offline re-recognition produces the inserted text.
- **Client-Server**: separate processes over the network (default: `127.0.0.1:6008`).

### Technology Stack

**macOS client (`client_macos_swift/`):** Swift, `AppKit` (menu bar + HUD + setup window), `AVAudioEngine`/`AVAudioConverter` (16kHz/float32/mono capture), `CGEventTap` (hotkeys incl. Fn), Accessibility + clipboard text injection.

**Windows client (`client_windows_native/`):** .NET 8, WinForms, `NAudio` (WASAPI capture), `YamlDotNet` (config). Hotkeys via `SetWindowsHookEx(WH_KEYBOARD_LL)`, text injection via clipboard + `SendInput`.

**Linux client (`client_linux/`):** Python 3, `PyGObject`/GTK4 (indicator), `evdev` (global hotkey, requires udev rule), `wl-clipboard` (`wl-copy`) for text insertion, `sounddevice` capture.

**Server (`server/`):** `onnxruntime`-based FunASR runtime, `tornado` web/WebSocket server, OpenAI-compatible LLM client for optional correction, `PyYAML`.

## Common Commands

### Server (from `server/` directory)

```bash
# Install + run via helper script (default: 127.0.0.1:6008)
./scripts/voice_typer_server.sh setup
./scripts/voice_typer_server.sh run

# Streaming is the default. For Linux / non-streaming clients:
./scripts/voice_typer_server.sh run --no-streaming

# Run the installed package directly
voice-typer-server --help

# Start with LLM correction
voice-typer-server --llm-base-url https://api.openai.com/v1 \
                   --llm-api-key sk-xxx \
                   --llm-model gpt-4o-mini
```

**Server Options:**
- `--host HOST` - Listen address (default: 127.0.0.1)
- `--port PORT` - Listen port (default: 6008)
- `--no-streaming` - Disable WebSocket streaming, serve HTTP `/recognize` only
- `--model MODEL` - ASR model (default: paraformer-zh)
- `--punc-model M` - Punctuation model (default: ct-punc, "none" to disable)
- `--device DEVICE` - Computing device (cpu, cuda, cuda:N)
- `--api-keys K` - API keys (comma-separated)
- `--llm-base-url URL` / `--llm-api-key KEY` / `--llm-model MODEL` - LLM correction
- `--llm-temperature T` / `--llm-max-tokens N` - LLM tuning

### macOS client (from `client_macos_swift/`)

```bash
open VoiceTyper.xcodeproj      # develop in Xcode
./build_xcode.sh               # CLI build → dist/VoiceTyper.app + .zip + .dmg
```

### Windows client (from `client_windows_native/`)

```bat
dotnet restore     REM restore dependencies
dotnet run         REM debug run
build.bat          REM publish portable + self-contained .exe into dist/
```

### Linux client (from `client_linux/`)

```bash
make install        # install Python deps
make install-udev   # install /etc/udev rule for evdev hotkey access
make run            # run in development mode
make log            # view logs (also make log-f to follow)
```

## Code Structure

### Native clients (macOS Swift / Windows .NET)

Both share the same layout (`.swift` / `.cs`):

- `App/` — `AppCoordinator` (central wiring, lifecycle, state-machine callbacks) and the platform entry point.
- `Core/` — `AppConfig` (config model), `AppState` (state enum + display info), `ConfigStore` (YAML read/write + hotword management), `VoiceTyperController` (core state machine + streaming/non-streaming dual path). macOS adds `PermissionCenter` for first-launch permission flow.
- `Services/` — `HotkeyService`, `AudioCaptureService` (16kHz/mono/float32), `StreamingASRClient` (WebSocket), `ASRClient` (HTTP), `TextInsertionService`.
- `UI/` — status-bar/tray controller, `RecordingHUD` (live status + streaming preview), `SetupForm`/`SetupWindowController` (connection, hotkey, hotword settings).
- `Support/` — logging, constants, native interop (P/Invoke on Windows).

### Linux client (`client_linux/`)

- `main.py` - entry point, GTK app loop
- `controller.py` - state machine controller
- `hotkey_listener.py` - global hotkey via `evdev`
- `recorder.py` - audio capture (16kHz float32)
- `asr_client.py` - HTTP client (non-streaming)
- `text_inserter.py` - clipboard insertion via `wl-copy`
- `indicator.py` - GTK4 status indicator
- `config.py` - YAML config management
- `99-voicetyper-input.rules` - udev rule for evdev access

### Server (`server/`)

- `asr_server.py` - Tornado entry point: `GET /health`, `POST /recognize` (HTTP), WebSocket streaming endpoint
- `recognizer.py` - FunASR/ONNX model wrapper
- `auth.py` - API authentication (`X-API-Key` / `Authorization` header)
- `llm_client.py` - OpenAI-compatible LLM client for text correction
- `scripts/voice_typer_server.sh` - setup/run helper

## Configuration

**Client config location:**
- macOS / Linux: `~/.config/voice_typer/config.yaml`
- Windows: `%APPDATA%\voice_typer\config.yaml`

The format is shared across all clients (so server/hotword config migrates between them):

```yaml
server:
  scheme: "http"         # http / https; ws/wss is derived automatically
  host: "127.0.0.1"
  port: 6008
  timeout: 60
  api_key: ""
  llm_recorrect: false   # enable LLM correction (requires server LLM config)
  streaming: true        # true = WebSocket streaming (native clients); false = HTTP non-streaming
hotkey:
  modifiers: []          # e.g. ["ctrl"]; native macOS supports "fn"
  key: "fn"              # macOS default fn; Windows default f2 with ctrl
hotword_files:
  - "hotwords.txt"
ui:
  opacity: 0.85
  width: 240
  height: 70
```

Hotword file lives next to the config (`hotwords.txt`); one word per line, `#` starts a comment. Hotwords only take effect in non-streaming mode.

**Server configuration:** command-line arguments only (see "Server Options").

## Key Implementation Details

### Audio Flow
1. User presses and holds the hotkey.
2. Client captures audio at 16kHz / float32 / mono.
3. **Streaming (default, native clients):** raw frames are pushed over WebSocket; partial text is shown live in the HUD. On key release, the server runs an offline re-recognition pass for the accurate final text.
4. **Non-streaming (Linux, or `--no-streaming`):** the full clip is sent via HTTP `POST /recognize` on key release.
5. Server pipeline: ASR → punctuation restoration → optional LLM correction.
6. Final text is inserted at the cursor.

> **Short-recording filter:** clients discard sessions shorter than **300ms** before any network call — see `PROTOCOL.md` §5.1 and `VoiceTyperController.minimumRecordingDuration` on macOS. This is a client-side convention; the server does not enforce it.

> **Wire protocol:** the canonical client↔server contract (rules, frames, partial-is-delta semantics, scheme handling) lives in [`PROTOCOL.md`](PROTOCOL.md). Update it whenever changing wire behavior on either side.

### Text Insertion
- **macOS:** prefer Accessibility direct write (`AXValue`/`AXSelectedTextRange`, no clipboard pollution); fall back to backing up the pasteboard, writing text, simulating `Cmd+V`, then restoring.
- **Windows:** clipboard + `SendInput` `Ctrl+V`.
- **Linux:** `wl-copy` + paste.

### Hotkeys
- **macOS:** `CGEventTap` (supports the Fn / globe key); Carbon `RegisterEventHotKey` planned for standard combos.
- **Windows:** low-level keyboard hook `SetWindowsHookEx(WH_KEYBOARD_LL)`; default `Ctrl+F2`.
- **Linux:** `evdev` device monitoring (needs the udev rule); default `Ctrl+F2`.

### macOS Permissions Required
- **Microphone** - audio recording
- **Accessibility** - text insertion / system interaction
- **Input Monitoring** - global hotkey, especially the Fn key

The Swift client checks all of these plus server connectivity on first launch and opens a setup window if anything is missing.

## Version Management

The server version lives in the Python package metadata (`server/`). Each native client tracks its own version constant (`Support/Constants`) and bundle metadata (`Info.plist` on macOS, `.csproj` on Windows).
