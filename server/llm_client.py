#!/usr/bin/env python3
"""
简单的 OpenAI 兼容 LLM 客户端
"""
import json
import logging
import os
from tornado.httpclient import AsyncHTTPClient, HTTPRequest, HTTPError

logger = logging.getLogger("VoiceTyper")


class LLMClient:
    """OpenAI 兼容的 LLM 客户端"""
    
    def __init__(self, base_url, api_key, model, temperature=0.3, max_tokens=800):
        """
        初始化 LLM 客户端
        
        Args:
            base_url: API 基础URL，如 https://api.openai.com/v1
            api_key: API 密钥
            model: 模型名称，如 gpt-4o-mini
            temperature: 温度参数 (0-2)
            max_tokens: 最大生成token数
        """
        self.base_url = base_url.rstrip('/')
        self.api_key = api_key
        self.model = model
        self.temperature = temperature
        self.max_tokens = max_tokens
        self.http_client = AsyncHTTPClient()
        
        # Load system prompt from file
        current_dir = os.path.dirname(os.path.abspath(__file__))
        prompt_path = os.path.join(current_dir, "prompts", "correction.md")
        try:
            with open(prompt_path, "r", encoding="utf-8") as f:
                self.system_prompt = f.read().strip()
        except FileNotFoundError:
            logger.error(f"无法找到 LLM 提示词文件: {prompt_path}")
            self.system_prompt = "你是训练有素的文本校对员，请修正识别文本中的错别字并返回纯文本。" # Fallback
        
    async def correct_text(self, text):
        """
        使用 LLM 修正识别文本中的显著错误
        
        Args:
            text: 原始识别文本
            
        Returns:
            修正后的文本
        """
        messages = [
            {"role": "system", "content": self.system_prompt},
            {"role": "user", "content": text}
        ]
        
        payload = {
            "model": self.model,
            "messages": messages,
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
        }
        
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}"
        }
        
        url = f"{self.base_url}/chat/completions"
        
        try:
            request = HTTPRequest(
                url=url,
                method='POST',
                headers=headers,
                body=json.dumps(payload),
                request_timeout=8.0
            )
            
            response = await self.http_client.fetch(request)
            result = json.loads(response.body.decode('utf-8'))
            corrected = result['choices'][0]['message']['content'].strip()
            return corrected
            
        except HTTPError as e:
            error_body = e.response.body.decode('utf-8') if e.response else str(e)
            logger.error(f"LLM API 错误 ({e.code}): {error_body}")
            raise Exception(f"LLM API 错误 ({e.code}): {error_body}")
        except Exception as e:
            logger.error(f"LLM 调用失败: {str(e)}")
            raise Exception(f"LLM 调用失败: {str(e)}")

    def close(self):
        """关闭 HTTP 客户端，释放资源"""
        if self.http_client:
            self.http_client.close()
            self.http_client = None
            
