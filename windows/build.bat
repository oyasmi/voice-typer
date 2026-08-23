@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

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
if "%VERSION%"=="" set VERSION=3.1.0

echo Version:  %VERSION%
echo.

REM ===== Clean previous build =====
if exist dist rd /s /q dist
if exist bin rd /s /q bin
if exist obj rd /s /q obj
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

REM ===== Publish x64 + arm64 (directory-style, NOT single-file) =====
REM 见 windows/DESIGN.md §7 D9：常驻自启工具不该用 PublishSingleFile 自解压，
REM 目录式部署 + Inno Setup 安装包才是最终产物。
for %%R in (win-x64 win-arm64) do (
    echo [2/4] Publishing %%R self-contained...
    dotnet publish VoiceTyper.csproj -c Release -r %%R ^
        --self-contained true ^
        -p:PublishSingleFile=false ^
        -o dist\%%R ^
        --nologo -v q
    if errorlevel 1 (
        echo [ERROR] Publish %%R failed.
        pause
        exit /b 1
    )
    echo       OK
)
echo.

REM ===== Code signing (optional,见 windows/README.md "签名" 章节) =====
REM 不设置 VOICETYPER_SIGN_THUMBPRINT 时整段跳过，行为完全不变——与 macOS 侧
REM build_xcode.sh 的可选签名对称。不签名的可执行文件会触发 SmartScreen
REM "未知发布者" 警告，与 Gatekeeper 是同一类问题（W-31）。
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
    powershell -NoProfile -Command "Compress-Archive -Path 'dist\%%R\*' -DestinationPath 'dist\!ZIP_NAME!' -Force"
)
echo       OK
echo.

REM ===== Inno Setup installers (optional: skipped if ISCC.exe not found) =====
echo [4/4] Building installers (Inno Setup)...
where iscc.exe >nul 2>&1
if errorlevel 1 (
    echo       [WARN] ISCC.exe (Inno Setup) not found in PATH — skipping installer build.
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
echo  Build complete! Output dir: %CD%\dist\
echo ========================================
if "%1"=="" pause
