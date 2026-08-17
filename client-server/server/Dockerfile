# ---- 阶段一：模型。只依赖 MODEL_REPO，源码改动不会触发重下 ----
FROM python:3.12-slim AS models

ARG MODEL_REPO=iic/SenseVoiceSmall-onnx

RUN pip install --no-cache-dir modelscope

# allow_file_pattern 与 recognizer.SenseVoiceRecognizer._ALLOW_PATTERNS 保持一致：
# 只取推理必需文件，跳过仓库里的示例音频 / demo apk。
RUN python -c "\
from modelscope.hub.snapshot_download import snapshot_download; \
snapshot_download('$MODEL_REPO', allow_file_pattern=[ \
    '*.onnx', 'am.mvn', 'config.yaml', 'tokens.json', 'tokens.txt'])"

# 运行期按 ~/.cache/modelscope/hub/models/<repo> 查缓存
# （recognizer._prepare_model_dir）。若 modelscope 改了缓存布局，
# 这里当场构建失败，而不是等到运行时静默重下一遍模型。
RUN ls "/root/.cache/modelscope/hub/models/$MODEL_REPO"/*.onnx

# ---- 阶段二：运行时 ----
FROM python:3.12-slim

WORKDIR /app

# 服务端只接收裸 float32 PCM，没有任何音频解码路径，
# 不需要 ffmpeg / libsndfile1。
COPY --from=models /root/.cache/modelscope /root/.cache/modelscope
COPY . .
RUN pip install --no-cache-dir .

ENV HOST=0.0.0.0
ENV PORT=6008
ENV DEVICE=cpu
ENV ONNX_THREADS=4
# MODEL / OFFLINE_MODEL / PUNC_MODEL 默认不设置：交给 CLI 按运行模式选择
# （单模型流式下预览与终稿都由 sensevoice-small 产出，与本阶段预烘的
# MODEL_REPO 默认值一致；换用其他离线模型时该镜像仍会在运行期按需下载）。

EXPOSE 6008

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["python", "/app/scripts/healthcheck.py"]

CMD ["sh", "-c", "python -m voice_typer_server \
    --host \"$HOST\" \
    --port \"$PORT\" \
    ${MODEL:+--model \"$MODEL\"} \
    ${OFFLINE_MODEL:+--offline-model \"$OFFLINE_MODEL\"} \
    ${PUNC_MODEL:+--punc-model \"$PUNC_MODEL\"} \
    --device \"$DEVICE\" \
    --onnx-threads \"$ONNX_THREADS\" \
    ${API_KEYS:+--api-keys \"$API_KEYS\"} \
    ${LLM_BASE_URL:+--llm-base-url \"$LLM_BASE_URL\"} \
    ${LLM_API_KEY:+--llm-api-key \"$LLM_API_KEY\"} \
    ${LLM_MODEL:+--llm-model \"$LLM_MODEL\"} \
    ${LLM_TEMPERATURE:+--llm-temperature \"$LLM_TEMPERATURE\"} \
    ${LLM_MAX_TOKENS:+--llm-max-tokens \"$LLM_MAX_TOKENS\"}"]
