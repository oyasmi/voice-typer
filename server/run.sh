#!/bin/bash
# VoiceTyper 语音识别服务启动脚本

cd "$(dirname "$0")"

# 默认参数
HOST="127.0.0.1"
PORT=6008
MODEL="paraformer-zh"
PUNC_MODEL="ct-punc"
DEVICE="cpu"
ASR_API_KEYS=""

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
        -h|--help)
            echo "VoiceTyper 语音识别服务"
            echo ""
            echo "用法: ./run.sh [选项]"
            echo ""
            echo "选项:"
            echo "  --host HOST        监听地址 (默认: 127.0.0.1)"
            echo "  --port PORT        监听端口 (默认: 6008)"
            echo "  --model MODEL      ASR 模型 (默认: paraformer-zh)"
            echo "  --punc-model M     标点模型 (默认: ct-punc, 设为 none 禁用)"
            echo "  --device DEVICE    计算设备 (默认: cpu, 其他 mps)"
            echo "  --api-keys K       API 密钥（逗号分隔多个密钥）"
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

exec python asr_server.py "${ARGS[@]}"