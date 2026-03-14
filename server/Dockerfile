FROM python:3.12-slim

WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg libsndfile1 && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY . .
RUN pip install --no-cache-dir .

ENV HOST=0.0.0.0
ENV PORT=6008
ENV MODEL=paraformer-zh
ENV PUNC_MODEL=ct-punc
ENV DEVICE=cpu
ENV ONNX_THREADS=4

EXPOSE 6008

CMD ["sh", "-c", "python -m voice_typer_server \
    --host \"$HOST\" \
    --port \"$PORT\" \
    --model \"$MODEL\" \
    --punc-model \"$PUNC_MODEL\" \
    --device \"$DEVICE\" \
    --onnx-threads \"$ONNX_THREADS\" \
    ${API_KEYS:+--api-keys \"$API_KEYS\"} \
    ${LLM_BASE_URL:+--llm-base-url \"$LLM_BASE_URL\"} \
    ${LLM_API_KEY:+--llm-api-key \"$LLM_API_KEY\"} \
    ${LLM_MODEL:+--llm-model \"$LLM_MODEL\"} \
    ${LLM_TEMPERATURE:+--llm-temperature \"$LLM_TEMPERATURE\"} \
    ${LLM_MAX_TOKENS:+--llm-max-tokens \"$LLM_MAX_TOKENS\"}"]
