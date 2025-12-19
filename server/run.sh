#!/bin/bash
# VoiceTyper 语音识别服务启动脚本

cd "$(dirname "$0")"

# 默认参数
HOST="127.0.0.1"
PORT=6008
MODEL="paraformer-zh"
PUNC_MODEL="ct-punc"
DEVICE="cpu"

# 显示帮助
if [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
    echo "VoiceTyper 语音识别服务"
    echo ""
    echo "用法: ./run.sh [选项]"
    echo ""
    echo "选项:"
    echo "  --host HOST      监听地址 (默认: 127.0.0.1)"
    echo "  --port PORT      监听端口 (默认: 6008)"
    echo "  --model MODEL    ASR 模型 (默认: paraformer-zh)"
    echo "  --punc-model M   标点模型 (默认: ct-punc, 设为 none 禁用)"
    echo "  --device DEVICE  计算设备 (默认: cpu, 其他 mps)"
    echo ""
    exit 0
fi

# 启动服务
exec python voice_server.py \
    --host "$HOST" \
    --port "$PORT" \
    --model "$MODEL" \
    --punc-model "$PUNC_MODEL" \
    --device "$DEVICE" \
    "$@"