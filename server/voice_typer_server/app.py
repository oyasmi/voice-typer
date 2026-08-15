"""
VoiceTyper 语音识别服务
支持流式（WebSocket）和非流式（HTTP）两种模式。
"""
import asyncio
import concurrent.futures
import json
import logging
import signal
import sys
import time
from typing import Optional

import numpy as np
import tornado.httpserver
import tornado.ioloop
import tornado.web
import tornado.websocket

from . import DEFAULT_OFFLINE_MODEL, DEFAULT_STREAMING_MODEL, __version__
from .auth import BaseAuthenticatedHandler, authorize_request
from .llm_client import LLMClient
from .recognizer import SenseVoiceRecognizer, SpeechRecognizer, StreamingSpeechRecognizer

logger = logging.getLogger("VoiceTyper")


def is_sensevoice_model(model_name: Optional[str]) -> bool:
    """SenseVoice 系模型走独立的识别器实现（自带标点）。"""
    return bool(model_name) and model_name.lower().startswith("sensevoice")


def build_offline_recognizer(model_name: str, punc_model: Optional[str], args):
    """构造离线整段识别器。

    SenseVoice 自带标点与 ITN，会忽略 --punc-model；paraformer 则需要
    外挂 ct-punc 才有标点。
    """
    if is_sensevoice_model(model_name):
        if punc_model:
            logger.info(f"标点: SenseVoice 自带，已忽略 --punc-model {punc_model}")
        return SenseVoiceRecognizer(
            model_name=model_name,
            device=args.device,
            intra_op_num_threads=args.onnx_threads,
            language=args.sensevoice_language,
        )

    return SpeechRecognizer(
        model_name=model_name,
        punc_model=punc_model,
        device=args.device,
        intra_op_num_threads=args.onnx_threads,
    )


# ---------------------------------------------------------------------------
# /health
# ---------------------------------------------------------------------------

class HealthHandler(BaseAuthenticatedHandler):
    def get(self):
        recognizer  = self.application.settings.get("recognizer")
        llm_client  = self.application.settings.get("llm_client")
        streaming   = self.application.settings.get("streaming", True)
        models      = self.application.settings.get("models", {})
        self.write({
            "status":            "ok",
            "ready":             recognizer.is_ready if recognizer else False,
            "version":           __version__,
            "protocol_version":  2,
            "streaming":         streaming,
            "llm_enabled":       llm_client is not None,
            "asr_model":         models.get("asr"),
            "offline_model":     models.get("offline"),
            "punc_model":        models.get("punc"),
            "device":            models.get("device"),
        })


# ---------------------------------------------------------------------------
# 非流式：POST /recognize
# ---------------------------------------------------------------------------

class RecognizeHandler(BaseAuthenticatedHandler):
    """非流式语音识别接口（HTTP POST）。"""

    def _parse_request(self):
        ct = self.request.headers.get("Content-Type", "").lower()
        if ct.startswith("application/octet-stream"):
            audio_bytes = self.request.body
            llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
            return audio_bytes, llm_recorrect
        if "audio" not in self.request.files:
            raise tornado.web.HTTPError(400, reason="缺少 audio 文件")
        audio_file    = self.request.files["audio"][0]
        audio_bytes   = audio_file["body"]
        llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        return audio_bytes, llm_recorrect

    async def post(self):
        recognizer = self.application.settings.get("recognizer")
        llm_client  = self.application.settings.get("llm_client")

        try:
            audio_bytes, llm_recorrect = self._parse_request()

            if len(audio_bytes) > 64 * 1024 * 1024:
                self.set_status(413)
                self.write({"error": "音频文件过大，最大支持 64MB"})
                return

            try:
                audio = np.frombuffer(audio_bytes, dtype=np.float32)
            except (ValueError, TypeError) as exc:
                logger.warning(f"音频格式无效: {exc}")
                self.set_status(400)
                self.write({"error": "音频格式无效，需要 float32 格式"})
                return

            if len(audio) == 0:
                self.set_status(400)
                self.write({"error": "音频数据为空"})
                return

            if not recognizer or not recognizer.is_ready:
                self.set_status(503)
                self.write({"error": "服务未就绪"})
                return

            executor = self.application.settings.get("offline_executor")
            loop = asyncio.get_running_loop()
            t0 = time.time()
            text = await loop.run_in_executor(
                executor, recognizer.recognize, audio
            )
            elapsed = round(time.time() - t0, 3)

            llm_elapsed: Optional[float] = None
            if llm_recorrect and llm_client and text.strip():
                try:
                    t1 = time.time()
                    original = text
                    text = await llm_client.correct_text(text)
                    llm_elapsed = round(time.time() - t1, 3)
                    if original != text:
                        logger.info(f"LLM 修正完成 ({llm_elapsed}s)")
                        logger.debug(f"LLM 修正: {original!r} → {text!r}")
                    else:
                        logger.info(f"LLM 修正: 无需改动 ({llm_elapsed}s)")
                except Exception as exc:
                    logger.warning(f"LLM 修正失败，使用原始文本: {exc}")

            result = {"text": text, "duration": round(len(audio) / 16000, 2), "elapsed": elapsed}
            if llm_elapsed is not None:
                result["llmElapsed"] = llm_elapsed
            self.write(result)

        except tornado.web.HTTPError as exc:
            self.set_status(exc.status_code)
            self.write({"error": exc.reason or str(exc)})
        except Exception as exc:
            self.set_status(500)
            self.write({"error": str(exc)})


# ---------------------------------------------------------------------------
# 流式：WebSocket /recognize/stream
# ---------------------------------------------------------------------------

class StreamRecognizeHandler(tornado.websocket.WebSocketHandler):
    """
    流式语音识别 WebSocket 端点。

    协议（详见仓库根目录 PROTOCOL.md）：
      Client → Server:
        连接后立即  text:   {"type":"start","sample_rate":16000}
        录音中每 600ms  binary: float32 PCM，9600 samples = 38400 bytes
        松开热键   text:   {"type":"finalize"}
      Server → Client:
        预览更新   text: {"type":"partial","text":"今天天气","seq":N}
                       注意：text 是**全量文本**，客户端直接替换预览即可。
                       整段重跑会修正先前的文字，因此不能是增量。
        非致命提示 text: {"type":"warning","code":"...","message":"..."}
                       feed 阶段单帧异常等，连接保持，finalize 仍可继续。
        完成时     text: {"type":"final","text":"今天天气不错。","asrElapsed":0.82}
        致命错误   text: {"type":"error","code":"...","message":"..."}（之后 close）
        正常结束   close (1000)
    """

    def check_origin(self, origin):
        # 原生客户端（macOS/Windows URLSession/WinHTTP）建立 WS 时不发送 Origin 头，
        # Tornado 仅在存在 Origin 头时才调用本方法。带 Origin 的连接基本来自浏览器页面，
        # 一律拒绝，防止任意网页连上本机 ws://127.0.0.1 白嫖识别算力。
        # 若将来新增 Web 客户端，需在此放行其同源 Origin。
        return False

    def prepare(self):
        if not authorize_request(self.request, self.application.settings):
            self.set_status(401)
            self.finish({"error": "Unauthorized"})

    def open(self):
        recognizer = self.application.settings.get("recognizer")
        if not recognizer or not recognizer.is_ready:
            self.close(code=4503, reason="service not ready")
            return
        self.llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        self.session = None
        self.seq = 0
        self.last_preview = ""
        self.preview_task: Optional[asyncio.Task] = None
        self.finalizing = False
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
            await self._send_warning("no_session", "audio received before start")
            return

        # Tornado 会等 on_message 的协程跑完才读下一条消息，所以这里只做
        # 便宜的累积，推理放到后台任务里——否则一次慢预览会把后续音频
        # 堵在接收缓冲区，预览越拖越远。
        self.session.append(np.frombuffer(data, dtype=np.float32))
        self._schedule_preview()

    def _schedule_preview(self):
        """启动一次预览；已有预览在跑就跳过。

        跳过是有意的：积压的音频会在下一次预览时一并处理。这样慢机器或长录音
        下预览自然变稀疏，而不会让推理队列无限堆积。
        """
        if self.finalizing:
            return
        if self.preview_task is not None and not self.preview_task.done():
            return
        self.preview_task = asyncio.create_task(self._run_preview())

    async def _run_preview(self):
        session = self.session
        if session is None:
            return

        loop = asyncio.get_running_loop()
        # 流式预览用独立 executor：避免预览排在其它会话漫长的离线 finalize 之后。
        # 用 SenseVoice 时两者是同一个 executor（同一份模型与前端状态需串行）。
        executor = self.application.settings.get("stream_executor")
        try:
            text = await loop.run_in_executor(executor, session.preview)
        except Exception as exc:
            # 预览失败属于非致命错误：通知客户端但保留连接，
            # finalize 时还能对累积音频整段识别兜底。
            logger.warning("流式预览异常: %s", exc)
            await self._send_warning("feed_failed", str(exc))
            return

        # finalize 可能在推理期间发生；此时 final 已经发出，不能再补发 partial
        if self.finalizing or self.session is not session:
            return
        if text and text != self.last_preview:
            self.last_preview = text
            await self.write_message(json.dumps(
                {"type": "partial", "text": text, "seq": self.seq},
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
            self.session = recognizer.new_session()
            self.seq = 0
            self.last_preview = ""
            self.finalizing = False
            logger.info(f"会话开始，llm={self.llm_recorrect}")
        elif msg_type == "finalize":
            await self._do_finalize()
        else:
            logger.warning(f"未知控制帧 type={msg_type!r}")

    async def _do_finalize(self):
        if self.session is None:
            await self._send_error("bad_state", "session not started")
            self.close(code=4500)
            return

        # 关掉预览通道：在途的那次即使跑完也不会再发 partial，
        # 避免 final 之后又冒出一条过期预览。
        self.finalizing = True
        if self.preview_task is not None:
            self.preview_task.cancel()

        loop = asyncio.get_running_loop()
        executor = self.application.settings.get("offline_executor")

        t0 = time.time()
        text = await loop.run_in_executor(executor, self.session.finalize)
        asr_elapsed = round(time.time() - t0, 3)
        logger.info(f"离线复识别耗时: {asr_elapsed}s")
        logger.debug(f"离线复识别文本: {text!r}")

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
        except Exception as exc:
            logger.debug(f"_send_error 写入失败（连接可能已关闭）: {exc}")

    async def _send_warning(self, code: str, message: str):
        """非致命提示帧；连接保持，客户端可继续 finalize。"""
        try:
            await self.write_message(json.dumps(
                {"type": "warning", "code": code, "message": message},
                ensure_ascii=False,
            ))
        except Exception as exc:
            logger.debug(f"_send_warning 写入失败（连接可能已关闭）: {exc}")

    def on_close(self):
        self.finalizing = True
        if self.preview_task is not None:
            self.preview_task.cancel()
        self.session = None
        logger.info(f"WS 连接关闭，code={self.close_code}")


# ---------------------------------------------------------------------------
# 工厂 & 启动
# ---------------------------------------------------------------------------

def make_app(api_keys=None, server_host="127.0.0.1",
             recognizer=None, llm_client=None,
             stream_executor=None, offline_executor=None,
             streaming=True, models=None):
    if streaming:
        handlers = [
            (r"/health",            HealthHandler),
            (r"/recognize/stream",  StreamRecognizeHandler),
        ]
    else:
        handlers = [
            (r"/health",    HealthHandler),
            (r"/recognize", RecognizeHandler),
        ]
    app = tornado.web.Application(handlers)
    app.settings["api_keys"]    = api_keys or []
    app.settings["server_host"] = server_host
    app.settings["recognizer"]  = recognizer
    app.settings["llm_client"]  = llm_client
    # stream_executor: 流式 partial 预览；offline_executor: 离线复识别 / HTTP 识别。
    # 两者独立，使并发会话的实时预览不被漫长的离线 finalize 阻塞。
    app.settings["stream_executor"]  = stream_executor
    app.settings["offline_executor"] = offline_executor
    app.settings["streaming"]   = streaming
    app.settings["models"]      = models or {}
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
    def __init__(self, server, executors, llm_client):
        self.server   = server
        # 单模型流式下 stream/offline 是同一个 executor，按身份去重避免重复关闭
        self.executors = list({id(e): e for e in executors if e is not None}.values())
        self.llm_client = llm_client

    def shutdown(self):
        logger.info("停止服务...")
        if self.llm_client:
            self.llm_client.close()
        for executor in self.executors:
            executor.shutdown(wait=False)
        tornado.ioloop.IOLoop.current().stop()


def create_server(args) -> ServerContext:
    api_keys = load_api_keys(args.api_keys)
    streaming = args.streaming

    if api_keys:
        logger.info(f"API密钥: 已配置 {len(api_keys)} 个")
    elif args.host != "127.0.0.1":
        logger.warning("远程访问未配置 API 密钥，建议使用 --api-keys 参数")

    logger.info("=" * 50)
    logger.info("VoiceTyper 语音识别服务")
    logger.info("=" * 50)
    if streaming:
        logger.info(f"模式: 流式（WebSocket）")
        logger.info(f"地址: ws://{args.host}:{args.port}/recognize/stream")
    else:
        logger.info(f"模式: 非流式（HTTP）")
        logger.info(f"地址: http://{args.host}:{args.port}/recognize")
    logger.info("后端: onnx")
    logger.info(f"设备: {args.device}")
    if streaming:
        logger.info(f"Chunk: {args.chunk_size}")
    elif args.chunk_size != "0,10,5":
        logger.warning("--chunk-size 仅在流式模式下生效，当前为非流式模式，已忽略")
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

    # --model 未指定时按模式选默认值。
    # 非流式模式的最终结果直接由 --model 产出，因此默认用 SenseVoice；
    # 流式模式的 --model 只负责 partial 预览，仍用 paraformer 流式模型。
    model_name = args.model
    if not model_name:
        model_name = DEFAULT_STREAMING_MODEL if streaming else DEFAULT_OFFLINE_MODEL
    if not streaming:
        logger.info(f"模型: {model_name}")
    # 流式模式下预览用哪个模型要等离线模型定下来才知道（SenseVoice 会复用自己），
    # 因此那条日志推迟到下面的分支里打。

    logger.info("初始化模型...")
    t0 = time.time()

    offline_model_name: Optional[str] = None
    single_model_streaming = False
    if streaming:
        offline_model_name = args.offline_model or DEFAULT_OFFLINE_MODEL
        logger.info(f"离线复识别模型: {offline_model_name}（最终结果由它产出）")
        # 离线整段识别器：负责松手后的准确识别（含标点）
        offline_recognizer = build_offline_recognizer(offline_model_name, punc_model, args)
        offline_recognizer.initialize()

        # SenseVoice 的 RTF 约 0.01，重跑整段音频做预览比再加载一个流式模型更划算：
        # 少一个模型（约 227MB）、少一套流式 cache 逻辑，而且预览与最终文本出自
        # 同一个模型，措辞不会在松手瞬间跳变。
        single_model_streaming = isinstance(offline_recognizer, SenseVoiceRecognizer)
        if single_model_streaming:
            recognizer = offline_recognizer
            model_name = offline_model_name
            logger.info("流式预览: 复用 SenseVoice 整段重跑，不加载独立流式模型")
            for flag, value in (("--model", args.model),
                                ("--chunk-size", args.chunk_size if args.chunk_size != "0,10,5" else None)):
                if value:
                    logger.warning(f"{flag} {value} 在 SenseVoice 单模型模式下被忽略")
        else:
            logger.info(f"流式预览模型: {model_name}")
            chunk_size = [int(x) for x in args.chunk_size.split(",")]
            # 流式识别器：仅产出 partial 预览，标点交给离线模型
            recognizer = StreamingSpeechRecognizer(
                model_name=model_name,
                punc_model=None,
                device=args.device,
                chunk_size=chunk_size,
                intra_op_num_threads=args.onnx_threads,
                offline_recognizer=offline_recognizer,
            )
            recognizer.initialize()
    else:
        recognizer = build_offline_recognizer(model_name, punc_model, args)
        recognizer.initialize()

    logger.info(f"初始化完成，耗时 {time.time() - t0:.1f}s")

    # 最终文本由 offline_recognizer（流式）或 recognizer（非流式）产出；
    # 标点是内置还是外挂 ct-punc，取决于它。
    final_recognizer = offline_recognizer if streaming else recognizer
    builtin_punc = isinstance(final_recognizer, SenseVoiceRecognizer)
    effective_punc = None if builtin_punc else punc_model

    logger.info(f"标点: {'内置（SenseVoice）' if builtin_punc else (effective_punc or '已禁用')}")

    models_meta = {
        "asr":     model_name,
        "offline": offline_model_name if streaming else None,
        "punc":    effective_punc,
        "device":  args.device,
    }

    # 单 worker executor：ONNX 推理与 funasr 前端都有可变状态，必须串行。
    offline_executor = concurrent.futures.ThreadPoolExecutor(
        max_workers=1, thread_name_prefix="vt-offline"
    )
    if not streaming:
        stream_executor = None
    elif single_model_streaming:
        # 预览与终稿是同一个 SenseVoice 实例（同一份 ONNX session 和 fbank 前端），
        # 必须共用一个 worker 串行执行，不能各跑各的。
        stream_executor = offline_executor
    else:
        # 两个模型互相独立，各占一个 worker：预览不会排在别的会话
        # 漫长的离线 finalize 后面。
        stream_executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=1, thread_name_prefix="vt-stream"
        )
    app = make_app(
        api_keys=api_keys,
        server_host=args.host,
        recognizer=recognizer,
        llm_client=llm_client,
        stream_executor=stream_executor,
        offline_executor=offline_executor,
        streaming=streaming,
        models=models_meta,
    )
    server = tornado.httpserver.HTTPServer(app, max_buffer_size=64 * 1024 * 1024)
    server.listen(args.port, args.host)

    if streaming:
        logger.info(f"服务已启动: ws://{args.host}:{args.port}/recognize/stream")
    else:
        logger.info(f"服务已启动: http://{args.host}:{args.port}")

    return ServerContext(server, [stream_executor, offline_executor], llm_client)


def run_server(args):
    configure_logging()
    ctx = create_server(args)
    logger.info("按 Ctrl+C 停止服务")

    def shutdown(signum, frame):
        ctx.shutdown()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    tornado.ioloop.IOLoop.current().start()
