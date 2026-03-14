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
    parser.add_argument("--model", default="paraformer-zh", help="ASR 模型 (默认: %(default)s)")
    parser.add_argument(
        "--punc-model",
        default="ct-punc",
        help="标点模型，使用 none 可禁用 (默认: %(default)s)",
    )
    parser.add_argument("--device", default="cpu", help="设备: cpu/cuda/cuda:N (默认: %(default)s)")
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


def main(argv=None):
    """CLI 主入口"""
    if sys.version_info < (3, 9):
        raise SystemExit("voice-typer-server requires Python 3.9 or newer")

    parser = build_parser()
    args = parser.parse_args(argv)

    from .app import run_server

    run_server(args)
