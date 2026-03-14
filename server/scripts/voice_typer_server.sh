#!/bin/sh
set -eu

VENV_DIR="${HOME}/.venvs/voice-typer"
PYTHON_BIN="${PYTHON_BIN:-python3}"

setup() {
    mkdir -p "${HOME}/.venvs"
    "${PYTHON_BIN}" -m venv "${VENV_DIR}"
    "${VENV_DIR}/bin/python" -m pip install --upgrade pip setuptools wheel

    if [ "${1:-}" = "--local" ]; then
        shift
        target="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
        "${VENV_DIR}/bin/python" -m pip install --upgrade --no-build-isolation "${target}"
        return
    fi

    "${VENV_DIR}/bin/python" -m pip install --upgrade voice-typer-server "$@"
}

run() {
    if [ ! -x "${VENV_DIR}/bin/voice-typer-server" ]; then
        echo "虚拟环境不存在或未安装 voice-typer-server，请先运行: $0 setup" >&2
        exit 1
    fi

    exec "${VENV_DIR}/bin/voice-typer-server" \
        --host 127.0.0.1 \
        --port 6008 \
        --device cpu \
        "$@"
}

case "${1:-}" in
    setup)
        shift
        setup "$@"
        ;;
    run)
        shift
        run "$@"
        ;;
    *)
        echo "用法: $0 {setup [--local [PATH]]|run [voice-typer-server 参数...]}" >&2
        exit 1
        ;;
esac
