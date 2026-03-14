"""
VoiceTyper 语音识别服务装配与启动
"""
import asyncio
import concurrent.futures
import logging
import signal
import sys
import time
from typing import Optional
from urllib.parse import unquote

import numpy as np
import tornado.httpserver
import tornado.ioloop
import tornado.web

from .auth import BaseAuthenticatedHandler
from .llm_client import LLMClient
from .recognizer import SpeechRecognizer

logger = logging.getLogger("VoiceTyper")


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
        })


class RecognizeHandler(BaseAuthenticatedHandler):
    """语音识别接口"""

    def _parse_audio_request(self):
        """解析请求中的音频和附加参数"""
        content_type = self.request.headers.get("Content-Type", "").lower()

        if content_type.startswith("application/octet-stream"):
            audio_bytes = self.request.body
            hotwords = unquote(self.request.headers.get("X-Hotwords", ""))
            llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
            return audio_bytes, hotwords, llm_recorrect

        if "audio" not in self.request.files:
            raise tornado.web.HTTPError(400, reason="缺少 audio 文件")

        audio_file = self.request.files["audio"][0]
        audio_bytes = audio_file["body"]
        hotwords = self.get_argument("hotwords", "")
        llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        return audio_bytes, hotwords, llm_recorrect

    @tornado.web.authenticated
    async def post(self):
        recognizer = self.application.settings.get("recognizer")
        llm_client = self.application.settings.get("llm_client")

        try:
            audio_bytes, hotwords, llm_recorrect = self._parse_audio_request()

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

            executor = self.application.settings.get("executor")
            loop = asyncio.get_event_loop()
            t0 = time.time()
            text = await loop.run_in_executor(executor, recognizer.recognize, audio, hotwords)
            elapsed = time.time() - t0

            llm_elapsed: Optional[float] = None
            if llm_recorrect and llm_client and text.strip():
                try:
                    t1 = time.time()
                    original_text = text
                    text = await llm_client.correct_text(text)
                    llm_elapsed = round(time.time() - t1, 3)

                    if original_text != text:
                        logger.info(f"LLM修正耗时: {llm_elapsed}s")
                        logger.info(f"  修正前: {original_text}")
                        logger.info(f"  修正后: {text}")
                    else:
                        logger.info(f"LLM修正: 无需修改 (耗时:{llm_elapsed}s)")
                except Exception as exc:
                    logger.warning(f"LLM修正失败: {exc}")

            result = {
                "text": text,
                "duration": round(len(audio) / 16000, 2),
                "elapsed": round(elapsed, 3),
            }
            if llm_elapsed is not None:
                result["llmElapsed"] = llm_elapsed

            self.write(result)
        except tornado.web.HTTPError as exc:
            self.set_status(exc.status_code)
            self.write({"error": exc.reason or str(exc)})
        except Exception as exc:
            self.set_status(500)
            self.write({"error": str(exc)})


def make_app(api_keys=None, server_host="127.0.0.1", recognizer=None, llm_client=None, executor=None):
    """创建 Tornado 应用"""
    app = tornado.web.Application([
        (r"/health", HealthHandler),
        (r"/recognize", RecognizeHandler),
    ])
    app.settings["api_keys"] = api_keys or []
    app.settings["server_host"] = server_host
    app.settings["recognizer"] = recognizer
    app.settings["llm_client"] = llm_client
    app.settings["executor"] = executor
    return app


def load_api_keys(api_keys_arg: Optional[str]):
    """从命令行参数加载 API keys"""
    if not api_keys_arg:
        return []
    return [key.strip() for key in api_keys_arg.split(",") if key.strip()]


def configure_logging():
    """配置日志"""
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s - %(levelname)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        stream=sys.stdout,
    )


def run_server(args):
    """启动服务"""
    configure_logging()

    api_keys = load_api_keys(args.api_keys)
    if api_keys:
        logger.info(f"API密钥: 已配置 {len(api_keys)} 个")
    elif args.host != "127.0.0.1":
        logger.warning("远程访问未配置 API 密钥，建议使用 --api-keys 参数")

    logger.info("=" * 50)
    logger.info("VoiceTyper 语音识别服务")
    logger.info("=" * 50)
    logger.info(f"地址: http://{args.host}:{args.port}")
    logger.info("后端: onnx")
    logger.info(f"模型: {args.model}")
    logger.info(f"标点: {args.punc_model}")
    logger.info(f"设备: {args.device}")
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
    recognizer = SpeechRecognizer(
        model_name=args.model,
        punc_model=punc_model,
        device=args.device,
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
    server = tornado.httpserver.HTTPServer(app, max_buffer_size=64 * 1024 * 1024)
    server.listen(args.port, args.host)

    logger.info(f"服务已启动: http://{args.host}:{args.port}")
    logger.info("按 Ctrl+C 停止服务")

    def shutdown(signum, frame):
        logger.info("停止服务...")
        if llm_client:
            llm_client.close()
        executor.shutdown(wait=False)
        tornado.ioloop.IOLoop.current().stop()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    tornado.ioloop.IOLoop.current().start()
