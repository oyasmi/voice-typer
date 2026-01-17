@echo off
REM VoiceTyper Go Client - Windows Build Script

setlocal enabledelayedexpansion

set VERSION=1.0.0
set APP_NAME=VoiceTyper
set BINARY_NAME=voicetyper.exe
set RELEASE_DIR=release

echo ==========================================
echo VoiceTyper Go Client - Build Script v%VERSION%
echo ==========================================
echo.

REM Check if Go is installed
where go >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Go compiler not found. Please install Go 1.21+
    exit /b 1
)

echo [INFO] Go version:
go version
echo.

REM Clean previous builds
echo [INFO] Cleaning previous builds...
if exist %RELEASE_DIR% rmdir /s /q %RELEASE_DIR%
if exist %BINARY_NAME% del /q %BINARY_NAME%

REM Build the application
echo [INFO] Building %APP_NAME% for Windows...
go build -ldflags="-s -w -X main.version=%VERSION%" -o %BINARY_NAME%

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed!
    exit /b 1
)

echo [INFO] Build completed successfully: %BINARY_NAME%

REM Display file size
for %%F in (%BINARY_NAME%) do echo [INFO] File size: %%~zF bytes

REM Create release directory
echo [INFO] Creating release package...
mkdir %RELEASE_DIR%
copy /Y %BINARY_NAME% %RELEASE_DIR%\ >nul

echo.
echo ==========================================
echo Build completed successfully!
echo Release package: %RELEASE_DIR%\%BINARY_NAME%
echo ==========================================

endlocal
