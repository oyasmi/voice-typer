@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

echo ========================================
echo VoiceTyper Windows Native build script
echo ========================================
echo.

REM ===== Check .NET SDK =====
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found
    echo Please install .NET 8 SDK:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo .NET SDK: %DOTNET_VER%

REM ===== Read version from csproj =====
set VERSION=
for /f "tokens=*" %%a in ('dotnet msbuild VoiceTyper.csproj -getProperty:Version -nologo 2^>nul') do set VERSION=%%a
if "%VERSION%"=="" set VERSION=3.0.0

echo Version:  %VERSION%
echo Target:   win-x64
echo.

REM ===== Clean previous build =====
if exist dist rd /s /q dist
if exist bin rd /s /q bin
if exist obj rd /s /q obj
mkdir dist 2>nul

REM ===== [0/3] Restore =====
echo [0/3] Restoring NuGet packages...
dotnet restore VoiceTyper.csproj --nologo -v q
if errorlevel 1 (
    echo [ERROR] NuGet restore failed.
    pause
    exit /b 1
)
echo       OK
echo.

REM ===== [1/3] Build =====
echo [1/3] Building...
dotnet build VoiceTyper.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)
echo       OK
echo.

REM ===== [2/3] Portable (framework-dependent) =====
echo [2/3] Publishing portable build (framework-dependent, ~3MB)...
dotnet publish VoiceTyper.csproj -c Release -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -o dist\_portable ^
    --nologo -v q
if errorlevel 1 (
    echo [ERROR] Portable publish failed.
    pause
    exit /b 1
)
echo       OK
echo.

REM ===== [3/3] Self-contained =====
echo [3/3] Publishing self-contained build (compressed)...
dotnet publish VoiceTyper.csproj -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o dist\_standalone ^
    --nologo -v q
if errorlevel 1 (
    echo [ERROR] Self-contained publish failed.
    pause
    exit /b 1
)
echo       OK
echo.

REM ===== Rename / collect artifacts =====
set PORTABLE_NAME=VoiceTyper-%VERSION%-win-x64-portable.exe
set STANDALONE_NAME=VoiceTyper-%VERSION%-win-x64.exe

move "dist\_portable\VoiceTyper.exe" "dist\%PORTABLE_NAME%" >nul 2>&1
move "dist\_standalone\VoiceTyper.exe" "dist\%STANDALONE_NAME%" >nul 2>&1

if exist "Assets\icon.ico" (
    mkdir "dist\Assets" 2>nul
    copy "Assets\icon.ico" "dist\Assets\icon.ico" >nul 2>&1
)

rd /s /q dist\_portable 2>nul
rd /s /q dist\_standalone 2>nul

REM ===== Summary =====
echo ========================================
echo  Build complete!
echo ========================================
echo.

set PORTABLE_SIZE=?
set STANDALONE_SIZE=?

if exist "dist\%PORTABLE_NAME%" (
    for /f %%s in ('powershell -command "[math]::Round((Get-Item 'dist\%PORTABLE_NAME%').Length / 1MB, 1)"') do set PORTABLE_SIZE=%%s
)
if exist "dist\%STANDALONE_NAME%" (
    for /f %%s in ('powershell -command "[math]::Round((Get-Item 'dist\%STANDALONE_NAME%').Length / 1MB, 1)"') do set STANDALONE_SIZE=%%s
)

echo  Output dir: %CD%\dist\
echo.
echo  Portable:   %PORTABLE_NAME%
echo              Size: %PORTABLE_SIZE% MB
echo              Requires: .NET Desktop Runtime 8.0
echo              Download: https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo  Standalone: %STANDALONE_NAME%
echo              Size: %STANDALONE_SIZE% MB
echo              No runtime required - just run.
echo.
echo ========================================
echo.

if "%1"=="" pause
