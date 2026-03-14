#!/usr/bin/env python3
"""
VoiceTyper 语音识别服务
"""
import os
import sys
import json
import time
import signal
import argparse
import logging
import numpy as np
import concurrent.futures
import asyncio
from urllib.parse import unquote

import tornado.ioloop
import tornado.web
import tornado.httpserver

from recognizer import SpeechRecognizer
from auth import BaseAuthenticatedHandler
from llm_client import LLMClient

# 配置日志
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(levelname)s - %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
    stream=sys.stdout
)
logger = logging.getLogger("VoiceTyper")


class HealthHandler(BaseAuthenticatedHandler):
    """健康检查"""

    @tornado.web.authenticated
    def get(self):
        recognizer = self.application.settings.get('recognizer')
        llm_client = self.application.settings.get('llm_client')
        self.write({
            "status": "ok",
            "ready": recognizer.is_ready if recognizer else False,
            "llm_enabled": llm_client is not None,
        })


class RecognizeHandler(BaseAuthenticatedHandler):
    """语音识别接口 - 优先支持 octet-stream，兼容 multipart/form-data"""

    def _parse_audio_request(self):
        """解析请求中的音频和附加参数"""
        content_type = self.request.headers.get("Content-Type", "").lower()

        # 新协议：原始音频字节直接放在请求体中，减少表单解析开销
        if content_type.startswith("application/octet-stream"):
            audio_bytes = self.request.body
            hotwords = unquote(self.request.headers.get("X-Hotwords", ""))
            llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
            return audio_bytes, hotwords, llm_recorrect

        # 兼容旧客户端：继续支持 multipart/form-data 上传
        if "audio" not in self.request.files:
            raise tornado.web.HTTPError(400, reason="缺少 audio 文件")

        audio_file = self.request.files["audio"][0]
        audio_bytes = audio_file["body"]
        hotwords = self.get_argument("hotwords", "")
        llm_recorrect = self.get_argument("llm_recorrect", "false").lower() == "true"
        return audio_bytes, hotwords, llm_recorrect

    @tornado.web.authenticated
    async def post(self):
        recognizer = self.application.settings.get('recognizer')
        llm_client = self.application.settings.get('llm_client')

        try:
            # 解析请求
            audio_bytes, hotwords, llm_recorrect = self._parse_audio_request()

            # 验证数据大小 (64MB 限制)
            if len(audio_bytes) > 64 * 1024 * 1024:
                self.set_status(413)
                self.write({"error": "音频文件过大，最大支持 64MB"})
                return

            # 验证格式并转换为 numpy 数组 (float32, 16kHz)
            try:
                audio = np.frombuffer(audio_bytes, dtype=np.float32)
            except (ValueError, TypeError) as e:
                logger.warning(f"音频格式无效: {e}")
                self.set_status(400)
                self.write({"error": "音频格式无效，需要 float32 格式"})
                return

            # 验证数据长度
            if len(audio) == 0:
                self.set_status(400)
                self.write({"error": "音频数据为空"})
                return

            # 检查服务状态
            if not recognizer or not recognizer.is_ready:
                self.set_status(503)
                self.write({"error": "服务未就绪"})
                return
            
            # 识别 (使用单线程池避免阻塞 Tornado 主事件循环，并发请求将排队执行)
            executor = self.application.settings.get('executor')
            loop = asyncio.get_event_loop()
            t0 = time.time()
            text = await loop.run_in_executor(
                executor,
                recognizer.recognize,
                audio,
                hotwords
            )
            elapsed = time.time() - t0
            
            # LLM 修正
            llm_elapsed = None
            if llm_recorrect and llm_client and text.strip():
                try:
                    t1 = time.time()
                    original_text = text
                    text = await llm_client.correct_text(text)
                    llm_elapsed = round(time.time() - t1, 3)

                    # 输出修正前后对比
                    if original_text != text:
                        logger.info(f"LLM修正耗时: {llm_elapsed}s")
                        logger.info(f"  修正前: {original_text}")
                        logger.info(f"  修正后: {text}")
                    else:
                        logger.info(f"LLM修正: 无需修改 (耗时:{llm_elapsed}s)")

                except Exception as e:
                    logger.warning(f"LLM修正失败: {e}")
                    # 继续返回原始识别结果
                    pass
            
            # 返回结果
            result = {
                "text": text,
                "duration": round(len(audio) / 16000, 2),
                "elapsed": round(elapsed, 3),
            }
            
            if llm_elapsed is not None:
                result["llmElapsed"] = llm_elapsed
            
            self.write(result)
        except tornado.web.HTTPError as e:
            self.set_status(e.status_code)
            self.write({"error": e.reason or str(e)})
        except Exception as e:
            self.set_status(500)
            self.write({"error": str(e)})


def make_app(api_keys=None, server_host="127.0.0.1", recognizer=None, llm_client=None, executor=None):
    """创建Tornado应用

    Args:
        api_keys: API key列表
        server_host: 服务器监听地址
        recognizer: 语音识别器实例
        llm_client: LLM 客户端实例
        executor: 识别任务线程池
    """
    app = tornado.web.Application([
        (r"/health", HealthHandler),
        (r"/recognize", RecognizeHandler),
    ])

    # 将配置存储到应用设置中
    app.settings['api_keys'] = api_keys or []
    app.settings['server_host'] = server_host
    app.settings['recognizer'] = recognizer
    app.settings['llm_client'] = llm_client
    app.settings['executor'] = executor

    return app


def load_api_keys(args):
    """从命令行参数和环境变量加载API keys"""
    api_keys = []

    # 优先使用命令行参数
    if args.api_keys:
        api_keys = [key.strip() for key in args.api_keys.split(',') if key.strip()]

    return api_keys


def main():
    parser = argparse.ArgumentParser(description="VoiceTyper 语音识别服务")
    parser.add_argument("--host", default="127.0.0.1", help="监听地址")
    parser.add_argument("--port", type=int, default=6008, help="监听端口")
    parser.add_argument("--model", default="paraformer-zh", help="ASR 模型")
    parser.add_argument("--punc-model", default="ct-punc", help="标点模型 (none 禁用)")
    parser.add_argument("--device", default="cpu", help="设备: ONNX 后端支持 cpu/cuda")
    parser.add_argument("--api-keys", help="API 密钥（逗号分隔多个密钥）")
    parser.add_argument("--onnx-threads", type=int, default=4, help="ONNX 后端 intra-op 线程数")

    # LLM 相关参数
    parser.add_argument("--llm-base-url", help="LLM API 基础URL (如 https://api.openai.com/v1)")
    parser.add_argument("--llm-api-key", help="LLM API 密钥")
    parser.add_argument("--llm-model", default="gpt-4o-mini", help="LLM 模型名称")
    parser.add_argument("--llm-temperature", type=float, default=0.3, help="LLM 温度参数 (0-2)")
    parser.add_argument("--llm-max-tokens", type=int, default=600, help="LLM 最大生成token数")

    args = parser.parse_args()

    # 处理API keys
    api_keys = load_api_keys(args)
    if api_keys:
        logger.info(f"API密钥: 已配置 {len(api_keys)} 个")
    elif args.host != "127.0.0.1":
        logger.warning("远程访问未配置API密钥，建议使用 --api-keys 参数")

    logger.info("=" * 50)
    logger.info("VoiceTyper 语音识别服务")
    logger.info("=" * 50)
    logger.info(f"地址: http://{args.host}:{args.port}")
    logger.info("后端: onnx")
    logger.info(f"模型: {args.model}")
    logger.info(f"标点: {args.punc_model}")
    logger.info(f"设备: {args.device}")
    if args.host == "127.0.0.1":
        logger.info("鉴权: 本地地址，已跳过")
    else:
        logger.info(f"鉴权: {'已启用' if api_keys else '未启用（不安全）'}")

    # 初始化 LLM 客户端
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

    # 初始化识别器
    punc = args.punc_model if args.punc_model != "none" else None
    recognizer = SpeechRecognizer(
        model_name=args.model,
        punc_model=punc,
        device=args.device,
        intra_op_num_threads=args.onnx_threads,
    )

    logger.info("初始化模型...")
    t0 = time.time()
    recognizer.initialize()
    elapsed = time.time() - t0
    logger.info(f"初始化完成，耗时 {elapsed:.1f}s")
    
    # 创建全局线程池用于识别任务 (限制最大 worker 为 1，使请求排队处理，防止并发撑爆本地显存/内存)
    executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)

    # 创建应用并传递识别器和LLM客户端
    app = make_app(
        api_keys=api_keys,
        server_host=args.host,
        recognizer=recognizer,
        llm_client=llm_client,
        executor=executor
    )
    server = tornado.httpserver.HTTPServer(app, max_buffer_size=64*1024*1024)  # 64MB
    server.listen(args.port, args.host)

    logger.info(f"服务已启动: http://{args.host}:{args.port}")
    logger.info("按 Ctrl+C 停止服务")

    def shutdown(signum, frame):
        logger.info("停止服务...")
        # 清理资源
        if llm_client:
            llm_client.close()
        executor.shutdown(wait=False)
        tornado.ioloop.IOLoop.current().stop()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    tornado.ioloop.IOLoop.current().start()


if __name__ == "__main__":
    main()
