"""容器 HEALTHCHECK 探针：请求本机 /health。

401（配了 API_KEYS 且监听非 loopback，见 auth.py）也算健康——
它同样证明进程在监听并正常路由，只是需要 Bearer token 才能拿到详情。
"""
import os
import sys
import urllib.error
import urllib.request

url = f"http://127.0.0.1:{os.environ.get('PORT', '6008')}/health"

try:
    urllib.request.urlopen(url, timeout=3)
except urllib.error.HTTPError as exc:
    sys.exit(0 if exc.code == 401 else 1)
except Exception:
    sys.exit(1)
