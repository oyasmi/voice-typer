# Windows Build Instructions

This document provides detailed instructions for building VoiceTyper on Windows.

## Prerequisites

1. **Install Go 1.21+**
   - Download from https://golang.org/dl/
   - Install with default options
   - Verify installation: `go version`

2. **Git (optional, for cloning)**
   - Download from https://git-scm.com/download/win

## Build Methods

### Method 1: Using build.bat (Recommended)

```batch
cd client_go
build.bat
```

This will:
- Clean previous builds
- Build `voicetyper.exe`
- Create `release/` directory with the executable

### Method 2: Using Go Directly

```batch
cd client_go
go build -ldflags="-s -w" -o voicetyper.exe
```

The `-ldflags="-s -w"` reduces the executable size by:
- `-s`: Omit symbol table and debug information
- `-w`: Omit DWARF symbol table

### Method 3: Using Make (requires MinGW/WSL)

```batch
cd client_go
make build
```

## Output

After successful build:
- `voicetyper.exe` - Main executable (~10-15 MB)
- `release/voicetyper.exe` - Copy for distribution

## Common Issues

### Issue: "go: command not found"

**Solution**: Make sure Go is installed and in your PATH
- Restart Command Prompt/PowerShell after installation
- Check: `where go`

### Issue: Build fails with CGO errors

**Solution**: Install GCC (MinGW-w64)
- Download from https://winlibs.com/ or
- Install via MSYS2: `pacman -S mingw-w64-x86_64-gcc`

### Issue: Missing DLLs

**Solution**: The executable should be self-contained. If you see DLL errors:
- Make sure you're building on 64-bit Windows
- Try rebuilding with `go clean -cache && go build`

## Testing

```batch
# Run the application
voicetyper.exe

# Check if it works:
# 1. Application should minimize to system tray
# 2. Press Ctrl+Space to test recording (requires ASR server)
# 3. Check configuration in %APPDATA%\voice-typer\config.yaml
```

## Distribution

To create a distributable package:

```batch
# Create a zip file
powershell Compress-Archive -Path release\voicetyper.exe -DestinationPath voicetyper-windows-x64.zip

# Or manually create a folder with:
# - voicetyper.exe
# - README.md
# - (Optional) A shortcut to the executable
```

## Advanced Build Options

### With Version Info

```batch
go build -ldflags="-s -w -X main.version=1.0.0" -o voicetyper.exe
```

### With Custom Icon

```batch
# Requires go-winres installed
go install github.com/tc-hib/go-winres@latest
go-winres init
go-winres icon=assets/icon.ico
go-winres version-info
go build -ldflags="-s -w" -o voicetyper.exe
```

### Reduce Size Further with UPX

```batch
# Download UPX from https://upx.github.io/
upx --best --lzma voicetyper.exe
```

This can reduce the size by 40-60%.
