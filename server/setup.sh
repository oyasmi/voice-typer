#!/bin/bash
set -e

VENV_DIR="$HOME/.venvs/voice-typer"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

install_lib() {
    echo "==> 创建虚拟环境..."
    mkdir -p "$HOME/.venvs"
    python3 -m venv "$VENV_DIR"

    echo "==> 安装依赖..."
    "$VENV_DIR/bin/pip" install -q -r "$SCRIPT_DIR/requirements.txt" --index-url https://pypi.tuna.tsinghua.edu.cn/simple
    echo "✓ 完成"
}

start_server() {
    if [ ! -d "$VENV_DIR" ]; then
        echo "✗ 虚拟环境不存在，先运行: $0 --install-lib"
        exit 1
    fi

    read -rp "LLM API Key (留空禁用LLM): " llm_key

    source "$VENV_DIR/bin/activate"
    if [ -z "$llm_key" ]; then
        echo "==> 启动服务器 (LLM功能已禁用)..."
        "$SCRIPT_DIR/run.sh" "$@"
    else
        echo "==> 启动服务器..."
        "$SCRIPT_DIR/run.sh" --llm-base-url https://antchat.alipay.com/v1 --llm-model Qwen3-Next-80B-A3B-Instruct --llm-api-key "$llm_key" "$@"
    fi
}

case "$1" in
    --install-lib)
        install_lib
        ;;
    --start-server)
        shift
        start_server "$@"
        ;;
    *)
        echo "用法: $0 [--install-lib | --start-server [run.sh参数]]"
        exit 1
        ;;
esac
