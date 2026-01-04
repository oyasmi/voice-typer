#!/bin/bash
# VoiceTyper 语音识别服务启动脚本
# 命令示例：$HOME/projects/voice-typer/server/run.sh --host 127.0.0.1 --api-keys a_super_key \
#    --llm-base-url https://dashscope.aliyuncs.com/compatible-mode/v1 \
#    --llm-api-key sk-xxx \
#    --llm-model qwen-flash-2025-07-28

cd "$(dirname "$0")"

# 默认参数
HOST="127.0.0.1"
PORT=6008
MODEL="paraformer-zh"
PUNC_MODEL="ct-punc"
DEVICE="cpu"
ASR_API_KEYS=""

# LLM 相关参数
LLM_BASE_URL=""
LLM_API_KEY=""
LLM_MODEL="gpt-4o-mini"
LLM_TEMPERATURE="0.3"
LLM_MAX_TOKENS="500"

# 解析命令行参数
while [ $# -gt 0 ]; do
    case $1 in
        --host)
            HOST="$2"
            shift 2
            ;;
        --port)
            PORT="$2"
            shift 2
            ;;
        --model)
            MODEL="$2"
            shift 2
            ;;
        --punc-model)
            PUNC_MODEL="$2"
            shift 2
            ;;
        --device)
            DEVICE="$2"
            shift 2
            ;;
        --api-keys)
            ASR_API_KEYS="$2"
            shift 2
            ;;
        --llm-base-url)
            LLM_BASE_URL="$2"
            shift 2
            ;;
        --llm-api-key)
            LLM_API_KEY="$2"
            shift 2
            ;;
        --llm-model)
            LLM_MODEL="$2"
            shift 2
            ;;
        --llm-temperature)
            LLM_TEMPERATURE="$2"
            shift 2
            ;;
        --llm-max-tokens)
            LLM_MAX_TOKENS="$2"
            shift 2
            ;;
        -h|--help)
            echo "VoiceTyper 语音识别服务"
            echo ""
            echo "用法: ./run.sh [选项]"
            echo ""
            echo "ASR 选项:"
            echo "  --host HOST           监听地址 (默认: 127.0.0.1)"
            echo "  --port PORT           监听端口 (默认: 6008)"
            echo "  --model MODEL         ASR 模型 (默认: paraformer-zh)"
            echo "  --punc-model M        标点模型 (默认: ct-punc, 设为 none 禁用)"
            echo "  --device DEVICE       计算设备 (默认: cpu, 其他 mps)"
            echo "  --api-keys K          API 密钥（逗号分隔多个密钥）"
            echo ""
            echo "LLM 选项:"
            echo "  --llm-base-url URL    LLM API 基础URL (如 https://api.openai.com/v1)"
            echo "  --llm-api-key KEY     LLM API 密钥"
            echo "  --llm-model MODEL     LLM 模型名称 (默认: gpt-4o-mini)"
            echo "  --llm-temperature T   LLM 温度参数 (默认: 0.3)"
            echo "  --llm-max-tokens N    LLM 最大token数 (默认: 600)"
            echo ""
            exit 0
            ;;
        *)
            echo "未知参数: $1"
            echo "使用 --help 查看帮助"
            exit 1
            ;;
    esac
done

# 启动服务
ARGS=(
    --host "$HOST"
    --port "$PORT"
    --model "$MODEL"
    --punc-model "$PUNC_MODEL"
    --device "$DEVICE"
)

# 只有当ASR_API_KEYS不为空时才添加--api-keys参数
if [ -n "$ASR_API_KEYS" ]; then
    ARGS+=(--api-keys "$ASR_API_KEYS")
fi

# LLM 参数
if [ -n "$LLM_BASE_URL" ]; then
    ARGS+=(--llm-base-url "$LLM_BASE_URL")
fi

if [ -n "$LLM_API_KEY" ]; then
    ARGS+=(--llm-api-key "$LLM_API_KEY")
fi

if [ -n "$LLM_MODEL" ] && [ -n "$LLM_BASE_URL" ]; then
    ARGS+=(--llm-model "$LLM_MODEL")
fi

if [ -n "$LLM_TEMPERATURE" ] && [ -n "$LLM_BASE_URL" ]; then
    ARGS+=(--llm-temperature "$LLM_TEMPERATURE")
fi

if [ -n "$LLM_MAX_TOKENS" ] && [ -n "$LLM_BASE_URL" ]; then
    ARGS+=(--llm-max-tokens "$LLM_MAX_TOKENS")
fi

exec python asr_server.py "${ARGS[@]}"