"""WebSocket 状态机：PROTOCOL.md 里 start/audio/finalize 的关键路径。

用 tornado.testing.AsyncHTTPTestCase 起一个真实的（但不含模型的）WS 服务，
FakeRecognizer/FakeSession 顶替真实 ONNX 识别器。覆盖 S-01/S-02/S-03 修复
后的行为：畸形帧不判死刑、无 session 不炸、会话上限只丢帧不断连。
"""
import json

import numpy as np
from tornado.testing import AsyncHTTPTestCase, gen_test
from tornado.websocket import websocket_connect

from voice_typer_server.app import StreamRecognizeHandler, make_app


class FakeSession:
    """顶替 SenseVoiceSession/ParaformerStreamingSession，不碰任何模型。"""

    def __init__(self):
        self._n = 0

    @property
    def n_samples(self) -> int:
        return self._n

    def append(self, chunk) -> None:
        self._n += len(chunk)

    def preview(self) -> str:
        return "预览文本"

    def finalize(self) -> str:
        return "最终文本"


class FakeRecognizer:
    is_ready = True

    def new_session(self) -> FakeSession:
        return FakeSession()


def _audio_frame(n_samples: int = 4) -> bytes:
    return np.zeros(n_samples, dtype=np.float32).tobytes()


class WSProtocolTest(AsyncHTTPTestCase):
    def get_app(self):
        return make_app(
            recognizer=FakeRecognizer(),
            streaming=True,
            stream_executor=None,
            offline_executor=None,
        )

    def get_ws_url(self, path: str = "/recognize/stream") -> str:
        return f"ws://127.0.0.1:{self.get_http_port()}{path}"

    async def _connect(self):
        return await websocket_connect(self.get_ws_url())

    async def _read_until(self, conn, msg_type: str, max_frames: int = 20) -> dict:
        """跳过无关帧（如 partial），读到指定 type 为止。"""
        for _ in range(max_frames):
            raw = await conn.read_message()
            assert raw is not None, f"连接过早关闭，等的是 type={msg_type!r}"
            msg = json.loads(raw)
            if msg.get("type") == msg_type:
                return msg
        raise AssertionError(f"读了 {max_frames} 帧都没等到 type={msg_type!r}")

    @gen_test
    async def test_start_audio_finalize_happy_path(self):
        conn = await self._connect()
        await conn.write_message(json.dumps({"type": "start", "sample_rate": 16000}))
        await conn.write_message(_audio_frame(), binary=True)
        await conn.write_message(json.dumps({"type": "finalize"}))

        final = await self._read_until(conn, "final")
        assert final["text"] == "最终文本"
        assert "asrElapsed" in final

        # finalize 后服务端主动 close(1000)
        assert await conn.read_message() is None

    @gen_test
    async def test_audio_before_start_warns_without_closing(self):
        conn = await self._connect()
        await conn.write_message(_audio_frame(), binary=True)

        warning = await self._read_until(conn, "warning")
        assert warning["code"] == "no_session"

        # 连接仍然存活：随后正常 start → finalize 应该照常工作
        await conn.write_message(json.dumps({"type": "start", "sample_rate": 16000}))
        await conn.write_message(json.dumps({"type": "finalize"}))
        final = await self._read_until(conn, "final")
        assert final["text"] == "最终文本"

    @gen_test
    async def test_malformed_frame_warns_and_session_survives(self):
        conn = await self._connect()
        await conn.write_message(json.dumps({"type": "start", "sample_rate": 16000}))
        # float32 PCM 必须是 4 字节的倍数；3 字节是畸形帧
        await conn.write_message(b"\x00\x01\x02", binary=True)

        warning = await self._read_until(conn, "warning")
        assert warning["code"] == "bad_frame"

        # 丢的是这一帧，不是整个会话：finalize 仍可用
        await conn.write_message(json.dumps({"type": "finalize"}))
        final = await self._read_until(conn, "final")
        assert final["text"] == "最终文本"

    @gen_test
    async def test_bad_sample_rate_errors_and_closes(self):
        conn = await self._connect()
        await conn.write_message(json.dumps({"type": "start", "sample_rate": 8000}))

        error = await self._read_until(conn, "error")
        assert error["code"] == "bad_request"

        assert await conn.read_message() is None
        assert conn.close_code == 4400

    @gen_test
    async def test_session_capped_stops_accepting_audio(self):
        # StreamRecognizeHandler.MAX_SESSION_SAMPLES 是类属性（S-01 修复的兜底方式）
        # 兜底，天然可以在测试里临时调小，不用真的灌 300 秒音频。
        original_cap = StreamRecognizeHandler.MAX_SESSION_SAMPLES
        StreamRecognizeHandler.MAX_SESSION_SAMPLES = 4
        try:
            conn = await self._connect()
            await conn.write_message(json.dumps({"type": "start", "sample_rate": 16000}))
            await conn.write_message(_audio_frame(4), binary=True)  # n_samples -> 4，达到上限
            await conn.write_message(_audio_frame(4), binary=True)  # 达到/超过上限，本帧应被丢弃

            warning = await self._read_until(conn, "warning")
            assert warning["code"] == "session_capped"

            # 会话仍保留，finalize 仍可用已累积部分产出结果
            await conn.write_message(json.dumps({"type": "finalize"}))
            final = await self._read_until(conn, "final")
            assert final["text"] == "最终文本"
        finally:
            StreamRecognizeHandler.MAX_SESSION_SAMPLES = original_cap
