"""
ASR 服务客户端
"""
import json
import uuid
import numpy as np
from typing import Optional
from tornado.httpclient import HTTPClient, HTTPError


class ASRClient:
    """语音识别服务客户端"""

    def __init__(self, host: str = "127.0.0.1", port: int = 6008, timeout: float = 30.0, api_key: Optional[str] = None):
        self.base_url = f"http://{host}:{port}"
        self.timeout = timeout
        self.api_key = api_key
        self.host = host
        self._client = HTTPClient()
    
    def _get_auth_headers(self) -> dict:
        """获取鉴权请求头"""
        headers = {}
        # 只有在非本地地址且有API key时才添加鉴权头
        if self.host != "127.0.0.1" and self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def health_check(self) -> bool:
        """检查服务是否可用"""
        try:
            headers = self._get_auth_headers()
            response = self._client.fetch(
                f"{self.base_url}/health",
                method="GET",
                headers=headers,
                request_timeout=5.0,
            )
            data = json.loads(response.body.decode('utf-8'))
            return data.get("ready", False)
        except Exception:
            return False
    
    def recognize(self, audio: np.ndarray, hotwords: str = "") -> Optional[str]:
        """识别音频"""
        try:
            # 构建 multipart/form-data
            boundary = f"----VoiceTyper{uuid.uuid4().hex}"
            
            body_parts = []
            
            # 音频文件
            body_parts.append(f"--{boundary}".encode())
            body_parts.append(b'Content-Disposition: form-data; name="audio"; filename="audio.raw"')
            body_parts.append(b'Content-Type: application/octet-stream')
            body_parts.append(b'')
            body_parts.append(audio.tobytes())
            
            # 热词
            if hotwords:
                body_parts.append(f"--{boundary}".encode())
                body_parts.append(b'Content-Disposition: form-data; name="hotwords"')
                body_parts.append(b'')
                body_parts.append(hotwords.encode('utf-8'))
            
            # 结束
            body_parts.append(f"--{boundary}--".encode())
            body_parts.append(b'')
            
            body = b'\r\n'.join(body_parts)
            
            # 发送请求
            headers = self._get_auth_headers()
            headers["Content-Type"] = f"multipart/form-data; boundary={boundary}"

            response = self._client.fetch(
                f"{self.base_url}/recognize",
                method="POST",
                headers=headers,
                body=body,
                request_timeout=self.timeout,
            )
            
            # 解析响应 - 确保使用 UTF-8 解码
            data = json.loads(response.body.decode('utf-8'))
            return data.get("text", "")
            
        except HTTPError as e:
            print(f"ASR 请求失败: HTTP {e.code}")
            return None
        except Exception as e:
            print(f"ASR 请求错误: {e}")
            return None
    
    def close(self):
        self._client.close()