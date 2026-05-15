"""
VoiceTyper 流式语音识别服务
"""
import asyncio
import concurrent.futures
import json
import logging
import signal
import sys
import time

import numpy as np
import tornado.httpserver
import tornado.ioloop
import tornado.web
import tornado.websocket

from .auth import BaseAuthenticatedHandler
from .llm_client import LLMClient
from .recognizer import StreamingSpeechRecognizer

logger = logging.getLogger("VoiceTyper")


def _check_api_key(request, settings) -> bool:
    """WebSocket 握手阶段的 API key 校验（复用 BaseAuthenticatedHandler 逻辑）。"""
    api_keys = settings.get("api_keys", [])
    server_host = settings.get("server_host", "127.0.0.1")

    if not api_keys:
        return True

    # 本地地址免鉴权
    remote_ip = request.remote_ip or ""
    if server_host == "127.0.0.1" and remote_ip in ("127.0.0.1", "::1"):
        return True

    auth_header = request.headers.get("Authorization", "")
    if auth_header.startswith("Bearer "):
        token = auth_header[len("Bearer "):]
        return token in api_keys

    return False


class HealthHandler(BaseAuthenticatedHandler):
    """健康检查"""

    @tornado.web.authenticated
    def get(self):
        recognizer = self.application.settings.get("recognizer")
        llm_client = self.application.settings.get("llm_client")
        self.write({
            "status": "ok",
            "ready": recognizer.is_ready if recognizer else False,
            "llm_enabled": llm_client is not None,
            "streaming": True,
        })


class StreamRecognizeHandler(tornado.websocket.WebSocketHandler):
    """
    流式语音识别 WebSocket 端点 (/recognize/stream)

    协议：
    Client → Server:
      连接后立即发送  text:   {"type":"start","hotwords":"词1 词2","sample_rate":16000}
      录音中每 600ms  binary: float32 PCM，9600 samples = 38400 bytes
      松开热键时      text:   {"type":"finalize"}

    Server → Client:
      每个 chunk 有增量  text: {"type":"partial","text":"今天","seq":N}
      finalize 完成     text: {"type":"final","text":"今天天气不错。","asrElapsed":0.82}
      错误              text: {"type":"error","code":"...","message":"..."}
      完成后 close (1000)
    """

    def check_origin(self, origin):
        return True

    def prepare(self):
        if not _check_api_key(self.request, self.application.settings):
            self.set_status(401)
            self.finish({"error": "Unauthorized"})

    def open(self):
        recognizer = self.application.settings.get("recognizer")
        if not recognizer or not recognizer.is_ready:
            self.close(code=4503, reason="service not ready")
            return

        self.llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        self.session = None
        self.hotwords = ""
        self.seq = 0
        self._tail_chunk: bytes = b""
        logger.info("WS 连接建立")

    async def on_message(self, message):
        try:
            if isinstance(message, (bytes, bytearray)):
                await self._handle_audio(bytes(message))
            else:
                await self._handle_control(message)
        except Exception as exc:
            logger.exception("WS message 处理异常")
            await self._send_error("internal", str(exc))
            self.close(code=4500)

    async def _handle_audio(self, data: bytes):
        if self.session is None:
            return  # start 帧还没到，忽略

        chunk = np.frombuffer(data, dtype=np.float32)
        loop = asyncio.get_event_loop()
        executor = self.application.settings.get("executor")
        fragment = await loop.run_in_executor(executor, self.session.feed, chunk)

        if fragment:
            await self.write_message(json.dumps(
                {"type": "partial", "text": fragment, "seq": self.seq},
                ensure_ascii=False,
            ))
            self.seq += 1

    async def _handle_control(self, raw: str):
        try:
            msg = json.loads(raw)
        except json.JSONDecodeError:
            await self._send_error("bad_request", "invalid JSON")
            return

        msg_type = msg.get("type")

        if msg_type == "start":
            recognizer = self.application.settings["recognizer"]
            self.hotwords = msg.get("hotwords", "")
            self.session = recognizer.new_session()
            self.seq = 0
            logger.info(f"会话开始，hotwords='{self.hotwords}' llm={self.llm_recorrect}")

        elif msg_type == "finalize":
            await self._do_finalize()

        else:
            logger.warning(f"未知控制帧 type={msg_type!r}")

    async def _do_finalize(self):
        if self.session is None:
            await self._send_error("bad_state", "session not started")
            self.close(code=4500)
            return

        loop = asyncio.get_event_loop()
        executor = self.application.settings.get("executor")

        t0 = time.time()
        text = await loop.run_in_executor(executor, self.session.finalize, None)
        asr_elapsed = round(time.time() - t0, 3)
        logger.info(f"ASR finalize 耗时: {asr_elapsed}s，原始文本: {text!r}")

        llm_elapsed = None
        llm_client = self.application.settings.get("llm_client")
        if self.llm_recorrect and llm_client and text.strip():
            try:
                t1 = time.time()
                original = text
                text = await llm_client.correct_text(text)
                llm_elapsed = round(time.time() - t1, 3)
                if original != text:
                    logger.info(f"LLM 修正 ({llm_elapsed}s): {original!r} → {text!r}")
                else:
                    logger.info(f"LLM 修正: 无需改动 ({llm_elapsed}s)")
            except Exception as exc:
                logger.warning(f"LLM 修正失败，使用原始文本: {exc}")

        payload = {"type": "final", "text": text, "asrElapsed": asr_elapsed}
        if llm_elapsed is not None:
            payload["llmElapsed"] = llm_elapsed

        await self.write_message(json.dumps(payload, ensure_ascii=False))
        self.close(code=1000)

    async def _send_error(self, code: str, message: str):
        try:
            await self.write_message(json.dumps(
                {"type": "error", "code": code, "message": message},
                ensure_ascii=False,
            ))
        except Exception:
            pass

    def on_close(self):
        self.session = None
        logger.info(f"WS 连接关闭，code={self.close_code}")


def make_app(api_keys=None, server_host="127.0.0.1", recognizer=None, llm_client=None, executor=None):
    """创建 Tornado 应用"""
    app = tornado.web.Application([
        (r"/health", HealthHandler),
        (r"/recognize/stream", StreamRecognizeHandler),
    ])
    app.settings["api_keys"] = api_keys or []
    app.settings["server_host"] = server_host
    app.settings["recognizer"] = recognizer
    app.settings["llm_client"] = llm_client
    app.settings["executor"] = executor
    return app


def load_api_keys(api_keys_arg):
    if not api_keys_arg:
        return []
    return [k.strip() for k in api_keys_arg.split(",") if k.strip()]


def configure_logging():
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s - %(levelname)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        stream=sys.stdout,
    )


class ServerContext:
    def __init__(self, server, executor, llm_client):
        self.server = server
        self.executor = executor
        self.llm_client = llm_client

    def shutdown(self):
        logger.info("停止服务...")
        if self.llm_client:
            self.llm_client.close()
        self.executor.shutdown(wait=False)
        tornado.ioloop.IOLoop.current().stop()


def create_server(args) -> ServerContext:
    """初始化模型、创建 HTTP/WS 服务并开始监听。"""
    api_keys = load_api_keys(args.api_keys)
    if api_keys:
        logger.info(f"API密钥: 已配置 {len(api_keys)} 个")
    elif args.host != "127.0.0.1":
        logger.warning("远程访问未配置 API 密钥，建议使用 --api-keys 参数")

    logger.info("=" * 50)
    logger.info("VoiceTyper 流式语音识别服务")
    logger.info("=" * 50)
    logger.info(f"地址: ws://{args.host}:{args.port}/recognize/stream")
    logger.info("后端: onnx (streaming)")
    logger.info(f"模型: {args.model}")
    logger.info(f"标点: {args.punc_model}")
    logger.info(f"设备: {args.device}")
    logger.info(f"Chunk: {args.chunk_size}")
    logger.info(f"Python: {sys.version.split()[0]}")
    if args.host == "127.0.0.1":
        logger.info("鉴权: 本地地址，已跳过")
    else:
        logger.info(f"鉴权: {'已启用' if api_keys else '未启用（不安全）'}")

    llm_client = None
    if args.llm_base_url and args.llm_api_key:
        logger.info(f"LLM: 已启用 ({args.llm_model})")
        llm_client = LLMClient(
            base_url=args.llm_base_url,
            api_key=args.llm_api_key,
            model=args.llm_model,
            temperature=args.llm_temperature,
            max_tokens=args.llm_max_tokens,
        )
    else:
        logger.info("LLM: 未启用")

    punc_model = args.punc_model if args.punc_model != "none" else None

    chunk_size = [int(x) for x in args.chunk_size.split(",")]
    recognizer = StreamingSpeechRecognizer(
        model_name=args.model,
        punc_model=punc_model,
        device=args.device,
        chunk_size=chunk_size,
        intra_op_num_threads=args.onnx_threads,
    )

    logger.info("初始化模型...")
    t0 = time.time()
    recognizer.initialize()
    elapsed = time.time() - t0
    logger.info(f"初始化完成，耗时 {elapsed:.1f}s")

    executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)
    app = make_app(
        api_keys=api_keys,
        server_host=args.host,
        recognizer=recognizer,
        llm_client=llm_client,
        executor=executor,
    )
    server = tornado.httpserver.HTTPServer(app)
    server.listen(args.port, args.host)

    logger.info(f"服务已启动: ws://{args.host}:{args.port}/recognize/stream")

    return ServerContext(server, executor, llm_client)


def run_server(args):
    """以前台模式启动服务"""
    configure_logging()

    ctx = create_server(args)
    logger.info("按 Ctrl+C 停止服务")

    def shutdown(signum, frame):
        ctx.shutdown()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    tornado.ioloop.IOLoop.current().start()
