"""
VoiceTyper 服务端包
"""

__version__ = "1.5.0"

#: 产出最终文本的离线模型默认值。SenseVoice-Small 自带标点与 ITN，
#: 无需外挂 ct-punc，详见 recognizer.SenseVoiceRecognizer。
DEFAULT_OFFLINE_MODEL = "sensevoice-small"

#: 流式模式下仅用于 partial 预览的模型（SenseVoice 不支持流式）。
DEFAULT_STREAMING_MODEL = "paraformer-zh-streaming"
