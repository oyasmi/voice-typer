"""
命令行入口
"""
import argparse
import sys

from . import __version__


def build_parser() -> argparse.ArgumentParser:
    """构建命令行参数解析器"""
    parser = argparse.ArgumentParser(description="VoiceTyper 语音识别服务")
    parser.add_argument("--host", default="127.0.0.1", help="监听地址 (默认: %(default)s)")
    parser.add_argument("--port", type=int, default=6008, help="监听端口 (默认: %(default)s)")
    # 识别模式
    streaming_group = parser.add_mutually_exclusive_group()
    streaming_group.add_argument(
        "--streaming",
        dest="streaming",
        action="store_true",
        default=True,
        help="使用流式识别（WebSocket，默认）",
    )
    streaming_group.add_argument(
        "--no-streaming",
        dest="streaming",
        action="store_false",
        help="使用非流式识别（HTTP，兼容模式）",
    )

    parser.add_argument(
        "--model",
        default=None,
        help=(
            "ASR 模型（默认：流式模式用 paraformer-zh-streaming，"
            "非流式模式用 paraformer-zh）"
        ),
    )
    parser.add_argument(
        "--offline-model",
        default=None,
        help=(
            "流式模式下用于松手后复识别的离线模型，最终结果由它产出 "
            "(默认: paraformer-zh)"
        ),
    )
    parser.add_argument(
        "--punc-model",
        default="ct-punc",
        help="标点模型，使用 none 可禁用 (默认: %(default)s)",
    )
    parser.add_argument("--device", default="cpu", help="设备: cpu/cuda/cuda:N (默认: %(default)s)")
    parser.add_argument(
        "--chunk-size",
        default="0,10,5",
        help="流式模式 chunk 大小，格式: left,current,right，单位 60ms 帧 (默认: %(default)s)",
    )
    parser.add_argument("--api-keys", help="API 密钥（逗号分隔多个密钥）")
    parser.add_argument(
        "--onnx-threads",
        type=int,
        default=4,
        help="ONNX 后端 intra-op 线程数 (默认: %(default)s)",
    )
    parser.add_argument("--llm-base-url", help="LLM API 基础 URL")
    parser.add_argument("--llm-api-key", help="LLM API 密钥")
    parser.add_argument("--llm-model", default="gpt-4o-mini", help="LLM 模型名称 (默认: %(default)s)")
    parser.add_argument(
        "--llm-temperature",
        type=float,
        default=0.3,
        help="LLM 温度参数 (默认: %(default)s)",
    )
    parser.add_argument(
        "--llm-max-tokens",
        type=int,
        default=600,
        help="LLM 最大生成 token 数 (默认: %(default)s)",
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    return parser


def _build_service_parser() -> argparse.ArgumentParser:
    """构建 service 子命令解析器"""
    parser = argparse.ArgumentParser(
        prog="voice-typer-server service",
        description="管理 VoiceTyper Windows 服务",
    )
    sub = parser.add_subparsers(dest="action", help="服务管理操作")
    sub.required = True

    # install
    install_parser = sub.add_parser("install", help="安装为 Windows 服务")
    install_parser.add_argument(
        "--startup",
        choices=["auto", "manual"],
        default="auto",
        help="启动类型 (默认: %(default)s)",
    )
    install_parser.add_argument(
        "server_args",
        nargs=argparse.REMAINDER,
        help="服务运行参数（在 -- 之后指定，如: -- --host 0.0.0.0 --port 6008）",
    )

    # uninstall
    sub.add_parser("uninstall", help="卸载 Windows 服务")

    # start
    sub.add_parser("start", help="启动服务")

    # stop
    sub.add_parser("stop", help="停止服务")

    return parser


def _handle_service_command(argv):
    """处理 service 子命令"""
    if sys.platform != "win32":
        print("错误: service 命令仅在 Windows 上可用", file=sys.stderr)
        sys.exit(1)

    try:
        from .win_service import install_service, uninstall_service, start_service, stop_service
    except ImportError as exc:
        print(
            f"错误: 缺少 pywin32 依赖，请运行: pip install voice-typer-server[windows-service]\n"
            f"详细信息: {exc}",
            file=sys.stderr,
        )
        sys.exit(1)

    parser = _build_service_parser()
    args = parser.parse_args(argv)

    if args.action == "install":
        # 去掉 server_args 中可能存在的 '--' 分隔符
        server_args = args.server_args
        if server_args and server_args[0] == "--":
            server_args = server_args[1:]
        install_service(startup=args.startup, server_args=server_args or None)

    elif args.action == "uninstall":
        uninstall_service()

    elif args.action == "start":
        start_service()

    elif args.action == "stop":
        stop_service()


def main(argv=None):
    """CLI 主入口"""
    if sys.version_info < (3, 9):
        raise SystemExit("voice-typer-server requires Python 3.9 or newer")

    # 预处理参数：检测 service 子命令
    raw_argv = argv if argv is not None else sys.argv[1:]
    if raw_argv and raw_argv[0] == "service":
        _handle_service_command(raw_argv[1:])
        return

    parser = build_parser()
    args = parser.parse_args(argv)

    from .app import run_server

    run_server(args)

