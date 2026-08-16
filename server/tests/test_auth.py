"""鉴权矩阵：PROTOCOL.md 里的三行规则表，逐条钉住。

不需要 ONNX 模型——authorize_request 只读 settings dict 和一个假的
request 对象，没有任何模型依赖。
"""
from types import SimpleNamespace

import pytest

from voice_typer_server.auth import authorize_request


def _request(headers=None, remote_ip="127.0.0.1"):
    return SimpleNamespace(headers=headers or {}, remote_ip=remote_ip)


def test_no_api_keys_always_passes():
    settings = {"api_keys": [], "server_host": "0.0.0.0"}
    assert authorize_request(_request(), settings) is True


def test_loopback_host_skips_auth_even_with_keys():
    settings = {"api_keys": ["secret"], "server_host": "127.0.0.1"}
    assert authorize_request(_request(), settings) is True


def test_remote_host_requires_bearer_token():
    settings = {"api_keys": ["secret"], "server_host": "0.0.0.0"}
    req = _request(headers={"Authorization": "Bearer secret"})
    assert authorize_request(req, settings) is True


def test_remote_host_rejects_wrong_token():
    settings = {"api_keys": ["secret"], "server_host": "0.0.0.0"}
    req = _request(headers={"Authorization": "Bearer wrong"})
    assert authorize_request(req, settings) is False


def test_remote_host_rejects_missing_header():
    settings = {"api_keys": ["secret"], "server_host": "0.0.0.0"}
    assert authorize_request(_request(), settings) is False


def test_remote_host_rejects_non_bearer_scheme():
    settings = {"api_keys": ["secret"], "server_host": "0.0.0.0"}
    req = _request(headers={"Authorization": "Basic secret"})
    assert authorize_request(req, settings) is False


def test_accepts_any_of_multiple_configured_keys():
    settings = {"api_keys": ["k1", "k2"], "server_host": "0.0.0.0"}
    req = _request(headers={"Authorization": "Bearer k2"})
    assert authorize_request(req, settings) is True


@pytest.mark.parametrize("host", ["0.0.0.0", "192.168.1.10"])
def test_non_loopback_hosts_all_require_auth(host):
    settings = {"api_keys": ["secret"], "server_host": host}
    assert authorize_request(_request(), settings) is False
