#!/bin/sh
set -eu

VENV_DIR="${HOME}/.venvs/voice-typer"
PYTHON_BIN="${PYTHON_BIN:-python3}"
MIN_PYTHON_MAJOR=3
MIN_PYTHON_MINOR=10

check_python_version() {
    python_bin="$1"
    version_source="$2"

    if ! command -v "${python_bin}" >/dev/null 2>&1; then
        echo "${version_source}不存在或不可执行: ${python_bin}" >&2
        exit 1
    fi

    if ! "${python_bin}" - <<'PY'
import sys

min_major = 3
min_minor = 10

if sys.version_info < (min_major, min_minor):
    sys.exit(1)
PY
    then
        version="$("${python_bin}" -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}")')"
        echo "${version_source}版本过低: ${version}。VoiceTyper Server 最低要求 Python ${MIN_PYTHON_MAJOR}.${MIN_PYTHON_MINOR}+。" >&2
        exit 1
    fi
}

setup() {
    check_python_version "${PYTHON_BIN}" "PYTHON_BIN"
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

    check_python_version "${VENV_DIR}/bin/python" "虚拟环境 Python"

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
