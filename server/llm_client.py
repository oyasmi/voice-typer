#!/usr/bin/env python3
"""
简单的 OpenAI 兼容 LLM 客户端
"""
import json
from tornado.httpclient import AsyncHTTPClient, HTTPRequest, HTTPError


class LLMClient:
    """OpenAI 兼容的 LLM 客户端"""
    
    def __init__(self, base_url, api_key, model, temperature=0.3, max_tokens=500):
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
        
    async def correct_text(self, text):
        """
        使用 LLM 修正识别文本中的显著错误
        
        Args:
            text: 原始识别文本
            
        Returns:
            修正后的文本
        """
        system_prompt = """你是一个文本校对助手。用户会给你一段语音识别的文本结果，其中可能包含一些文字的错误、标点的误用等，请你修正。

请修正文本中**显著的错误**，包括：
1. 明显的同音字错误（特别注意"的地得"和"他她它"的正确使用）
2. 明显的错别字、词语错误（如"机器学习"误识别为"机械学习"）
3. 明显不通顺的语句，明显不正确的重复字词
4. 数字表达：在合适的场景下，将汉字数字、百分比、日期等改为阿拉伯数字表示（如"二十一点五"改为"21.5"，"第三个"改为"第3个"）
5. 去除明显的口语停顿词（如"呃"、"嗯"、"那个"等填充词，但要保留有实际含义的用法）
6. 标点符号：修正混用的英文标点为中文标点（如英文逗号改为中文顿号或逗号）
7. 对于较短的字句（如2-4个字），如果只是一个词或短语而非完整句子，应去掉末尾的句号（但"是的。"、"好的。"、"对的。"等完整回应保留句号）

**注意**：
- 只修正明显的错误，不要过度修改
- 保持原文的语气、风格和表达习惯
- 不要添加原文没有的内容
- 不要改变原文的意思
- 如果不确定是否有错误，保持原样

请直接返回修正后的文本，不要添加任何解释或说明。"""

        messages = [
            {"role": "system", "content": system_prompt},
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
                request_timeout=4.0
            )
            
            response = await self.http_client.fetch(request)
            result = json.loads(response.body.decode('utf-8'))
            corrected = result['choices'][0]['message']['content'].strip()
            return corrected
            
        except HTTPError as e:
            error_body = e.response.body.decode('utf-8') if e.response else str(e)
            raise Exception(f"LLM API 错误 ({e.code}): {error_body}")
        except Exception as e:
            raise Exception(f"LLM 调用失败: {str(e)}")
            