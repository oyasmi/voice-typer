@echo off
setlocal enabledelayedexpansion

set "VENV_DIR=%USERPROFILE%\.venvs\voice-typer"
if "%PYTHON_BIN%"=="" set "PYTHON_BIN=python"
set "MIN_PYTHON_MAJOR=3"
set "MIN_PYTHON_MINOR=10"

if "%~1"=="setup" (
    shift
    goto :setup
)
if "%~1"=="run" (
    shift
    goto :run
)
echo 用法: %~nx0 {setup [--local [PATH]]^|run [voice-typer-server 参数...]} 1>&2
exit /b 1

:check_python_version
    set "PY=%~1"
    set "SRC=%~2"
    where "%PY%" >nul 2>&1
    if errorlevel 1 (
        echo %SRC%不存在或不可执行: %PY% 1>&2
        exit /b 1
    )
    "%PY%" -c "import sys; exit(0 if sys.version_info >= (%MIN_PYTHON_MAJOR%, %MIN_PYTHON_MINOR%) else 1)" 2>nul
    if errorlevel 1 (
        for /f "delims=" %%v in ('"%PY%" -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')"') do set "PY_VER=%%v"
        echo %SRC%版本过低: !PY_VER!。VoiceTyper Server 最低要求 Python %MIN_PYTHON_MAJOR%.%MIN_PYTHON_MINOR%+。 1>&2
        exit /b 1
    )
    exit /b 0

:setup
    call :check_python_version "%PYTHON_BIN%" "PYTHON_BIN"
    if errorlevel 1 exit /b 1

    if not exist "%USERPROFILE%\.venvs" mkdir "%USERPROFILE%\.venvs"
    "%PYTHON_BIN%" -m venv "%VENV_DIR%"
    "%VENV_DIR%\Scripts\python.exe" -m pip install --upgrade pip setuptools wheel

    rem 检查是否为 --local 模式
    if "%~1"=="--local" (
        shift
        if "%~1"=="" (
            set "TARGET=%~dp0.."
        ) else (
            set "TARGET=%~1"
        )
        "%VENV_DIR%\Scripts\python.exe" -m pip install --upgrade --no-build-isolation "!TARGET!"
        exit /b %errorlevel%
    )

    rem 收集剩余参数
    set "ARGS="
    :setup_args
    if "%~1"=="" goto :setup_do
    set "ARGS=!ARGS! %~1"
    shift
    goto :setup_args

    :setup_do
    "%VENV_DIR%\Scripts\python.exe" -m pip install --upgrade voice-typer-server !ARGS!
    exit /b %errorlevel%

:run
    if not exist "%VENV_DIR%\Scripts\voice-typer-server.exe" (
        echo 虚拟环境不存在或未安装 voice-typer-server，请先运行: %~nx0 setup 1>&2
        exit /b 1
    )

    call :check_python_version "%VENV_DIR%\Scripts\python.exe" "虚拟环境 Python"
    if errorlevel 1 exit /b 1

    rem 收集剩余参数
    set "ARGS="
    :run_args
    if "%~1"=="" goto :run_do
    set "ARGS=!ARGS! %~1"
    shift
    goto :run_args

    :run_do
    "%VENV_DIR%\Scripts\voice-typer-server.exe" --host 127.0.0.1 --port 6008 --device cpu !ARGS!
    exit /b %errorlevel%
