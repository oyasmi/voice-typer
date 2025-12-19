"""
ASR 服务客户端 - 使用 multipart/form-data 传输音频
"""
import json
import uuid
import numpy as np
from typing import Optional
from tornado.httpclient import HTTPClient, HTTPError


class ASRClient:
    """语音识别服务客户端"""
    
    def __init__(self, host: str = "127.0.0.1", port: int = 6008, timeout: float = 30.0):
        self.base_url = f"http://{host}:{port}"
        self.timeout = timeout
        self._client = HTTPClient()
    
    def health_check(self) -> bool:
        """检查服务是否可用"""
        try:
            response = self._client.fetch(
                f"{self.base_url}/health",
                method="GET",
                request_timeout=5.0,
            )
            data = json.loads(response.body)
            return data.get("ready", False)
        except Exception:
            return False
    
    def recognize(self, audio: np.ndarray, hotwords: str = "") -> Optional[str]:
        """
        识别音频
        
        Args:
            audio: float32 音频数据，16kHz
            hotwords: 热词字符串，空格分隔
            
        Returns:
            识别结果文本，失败返回 None
        """
        try:
            # 构建 multipart/form-data
            boundary = f"----VoiceTyper{uuid.uuid4().hex}"
            
            body_parts = []
            
            # 音频文件部分
            body_parts.append(f"--{boundary}".encode())
            body_parts.append(b'Content-Disposition: form-data; name="audio"; filename="audio.raw"')
            body_parts.append(b'Content-Type: application/octet-stream')
            body_parts.append(b'')
            body_parts.append(audio.tobytes())
            
            # 热词字段部分
            if hotwords:
                body_parts.append(f"--{boundary}".encode())
                body_parts.append(b'Content-Disposition: form-data; name="hotwords"')
                body_parts.append(b'')
                body_parts.append(hotwords.encode('utf-8'))
            
            # 结束边界
            body_parts.append(f"--{boundary}--".encode())
            body_parts.append(b'')
            
            # 组装 body
            body = b'\r\n'.join(body_parts)
            
            # 发送请求
            response = self._client.fetch(
                f"{self.base_url}/recognize",
                method="POST",
                headers={
                    "Content-Type": f"multipart/form-data; boundary={boundary}",
                },
                body=body,
                request_timeout=self.timeout,
            )
            
            # 解析响应
            data = json.loads(response.body)
            return data.get("text", "")
            
        except HTTPError as e:
            print(f"ASR 请求失败: HTTP {e.code}")
            return None
        except Exception as e:
            print(f"ASR 请求错误: {e}")
            return None
    
    def close(self):
        """关闭客户端"""
        self._client.close()