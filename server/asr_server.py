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
import numpy as np

import tornado.ioloop
import tornado.web
import tornado.httpserver

from recognizer import SpeechRecognizer
from auth import BaseAuthenticatedHandler
from llm_client import LLMClient

recognizer: SpeechRecognizer = None
llm_client: LLMClient = None


class HealthHandler(BaseAuthenticatedHandler):
    """健康检查"""

    @tornado.web.authenticated
    def get(self):
        self.write({
            "status": "ok",
            "ready": recognizer.is_ready if recognizer else False,
            "llm_enabled": llm_client is not None,
        })


class RecognizeHandler(BaseAuthenticatedHandler):
    """语音识别接口 - 支持 multipart/form-data"""

    @tornado.web.authenticated
    async def post(self):
        try:
            # 获取音频文件
            if "audio" not in self.request.files:
                self.set_status(400)
                self.write({"error": "缺少 audio 文件"})
                return
            
            audio_file = self.request.files["audio"][0]
            audio_bytes = audio_file["body"]
            
            # 转换为 numpy 数组 (float32, 16kHz)
            audio = np.frombuffer(audio_bytes, dtype=np.float32)
            
            # 获取参数
            hotwords = self.get_argument("hotwords", "")
            llm_recorrect = self.get_argument("llm_recorrect", "true").lower() == "true"
            print("llm_recorrect:", llm_recorrect)
            
            # 检查服务状态
            if not recognizer or not recognizer.is_ready:
                self.set_status(503)
                self.write({"error": "服务未就绪"})
                return
            
            # 识别
            t0 = time.time()
            text = recognizer.recognize(audio, hotwords)
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
                        print("=" * 60)
                        print(f"LLM 修正 (耗时: {llm_elapsed}s)")
                        print(f"修正前: {original_text}")
                        print(f"修正后: {text}")
                        print("=" * 60)
                    else:
                        print(f"LLM 修正: 无需修改 (耗时: {llm_elapsed}s)")
                        
                except Exception as e:
                    print(f"LLM 修正失败: {e}")
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
            
        except Exception as e:
            self.set_status(500)
            self.write({"error": str(e)})


def make_app(api_keys=None, server_host="127.0.0.1"):
    """创建Tornado应用

    Args:
        api_keys: API key列表
        server_host: 服务器监听地址
    """
    app = tornado.web.Application([
        (r"/health", HealthHandler),
        (r"/recognize", RecognizeHandler),
    ])

    # 将配置存储到应用设置中
    app.settings['api_keys'] = api_keys or []
    app.settings['server_host'] = server_host

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
    parser.add_argument("--device", default="cpu", help="设备: mps, cpu")
    parser.add_argument("--api-keys", help="API 密钥（逗号分隔多个密钥）")
    
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
        print(f"API 密钥: 已配置 {len(api_keys)} 个密钥")
    elif args.host != "127.0.0.1":
        print("警告: 远程访问未配置API密钥，建议使用 --api-keys 参数")

    print("=" * 50)
    print("VoiceTyper 语音识别服务")
    print("=" * 50)
    print()
    print(f"地址: http://{args.host}:{args.port}")
    print(f"模型: {args.model}")
    print(f"标点: {args.punc_model}")
    print(f"设备: {args.device}")
    if args.host == "127.0.0.1":
        print("鉴权: 本地地址，已跳过")
    else:
        print(f"鉴权: {'已启用' if api_keys else '未启用（不安全）'}")
    
    # 初始化 LLM 客户端
    global llm_client
    if args.llm_base_url and args.llm_api_key:
        print(f"LLM: 已启用 ({args.llm_model})")
        llm_client = LLMClient(
            base_url=args.llm_base_url,
            api_key=args.llm_api_key,
            model=args.llm_model,
            temperature=args.llm_temperature,
            max_tokens=args.llm_max_tokens,
        )
    else:
        print("LLM: 未启用")
    
    print()
    
    global recognizer
    punc = args.punc_model if args.punc_model != "none" else None
    recognizer = SpeechRecognizer(
        model_name=args.model,
        punc_model=punc,
        device=args.device,
    )
    
    print("初始化模型...")
    t0 = time.time()
    recognizer.initialize(log=print)
    print(f"\n初始化完成，耗时 {time.time() - t0:.1f}s\n")
    
    app = make_app(api_keys=api_keys, server_host=args.host)
    server = tornado.httpserver.HTTPServer(app, max_buffer_size=100*1024*1024)  # 100MB
    server.listen(args.port, args.host)
    
    print(f"服务已启动: http://{args.host}:{args.port}")
    print("Ctrl+C 停止")
    print()
    
    def shutdown(signum, frame):
        print("\n停止服务...")
        tornado.ioloop.IOLoop.current().stop()
        import sys
        sys.exit(0)
    
    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    
    tornado.ioloop.IOLoop.current().start()


if __name__ == "__main__":
    main()