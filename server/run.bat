@echo off
setlocal EnableDelayedExpansion

cd /d "%~dp0"

:: 默认参数
set HOST=127.0.0.1
set PORT=6008
set MODEL=paraformer-zh
set PUNC_MODEL=ct-punc
set DEVICE=cpu
set ASR_API_KEYS=

:: LLM 相关参数
set LLM_BASE_URL=
set LLM_API_KEY=
set LLM_MODEL=gpt-4o-mini
set LLM_TEMPERATURE=0.3
set LLM_MAX_TOKENS=500

:parse_loop
if "%~1"=="" goto start_server

if "%~1"=="--host" (
    set HOST=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--port" (
    set PORT=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--model" (
    set MODEL=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--punc-model" (
    set PUNC_MODEL=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--device" (
    set DEVICE=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--api-keys" (
    set ASR_API_KEYS=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--llm-base-url" (
    set LLM_BASE_URL=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--llm-api-key" (
    set LLM_API_KEY=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--llm-model" (
    set LLM_MODEL=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--llm-temperature" (
    set LLM_TEMPERATURE=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="--llm-max-tokens" (
    set LLM_MAX_TOKENS=%~2
    shift
    shift
    goto parse_loop
)
if "%~1"=="-h" goto help
if "%~1"=="--help" goto help

echo 未知参数: %~1
echo 使用 --help 查看帮助
exit /b 1

:help
echo VoiceTyper 语音识别服务
echo.
echo 用法: run.bat [选项]
echo.
echo ASR 选项:
echo   --host HOST           监听地址 (默认: 127.0.0.1)
echo   --port PORT           监听端口 (默认: 6008)
echo   --model MODEL         ASR 模型 (默认: paraformer-zh)
echo   --punc-model M        标点模型 (默认: ct-punc, 设为 none 禁用)
echo   --device DEVICE       计算设备 (默认: cpu, 其他 mps)
echo   --api-keys K          API 密钥（逗号分隔多个密钥）
echo.
echo LLM 选项:
echo   --llm-base-url URL    LLM API 基础URL (如 https://api.openai.com/v1)
echo   --llm-api-key KEY     LLM API 密钥
echo   --llm-model MODEL     LLM 模型名称 (默认: gpt-4o-mini)
echo   --llm-temperature T   LLM 温度参数 (默认: 0.3)
echo   --llm-max-tokens N    LLM 最大token数 (默认: 600)
echo.
exit /b 0

:start_server
:: 构造基本的参数
set ARGS=--host !HOST! --port !PORT! --model !MODEL! --punc-model !PUNC_MODEL! --device !DEVICE!

:: 只有当ASR_API_KEYS不为空时才添加
if not "!ASR_API_KEYS!"=="" (
    set ARGS=!ARGS! --api-keys !ASR_API_KEYS!
)

:: LLM 参数
if not "!LLM_BASE_URL!"=="" (
    set ARGS=!ARGS! --llm-base-url !LLM_BASE_URL!
    
    :: 只有设置了 base url, model/temp/max_tokens 才生效 (参考 run.sh 逻辑)
    if not "!LLM_MODEL!"=="" set ARGS=!ARGS! --llm-model !LLM_MODEL!
    if not "!LLM_TEMPERATURE!"=="" set ARGS=!ARGS! --llm-temperature !LLM_TEMPERATURE!
    if not "!LLM_MAX_TOKENS!"=="" set ARGS=!ARGS! --llm-max-tokens !LLM_MAX_TOKENS!
)

:: api key 似乎是如果设置了就传 (参考 run.sh 逻辑)
if not "!LLM_API_KEY!"=="" (
    set ARGS=!ARGS! --llm-api-key !LLM_API_KEY!
)

python asr_server.py !ARGS!
