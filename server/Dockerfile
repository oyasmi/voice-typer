FROM python:3.12-slim

# 设置工作目录
WORKDIR /app

# 安装系统依赖 (ffmpeg 等，语音处理通常需要)
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg libsndfile1 && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# 复制依赖配置
COPY requirements.txt .

# 安装 Python 依赖 
RUN pip install --no-cache-dir -r requirements.txt -i https://pypi.tuna.tsinghua.edu.cn/simple

# 复制项目代码
COPY . .

# 环境变量默认配置
ENV HOST=0.0.0.0
ENV PORT=6008
ENV MODEL=paraformer-zh
ENV PUNC_MODEL=ct-punc
ENV DEVICE=cpu

# 可选环境变量 (留空表示不启用)
# ENV API_KEYS=
# ENV LLM_BASE_URL=
# ENV LLM_API_KEY=
# ENV LLM_MODEL=gpt-4o-mini
# ENV LLM_TEMPERATURE=0.3
# ENV LLM_MAX_TOKENS=600

# 暴露端口
EXPOSE $PORT

# 启动服务，支持环境变量传参并根据按需拼接参数
CMD ["sh", "-c", "python asr_server.py \
    --host \"$HOST\" \
    --port \"$PORT\" \
    --model \"$MODEL\" \
    --punc-model \"$PUNC_MODEL\" \
    --device \"$DEVICE\" \
    ${API_KEYS:+--api-keys \"$API_KEYS\"} \
    ${LLM_BASE_URL:+--llm-base-url \"$LLM_BASE_URL\"} \
    ${LLM_API_KEY:+--llm-api-key \"$LLM_API_KEY\"} \
    ${LLM_MODEL:+--llm-model \"$LLM_MODEL\"} \
    ${LLM_TEMPERATURE:+--llm-temperature \"$LLM_TEMPERATURE\"} \
    ${LLM_MAX_TOKENS:+--llm-max-tokens \"$LLM_MAX_TOKENS\"}"]
