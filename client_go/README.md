# VoiceTyper for Windows

VoiceTyper is a voice-to-text input tool for Windows 10/11. It provides offline speech recognition by connecting to a local ASR server.

## Features

- **Global Hotkey**: Press `Ctrl+Space` to start/stop voice input
- **System Tray**: Runs in the background with a system tray icon
- **Auto-insertion**: Recognized text is automatically inserted at the cursor position
- **Custom Hotwords**: Support for custom vocabulary to improve recognition accuracy

## Requirements

- Windows 10/11 (64-bit)
- Go 1.21+ (for building)
- [FunASR Server](../../server/) running locally or remotely

## Building from Source

### Prerequisites

1. Install Go 1.21 or later from [golang.org](https://golang.org/dl/)
2. Open Command Prompt or PowerShell

### Build Steps

```batch
# Option 1: Using the build script (recommended)
build.bat

# Option 2: Using Go directly
go build -ldflags="-s -w" -o voicetyper.exe

# Option 3: Using Make (if you have MinGW/WSL)
make build
```

The compiled executable `voicetyper.exe` will be created in the current directory.

## Installation

1. Copy `voicetyper.exe` to a desired location (e.g., `C:\Program Files\VoiceTyper\`)
2. Run the executable - it will minimize to the system tray
3. Configure settings in `%APPDATA%\voice-typer\config.yaml`

### Auto-start on Windows Login

1. Press `Win+R`, type `shell:startup` and press Enter
2. Create a shortcut to `voicetyper.exe` in the Startup folder

## Configuration

Configuration file location: `%APPDATA%\voice-typer\config.yaml`

Default configuration (auto-created on first run):

```yaml
servers:
  - name: "local"
    host: "127.0.0.1"
    port: 6008
    timeout: 30.0
    api_key: ""
    llm_recorrect: false

hotkey:
  modifiers:
    - "ctrl"
  key: "space"

hotword_files:
  - "hotwords.txt"

ui:
  opacity: 0.85
  width: 240
  height: 70

input:
  method: "clipboard"
```

### Configuration Options

- `servers`: ASR server list (will try in order and use the first available)
- `hotkey`: Global hotkey combination (modifiers: ctrl, alt, shift)
- `hotword_files`: Custom vocabulary files (one word per line)
- `ui.opacity`: Recording indicator window transparency (0.0-1.0)
- `input.method`: Text input method (currently only "clipboard" supported)

## Custom Hotwords

Edit `%APPDATA%\voice-typer\hotwords.txt` to add custom vocabulary:

```
# One word per line
# Lines starting with # are comments

FunASR
Python
GitHub
OpenAI
ChatGPT
```

## Usage

1. Start VoiceTyper - it will appear in the system tray
2. Make sure the ASR server is running
3. Click on any text field (browser, editor, etc.)
4. Press `Ctrl+Space` to start recording
5. Speak clearly
6. Release `Ctrl+Space` to stop recording
7. Recognized text will be automatically inserted

## Troubleshooting

### Application won't start

- Check if the ASR server is running at the configured address
- Review the console output for error messages

### Hotkey not working

- Make sure VoiceTyper is running (check system tray)
- Try restarting the application
- Check if another application is using the same hotkey

### Text not being inserted

- Make sure you've clicked on a text field before recording
- Check if the clipboard is accessible
- Try manually pasting with `Ctrl+V` to verify the text was recognized

## Development

```bash
# Install dependencies
go mod download

# Run tests (if available)
go test ./...

# Format code
go fmt ./...

# Vet code
go vet ./...
```

## License

See [LICENSE](../../LICENSE) file in the root directory.

## Credits

- [FunASR](https://github.com/alibaba-damo-academy/FunASR) - Speech recognition toolkit
- [Fyne](https://fyne.io/) - Cross-platform GUI toolkit
- [robotgo](https://github.com/go-vgo/robotgo) - Robotgo library for keyboard/mouse control
- [gohook](https://github.com/robotn/gohook) - Global hotkey library
