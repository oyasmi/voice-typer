"""
鉴权基类模块
"""
import logging
from typing import Optional

import tornado.web

logger = logging.getLogger("VoiceTyper")


def is_local_request(remote_ip: Optional[str]) -> bool:
    """检查请求是否来自本地地址"""
    if not remote_ip:
        return False
    return remote_ip in ("127.0.0.1", "::1")


class BaseAuthenticatedHandler(tornado.web.RequestHandler):
    """鉴权基类，提供 API key 验证功能"""

    def prepare(self):
        """设置响应头"""
        self.set_header("Content-Type", "application/json; charset=utf-8")

    def write_error(self, status_code: int, **kwargs):
        """自定义错误响应，返回 JSON 格式"""
        self.set_header("Content-Type", "application/json; charset=utf-8")
        if status_code == 401:
            self.finish({"error": "Unauthorized"})
            return
        super().write_error(status_code, **kwargs)

    def get_current_user(self):
        """获取当前用户，返回 API key 或 None"""
        if is_local_request(self.request.remote_ip):
            return True

        auth_header = self.request.headers.get("Authorization")
        if not auth_header or not auth_header.startswith("Bearer "):
            logger.warning(f"鉴权失败: 缺少或无效的 Authorization 头 | {self.request.remote_ip}")
            return None

        token = auth_header[7:]
        api_keys = self.application.settings.get("api_keys", [])
        if token not in api_keys:
            logger.warning(f"鉴权失败: 无效的 API 密钥 | {self.request.remote_ip}")
            return None

        return token
