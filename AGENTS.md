# VoiceTyper - Agent Instructions

## Project Overview

VoiceTyper is a multi-platform speech-to-text application using FunASR (Alibaba's speech recognition toolkit). It follows a client-server architecture with HTTP/JSON communication.

**Architecture:**
- **Server** (`server/`): Python Tornado-based ASR service with FunASR models
- **Clients**: macOS (`client_macos/`), Linux (`client_linux/`), Windows (`client_windows/`), Go rewrite planned (`client_go/`)

## Build Commands

### Server (Python)
```bash
cd server
pip install -r requirements.txt
./run.sh                    # Start server (default: 127.0.0.1:6008)
./run.sh --device cpu       # Default runtime device
./run.sh --help             # View all options
```

### macOS Client
```bash
cd client_macos
make install    # Install dependencies
make run        # Development mode
make build      # Build .app bundle
make dist       # Create release zip
make log        # View logs (~/.config/voice_typer/app.log)
```

### Linux Client (Wayland)
```bash
cd client_linux
make install      # Install Python deps
make install-udev # Setup input device permissions
make run          # Start application
make check-deps   # Verify system dependencies
```

### Windows Client
```bash
cd client_windows
pip install -r requirements.txt
python main.py
pyinstaller voicetyper.spec  # Build .exe
```

## Testing

**No formal test suite exists.** Manual testing workflow:
1. Start server: `cd server && ./run.sh`
2. Start client in dev mode: `cd client_<platform> && make run` (or `python main.py`)
3. Press and hold the hotkey (macOS default: Fn), speak, then release to test recognition
4. Check logs: `make log` (macOS/Linux) or console output

## Code Style Guidelines

### Python

**Imports:**
- Group: stdlib → third-party → local modules
- Use `from typing import Optional, List, Callable` for type hints
- Example:
```python
import os
import sys
import logging
from typing import Optional, List

import numpy as np
import tornado.web

from config import AppConfig
```

**Formatting:**
- 4-space indentation
- Line length: ~100 characters (soft limit)
- Use double quotes for strings, single quotes acceptable for dict keys

**Type Hints:**
- Use type hints on function signatures and class attributes
- Use `Optional[Type]` for nullable values
- Use `@dataclass` for configuration classes
- Example:
```python
class VoiceTyperController:
    def __init__(self, config: AppConfig):
        self._asr_client: Optional[ASRClient] = None

    def get_stats_display(self) -> str:
        ...
```

**Naming Conventions:**
- `snake_case` for functions, variables, modules
- `PascalCase` for classes
- `_prefix` for private methods/attributes
- `UPPER_CASE` for module-level constants (e.g., `APP_NAME = "VoiceTyper"`)

**Error Handling:**
- Log errors with `logger.error()` or `logger.warning()`
- Use try/except with specific exception types
- For async/threading: catch exceptions and update status via callbacks
- Never suppress exceptions silently

**Logging:**
- Use module-level logger: `logger = logging.getLogger("VoiceTyper")`
- Log format: `%(asctime)s - %(levelname)s - %(message)s` (server)
- Log user-facing messages via controller callbacks for UI display

**Documentation:**
- Module docstrings at top of file
- Class and method docstrings for public APIs
- Comments in **Chinese** (project convention)
- Example:
```python
"""
核心控制器
协调录音、识别、文本插入等组件
"""
```

**State Management:**
- Use `threading.Lock()` for thread safety in hotkey/audio handling
- Controller pattern: central coordinator for components
- Observer pattern: callbacks for status updates (`on_status_change`)

**Configuration:**
- YAML config at `~/.config/voice_typer/config.yaml`
- Use dataclasses with defaults
- Environment-aware paths (APPDATA on Windows, ~/.config on Unix)

**Platform-Specific Code:**
- macOS: `rumps` for menu bar, `PyObjC` for native UI
- Linux: `evdev` for hotkeys, GTK4 for indicator, `wl-clipboard` for paste
- Windows: `pystray` for system tray, `pynput` for input

### Go (client_go/)

- Planned rewrite, currently design phase only
- Target: Single binary, cross-platform, fast startup
- See `client_go/DESIGN.md` for architecture plans

## Key Implementation Patterns

**Audio Flow:**
1. Hotkey press → Start recording (16kHz, float32)
2. Hotkey release → Stop, send to server via HTTP POST
3. Server: FunASR → Punctuation → Optional LLM correction
4. Client: Insert via clipboard + Ctrl+V simulation

**Text Insertion:**
- macOS: `pbcopy` + `pynput.keyboard.Controller`
- Linux: `wl-copy` + `uinput` for keyboard simulation
- Windows: `pyperclip` + `pynput`

**Version Management:**
- Keep version in sync across:
  - `client_<platform>/Makefile`: `VERSION = x.x.x`
  - `client_<platform>/config.py`: `APP_VERSION = "x.x.x"`
  - `voicetyper.spec` (PyInstaller): `CFBundleVersion`

## Important Notes

- **Never use `as any` or `@ts-ignore`** (not applicable but good discipline)
- Server uses single-threaded executor (`max_workers=1`) to prevent GPU memory issues
- Short recordings (<0.3s) are filtered as accidental triggers
- LLM correction is optional; gracefully degrade if unavailable
