"""
简单的 OpenAI 兼容 LLM 客户端
"""
from importlib import resources
import json
import logging

from tornado.httpclient import AsyncHTTPClient, HTTPError, HTTPRequest

logger = logging.getLogger("VoiceTyper")


class LLMClient:
    """OpenAI 兼容的 LLM 客户端"""

    def __init__(
        self,
        base_url: str,
        api_key: str,
        model: str,
        temperature: float = 0.3,
        max_tokens: int = 800,
    ):
        """初始化 LLM 客户端"""
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.model = model
        self.temperature = temperature
        self.max_tokens = max_tokens
        self.http_client = AsyncHTTPClient()
        self.system_prompt = self._load_system_prompt()

    def _load_system_prompt(self) -> str:
        """从包资源中加载提示词"""
        try:
            prompt_path = resources.files("voice_typer_server.prompts").joinpath("correction.md")
            return prompt_path.read_text(encoding="utf-8").strip()
        except FileNotFoundError:
            logger.error("无法找到 LLM 提示词文件: prompts/correction.md")
        except Exception as exc:
            logger.error(f"读取 LLM 提示词失败: {exc}")

        return "你是训练有素的文本校对员，请修正识别文本中的错别字并返回纯文本。"

    async def correct_text(self, text: str) -> str:
        """使用 LLM 修正识别文本中的显著错误"""
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": self.system_prompt},
                {"role": "user", "content": text},
            ],
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
        }

        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}",
        }
        url = f"{self.base_url}/chat/completions"

        try:
            request = HTTPRequest(
                url=url,
                method="POST",
                headers=headers,
                body=json.dumps(payload),
                request_timeout=8.0,
            )
            response = await self.http_client.fetch(request)
            result = json.loads(response.body.decode("utf-8"))
            return result["choices"][0]["message"]["content"].strip()
        except HTTPError as exc:
            error_body = exc.response.body.decode("utf-8") if exc.response else str(exc)
            logger.error(f"LLM API 错误 ({exc.code}): {error_body}")
            raise Exception(f"LLM API 错误 ({exc.code}): {error_body}") from exc
        except Exception as exc:
            logger.error(f"LLM 调用失败: {exc}")
            raise Exception(f"LLM 调用失败: {exc}") from exc

    def close(self):
        """关闭 HTTP 客户端，释放资源"""
        if self.http_client:
            self.http_client.close()
            self.http_client = None
