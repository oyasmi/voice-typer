@echo off
setlocal enabledelayedexpansion
REM Keep this file pure ASCII. With "chcp 65001" active, cmd.exe mis-seeks the file
REM when it resumes reading after external commands if the file contains multi-byte
REM UTF-8 characters (e.g. Chinese comments), which corrupts parsing mid-script.
REM Put Chinese explanations in README.md / DESIGN.md instead.
chcp 65001 >nul
cd /d "%~dp0"

echo ========================================
echo VoiceTyper (unified) build script
echo ========================================
echo.

REM ===== Check .NET SDK =====
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found
    echo Please install .NET 10 SDK:
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo .NET SDK: %DOTNET_VER%

REM ===== Read version from csproj =====
set VERSION=
for /f "tokens=*" %%a in ('dotnet msbuild VoiceTyper.csproj -getProperty:Version -nologo 2^>nul') do set VERSION=%%a
if "%VERSION%"=="" set VERSION=3.5.1

echo Version:  %VERSION%
echo.

REM ===== Clean previous build =====
if exist dist rd /s /q dist
REM Do not wipe the whole bin tree: a developer build may be running from it.
mkdir dist 2>nul

REM ===== Restore =====
echo [1/4] Restoring NuGet packages...
dotnet restore VoiceTyper.csproj --nologo -v q
if errorlevel 1 (
    echo [ERROR] NuGet restore failed.
    pause
    exit /b 1
)
echo       OK
echo.

REM ===== Publish x64 + arm64: directory layout, needs the system .NET 10 Desktop Runtime =====
REM See windows/DESIGN.md section 7 D9: a resident autostart tray tool must not
REM self-extract via PublishSingleFile; directory deploy + Inno Setup installer
REM is the final artifact.
for %%R in (win-x64 win-arm64) do (
    echo [2/4] Publishing %%R framework-dependent...
    REM Clean stale output for the target RID so precompiled dependency DLLs
    REM from a previous publish are never reused after toggling ReadyToRun.
    dotnet clean VoiceTyper.csproj -c Release -r %%R --nologo -v q
    if errorlevel 1 (
        echo [ERROR] Clean %%R failed.
        pause
        exit /b 1
    )
    dotnet publish VoiceTyper.csproj -c Release -r %%R ^
        --self-contained false ^
        -p:PublishSingleFile=false ^
        -o dist\%%R ^
        --nologo -v q
    if errorlevel 1 (
        echo [ERROR] Publish %%R failed.
        pause
        exit /b 1
    )
    REM -ExecutionPolicy Bypass applies to this child process only: most Windows
    REM boxes default to Restricted and would refuse to load verify_publish.ps1,
    REM falsely failing the build.
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify_publish.ps1 -PublishDirectory dist\%%R
    if errorlevel 1 (
        echo [ERROR] Publish output verification failed for %%R.
        pause
        exit /b 1
    )
    echo       OK
)
echo.

REM ===== Code signing (optional, see windows/README.md) =====
REM Skipped entirely when VOICETYPER_SIGN_THUMBPRINT is unset -- symmetric with the
REM optional signing in the macOS build_xcode.sh. An unsigned executable triggers
REM the SmartScreen "unknown publisher" warning, same class of issue as Gatekeeper (W-31).
if not "%VOICETYPER_SIGN_THUMBPRINT%"=="" (
    if "%VOICETYPER_TIMESTAMP_URL%"=="" set VOICETYPER_TIMESTAMP_URL=http://timestamp.digicert.com
    echo Signing build outputs with thumbprint %VOICETYPER_SIGN_THUMBPRINT% ...
    for %%R in (win-x64 win-arm64) do (
        signtool.exe sign /sha1 %VOICETYPER_SIGN_THUMBPRINT% /fd SHA256 /tr %VOICETYPER_TIMESTAMP_URL% /td SHA256 "dist\%%R\VoiceTyper.exe"
        if errorlevel 1 (
            echo [ERROR] signtool failed for dist\%%R\VoiceTyper.exe
            pause
            exit /b 1
        )
    )
    echo       OK
    echo.
)

REM ===== Zip portable builds =====
echo [3/4] Packaging portable zips...
for %%R in (win-x64 win-arm64) do (
    set ZIP_NAME=VoiceTyper-%VERSION%-%%R-portable.zip
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; try { Compress-Archive -Path 'dist\%%R\*' -DestinationPath 'dist\!ZIP_NAME!' -Force } catch { Write-Error $_; exit 1 }"
    if errorlevel 1 (
        echo [ERROR] Compress-Archive failed for %%R.
        pause
        exit /b 1
    )
)
echo       OK
echo.

REM ===== Inno Setup installers (optional: skipped if ISCC.exe not found) =====
echo [4/4] Building installers (Inno Setup)...
where iscc.exe >nul 2>&1
if errorlevel 1 (
    echo       [WARN] ISCC.exe ^(Inno Setup^) not found in PATH -- skipping installer build.
    echo       Install from https://jrsoftware.org/isdl.php to produce the setup .exe.
) else (
    for %%R in (x64 arm64) do (
        iscc.exe /DAppVersion=%VERSION% /DTargetRid=win-%%R /DTargetArch=%%R installer\VoiceTyper.iss
        if errorlevel 1 (
            echo [ERROR] Inno Setup build failed for %%R.
            pause
            exit /b 1
        )
        if not "%VOICETYPER_SIGN_THUMBPRINT%"=="" (
            signtool.exe sign /sha1 %VOICETYPER_SIGN_THUMBPRINT% /fd SHA256 /tr %VOICETYPER_TIMESTAMP_URL% /td SHA256 "dist\VoiceTyper-%VERSION%-win-%%R-setup.exe"
            if errorlevel 1 (
                echo [ERROR] signtool failed for the win-%%R installer.
                pause
                exit /b 1
            )
        )
    )
)
echo.

echo ========================================
echo  Build complete. Output dir: %CD%\dist\
echo ========================================
if "%1"=="" pause
