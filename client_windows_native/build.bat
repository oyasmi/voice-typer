@echo off
setlocal enabledelayedexpansion

echo ========================================
echo VoiceTyper Windows Native 构建脚本
echo ========================================
echo.

REM ===== 检查 .NET SDK =====
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 .NET SDK
    echo.
    echo 请安装 .NET 8 SDK:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo .NET SDK: %DOTNET_VER%

REM ===== 读取版本号 =====
set VERSION=
for /f "tokens=*" %%a in ('dotnet msbuild -getProperty:Version -nologo 2^>nul') do set VERSION=%%a
if "%VERSION%"=="" set VERSION=2.1.0

echo 版本号:  %VERSION%
echo 目标:    win-x64
echo.

REM ===== 清理旧构建 =====
if exist dist rd /s /q dist
if exist bin rd /s /q bin
if exist obj rd /s /q obj
mkdir dist 2>nul

REM ===== [0/3] 还原依赖 =====
echo [0/3] 还原 NuGet 依赖...
dotnet restore --nologo -v q
if errorlevel 1 (
    echo.
    echo [错误] NuGet 还原失败，请检查网络连接
    pause
    exit /b 1
)
echo       完成
echo.

REM ===== [1/3] 构建检查 =====
echo [1/3] 编译检查...
dotnet build -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo [错误] 编译失败，请检查错误信息
    pause
    exit /b 1
)
echo       编译通过
echo.

REM ===== [2/3] 便携版 =====
echo [2/3] 发布便携版 (framework-dependent, ~3MB)...
dotnet publish -c Release -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -o dist\_portable ^
    --nologo -v q

if errorlevel 1 (
    echo.
    echo [错误] 便携版发布失败
    pause
    exit /b 1
)
echo       成功
echo.

REM ===== [3/3] 完整版 =====
echo [3/3] 发布完整版 (self-contained + compressed)...
dotnet publish -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o dist\_standalone ^
    --nologo -v q

if errorlevel 1 (
    echo.
    echo [错误] 完整版发布失败
    pause
    exit /b 1
)
echo       成功
echo.

REM ===== 整理产物 =====
set PORTABLE_NAME=VoiceTyper-%VERSION%-win-x64-portable.exe
set STANDALONE_NAME=VoiceTyper-%VERSION%-win-x64.exe

move "dist\_portable\VoiceTyper.exe" "dist\%PORTABLE_NAME%" >nul 2>&1
move "dist\_standalone\VoiceTyper.exe" "dist\%STANDALONE_NAME%" >nul 2>&1

REM 复制图标到 dist（便携版运行时可能需要）
if exist "Assets\icon.ico" (
    mkdir "dist\Assets" 2>nul
    copy "Assets\icon.ico" "dist\Assets\icon.ico" >nul 2>&1
)

REM 清理临时目录
rd /s /q dist\_portable 2>nul
rd /s /q dist\_standalone 2>nul

REM ===== 输出结果 =====
echo ========================================
echo  构建完成!
echo ========================================
echo.

REM 计算文件大小
set PORTABLE_SIZE=?
set STANDALONE_SIZE=?

if exist "dist\%PORTABLE_NAME%" (
    for /f %%s in ('powershell -command "[math]::Round((Get-Item 'dist\%PORTABLE_NAME%').Length / 1MB, 1)"') do set PORTABLE_SIZE=%%s
)
if exist "dist\%STANDALONE_NAME%" (
    for /f %%s in ('powershell -command "[math]::Round((Get-Item 'dist\%STANDALONE_NAME%').Length / 1MB, 1)"') do set STANDALONE_SIZE=%%s
)

echo  产物目录: %CD%\dist\
echo.
echo  便携版:   %PORTABLE_NAME%
echo            大小: %PORTABLE_SIZE% MB
echo            需要: .NET Desktop Runtime 8.0
echo            下载: https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo  完整版:   %STANDALONE_NAME%
echo            大小: %STANDALONE_SIZE% MB
echo            无需安装任何运行时，开箱即用
echo.
echo ========================================
echo.

REM 如果从资源管理器双击运行, 暂停以查看结果
if "%1"=="" pause
