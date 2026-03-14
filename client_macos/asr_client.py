"""
ASR 服务客户端
"""
import json
import logging
import numpy as np
from typing import Optional
from urllib.parse import quote
from tornado.httpclient import HTTPClient, HTTPError


logger = logging.getLogger("VoiceTyper")


class ASRClient:
    """语音识别服务客户端"""

    def __init__(self, host: str = "127.0.0.1", port: int = 6008, timeout: float = 30.0, api_key: Optional[str] = None, llm_recorrect: bool = False):
        self.base_url = f"http://{host}:{port}"
        self.timeout = timeout
        self.api_key = api_key
        self.host = host
        self.llm_recorrect = llm_recorrect
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
        except (json.JSONDecodeError, Exception):
            return False
    
    def recognize(self, audio: np.ndarray, hotwords: str = "") -> Optional[str]:
        """识别音频"""
        try:
            # 发送请求
            headers = self._get_auth_headers()
            headers["Content-Type"] = "application/octet-stream"
            if hotwords:
                headers["X-Hotwords"] = quote(hotwords, safe="")

            response = self._client.fetch(
                f"{self.base_url}/recognize?llm_recorrect={'true' if self.llm_recorrect else 'false'}",
                method="POST",
                headers=headers,
                body=audio.tobytes(),
                request_timeout=self.timeout,
            )
            
            # 解析响应 - 确保使用 UTF-8 解码
            data = json.loads(response.body.decode('utf-8'))
            return data.get("text", "")
            
        except HTTPError as e:
            logger.warning(f"ASR 请求失败: HTTP {e.code}")
            return None
        except json.JSONDecodeError:
            logger.error("ASR 响应解析失败")
            return None
        except Exception as e:
            logger.error(f"ASR 请求错误: {e}")
            return None
    
    def close(self):
        self._client.close()
