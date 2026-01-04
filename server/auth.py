"""
鉴权基类模块 - 使用Tornado原生鉴权机制
"""
import tornado.web


def is_localhost(host: str) -> bool:
    """检查是否为IPv4本地地址127.0.0.1"""
    return host == "127.0.0.1"


class BaseAuthenticatedHandler(tornado.web.RequestHandler):
    """鉴权基类，提供API key验证功能"""

    def prepare(self):
        """设置响应头"""
        self.set_header("Content-Type", "application/json; charset=utf-8")

    def write_error(self, status_code: int, **kwargs):
        """自定义错误响应，返回JSON格式"""
        self.set_header("Content-Type", "application/json; charset=utf-8")
        if status_code == 401:
            self.finish({"error": "Unauthorized"})
        else:
            super().write_error(status_code, **kwargs)

    def get_current_user(self):
        """获取当前用户，返回API key或None

        Returns:
            str | bool | None: API key字符串，True（本地跳过），或None（验证失败）
        """
        # 检查是否为本地地址
        server_host = self.application.settings.get('server_host', '-')
        if is_localhost(server_host):
            return True

        # 提取Bearer token
        auth_header = self.request.headers.get("Authorization")
        if not auth_header or not auth_header.startswith("Bearer "):
            return None

        token = auth_header[7:]  # 移除"Bearer "前缀
        api_keys = self.application.settings.get('api_keys', [])

        return token if token in api_keys else None
        