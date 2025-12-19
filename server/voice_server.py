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

recognizer: SpeechRecognizer = None


class HealthHandler(tornado.web.RequestHandler):
    """健康检查"""
    
    def get(self):
        self.write({
            "status": "ok",
            "ready": recognizer.is_ready if recognizer else False,
        })


class RecognizeHandler(tornado.web.RequestHandler):
    """语音识别接口 - 支持 multipart/form-data"""
    
    def post(self):
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
            
            # 获取热词（从表单字段）
            hotwords = self.get_argument("hotwords", "")
            
            # 检查服务状态
            if not recognizer or not recognizer.is_ready:
                self.set_status(503)
                self.write({"error": "服务未就绪"})
                return
            
            # 识别
            t0 = time.time()
            text = recognizer.recognize(audio, hotwords)
            elapsed = time.time() - t0
            
            # 返回结果
            self.write({
                "text": text,
                "duration": round(len(audio) / 16000, 2),
                "elapsed": round(elapsed, 3),
            })
            
        except Exception as e:
            self.set_status(500)
            self.write({"error": str(e)})


def make_app():
    return tornado.web.Application([
        (r"/health", HealthHandler),
        (r"/recognize", RecognizeHandler),
    ])


def main():
    parser = argparse.ArgumentParser(description="VoiceTyper 语音识别服务")
    parser.add_argument("--host", default="127.0.0.1", help="监听地址")
    parser.add_argument("--port", type=int, default=6008, help="监听端口")
    parser.add_argument("--model", default="paraformer-zh", help="ASR 模型")
    parser.add_argument("--punc-model", default="ct-punc", help="标点模型 (none 禁用)")
    parser.add_argument("--device", default="mps", help="设备: mps, cpu")
    args = parser.parse_args()
    
    print("=" * 50)
    print("VoiceTyper 语音识别服务")
    print("=" * 50)
    print()
    print(f"地址: http://{args.host}:{args.port}")
    print(f"模型: {args.model}")
    print(f"标点: {args.punc_model}")
    print(f"设备: {args.device}")
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
    
    app = make_app()
    server = tornado.httpserver.HTTPServer(app, max_buffer_size=100*1024*1024)  # 100MB
    server.listen(args.port, args.host)
    
    print(f"服务已启动: http://{args.host}:{args.port}")
    print("Ctrl+C 停止")
    print()
    
    def shutdown(signum, frame):
        print("\n停止服务...")
        tornado.ioloop.IOLoop.current().stop()
    
    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    
    tornado.ioloop.IOLoop.current().start()


if __name__ == "__main__":
    main()