"""
鉴权工具：HTTP 与 WebSocket 共用同一套规则。

规则（参考 PROTOCOL.md）：
  - 未配置 api_keys           → 任何请求放行
  - 配置了 api_keys 且 host=127.0.0.1 → 只接受本机进程，无需 Bearer
  - 配置了 api_keys 且 host 监听外部地址 → 必须携带 Authorization: Bearer <key>

无论 HTTP 还是 WS，都通过 `authorize_request()` 走同一条决策路径，
避免之前 HTTP/WS 两套实现语义不一致导致的鉴权绕过隐患。
"""
import logging

import tornado.web

logger = logging.getLogger("VoiceTyper")


def authorize_request(request, settings) -> bool:
    """统一鉴权入口。返回 True 表示放行。"""
    api_keys = settings.get("api_keys", [])
    if not api_keys:
        return True

    server_host = settings.get("server_host", "127.0.0.1")
    if server_host == "127.0.0.1":
        # 仅监听 loopback 时外部无法直连，本机进程视为受信
        return True

    auth_header = request.headers.get("Authorization", "")
    if auth_header.startswith("Bearer "):
        token = auth_header[len("Bearer "):]
        if token in api_keys:
            return True

    logger.warning("鉴权失败 | %s", request.remote_ip)
    return False


class BaseAuthenticatedHandler(tornado.web.RequestHandler):
    """HTTP 鉴权基类。"""

    def prepare(self):
        self.set_header("Content-Type", "application/json; charset=utf-8")
        if not authorize_request(self.request, self.application.settings):
            self.set_status(401)
            self.finish({"error": "Unauthorized"})

    def write_error(self, status_code: int, **kwargs):
        self.set_header("Content-Type", "application/json; charset=utf-8")
        if status_code == 401:
            self.finish({"error": "Unauthorized"})
            return
        super().write_error(status_code, **kwargs)
