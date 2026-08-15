"""
FunASR ONNXRuntime 语音识别封装

提供三种识别器：
- SpeechRecognizer           离线整段识别（paraformer-zh + ct-punc）
- SenseVoiceRecognizer       离线整段识别（SenseVoice-Small，自带标点/ITN）
- StreamingSpeechRecognizer  流式逐块识别（paraformer-zh-streaming）
"""
import importlib
import importlib.machinery
import json
import logging
from pathlib import Path
import re
import sys
import types
from typing import List, Optional, Tuple, Type, Union

import numpy as np

logger = logging.getLogger("VoiceTyper")


# ---------------------------------------------------------------------------
# 公共工具
# ---------------------------------------------------------------------------

def _bypass_load(submodule: str) -> object:
    """绕过 funasr_onnx 包级 import 里的 torch 依赖，直接加载指定子模块。"""
    pkg = "funasr_onnx"
    if pkg not in sys.modules:
        spec = importlib.machinery.PathFinder.find_spec(pkg, sys.path)
        if spec is None or spec.submodule_search_locations is None:
            raise ImportError("未找到 funasr_onnx 包，请先 pip install funasr-onnx")
        package = types.ModuleType(pkg)
        package.__path__ = list(spec.submodule_search_locations)
        package.__spec__ = spec
        sys.modules[pkg] = package
    return importlib.import_module(submodule)


def _resolve_device_id(device: str) -> Union[str, int]:
    d = (device or "cpu").lower()
    if d == "cpu":
        return "-1"
    if d.startswith("cuda:"):
        return int(d.split(":", 1)[1])
    if d == "cuda":
        return 0
    logger.warning(f"ONNX 后端暂不支持 device={device}，已回退到 CPU")
    return "-1"


def _prepare_model_dir(
    model_name: Optional[str],
    aliases: dict,
    allow_patterns: Optional[List[str]] = None,
) -> Tuple[Optional[str], bool]:
    """返回 (model_dir, quantize)；不存在则从 ModelScope 下载。

    allow_patterns 用于跳过仓库里与推理无关的大文件（示例音频、demo apk 等），
    仅在需要下载时生效。
    """
    if not model_name:
        return None, False

    resolved = aliases.get(model_name, model_name)
    model_dir = Path(resolved).expanduser()

    if not model_dir.exists():
        cache_path = Path.home() / ".cache/modelscope/hub/models" / resolved
        if cache_path.exists():
            model_dir = cache_path
        else:
            from modelscope.hub.snapshot_download import snapshot_download
            logger.info(f"下载 ONNX 模型: {resolved}")
            kwargs = {"allow_file_pattern": allow_patterns} if allow_patterns else {}
            model_dir = Path(snapshot_download(resolved, **kwargs))

    if (model_dir / "model.onnx").exists():
        return str(model_dir), False
    if (model_dir / "model_quant.onnx").exists():
        logger.info(f"仅找到量化模型，自动使用: {model_dir}")
        return str(model_dir), True

    raise FileNotFoundError(f"未找到 ONNX 模型文件: {model_dir}")


def _extract_preds_text(asr_result) -> str:
    """从 funasr-onnx ASR 结果中提取文本，兼容 online/offline 两种返回格式。"""
    if not asr_result:
        return ""
    first = asr_result[0]
    preds = first.get("preds", "") if isinstance(first, dict) else first
    if isinstance(preds, str):
        return preds
    if isinstance(preds, (list, tuple)) and preds:
        head = preds[0]
        return head if isinstance(head, str) else str(head)
    return str(preds) if preds else ""


def _extract_punc_text(punc_result) -> str:
    if not punc_result:
        return ""
    if isinstance(punc_result, str):
        return punc_result
    if isinstance(punc_result, (list, tuple)) and punc_result:
        head = punc_result[0]
        if isinstance(head, str):
            return head
        if isinstance(head, (list, tuple)) and head:
            nested = head[0]
            return nested if isinstance(nested, str) else str(nested)
        return str(head)
    return str(punc_result)


# ---------------------------------------------------------------------------
# 离线整段识别器
# ---------------------------------------------------------------------------

class SpeechRecognizer:
    """基于 funasr-onnx paraformer_bin 的离线整段识别器。"""

    MODEL_ALIASES = {
        "paraformer-zh": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
        "ct-punc":       "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
    }

    def __init__(
        self,
        model_name: str = "paraformer-zh",
        punc_model: Optional[str] = "ct-punc",
        device: str = "cpu",
        intra_op_num_threads: int = 4,
    ):
        self.model_name = model_name
        self.punc_model_name = punc_model
        self.device = device
        self.intra_op_num_threads = intra_op_num_threads
        self._model = None
        self._punc_model = None
        self._initialized = False

    def initialize(self):
        paraformer_mod = _bypass_load("funasr_onnx.paraformer_bin")
        punc_mod       = _bypass_load("funasr_onnx.punc_bin")

        device_id = _resolve_device_id(self.device)
        asr_dir, asr_q   = _prepare_model_dir(self.model_name,      self.MODEL_ALIASES)
        punc_dir, punc_q = _prepare_model_dir(self.punc_model_name, self.MODEL_ALIASES)

        logger.info(f"[1/2] 加载 ONNX 离线 ASR 模型: {asr_dir}")
        self._model = paraformer_mod.Paraformer(
            asr_dir,
            batch_size=1,
            device_id=device_id,
            quantize=asr_q,
            intra_op_num_threads=self.intra_op_num_threads,
        )

        if punc_dir:
            logger.info(f"[2/2] 加载 ONNX 标点恢复模型: {punc_dir}")
            try:
                self._punc_model = punc_mod.CT_Transformer(
                    punc_dir,
                    device_id=device_id,
                    quantize=punc_q,
                    intra_op_num_threads=self.intra_op_num_threads,
                )
            except Exception as exc:
                logger.warning(f"标点模型加载失败，将跳过标点: {exc}")
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    def recognize(self, audio: np.ndarray) -> str:
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")
        if len(audio) == 0:
            return ""

        result = self._model(audio)
        if not result:
            return ""

        text = self._extract_asr_text(result)
        if self._punc_model and text:
            try:
                punc_result = self._punc_model(text)
                normalized = _extract_punc_text(punc_result)
                if normalized:
                    text = normalized
            except Exception as exc:
                logger.warning(f"标点恢复失败: {exc}")
        return text

    def _extract_asr_text(self, asr_result) -> str:
        return _extract_preds_text(asr_result)


# ---------------------------------------------------------------------------
# SenseVoice 离线识别器
# ---------------------------------------------------------------------------

# SenseVoice 把语言 / 情感 / 事件 / 文本规整信息以 <|xxx|> 形式混在 CTC 输出里，
# 上屏前需要整体剥掉。
_RICH_TAG_RE = re.compile(r"<\|[^|]*\|>")

# SentencePiece 的词首标记 ▁ 会在中英交界处留下不一致的空格
# （"那个 issue。" 但 "用swift"）。统一去掉与中日韩字符相邻的空格，
# 与 paraformer + ct-punc 的既有输出风格保持一致；纯英文句子不受影响。
_CJK = r"　-〿㐀-䶿一-鿿＀-￯"
_SPACE_AFTER_CJK_RE = re.compile(rf"(?<=[{_CJK}])\s+(?=[0-9A-Za-z])")
_SPACE_BEFORE_CJK_RE = re.compile(rf"(?<=[0-9A-Za-z])\s+(?=[{_CJK}])")

# 判断结果里是否有"实字"。输入接近静音时 SenseVoice 常吐一个孤立的句号，
# 这种纯标点结果没有任何上屏价值，直接丢弃。
_SUBSTANTIVE_RE = re.compile(
    r"[0-9A-Za-z一-鿿぀-ヿ가-힯]"
)

# 语言与文本规整标签的 token id：与模型词表强绑定，不可随意改动。
_SENSEVOICE_LID = {
    "auto": 0, "zh": 3, "en": 4, "yue": 7, "ja": 11, "ko": 12, "nospeech": 13,
}
_SENSEVOICE_TEXTNORM = {"withitn": 14, "woitn": 15}


class SenseVoiceRecognizer:
    """SenseVoice-Small ONNX 离线整段识别器。

    对外接口与 SpeechRecognizer 完全一致（initialize / is_ready / recognize），
    因此可直接充当流式模式的 offline_recognizer，或非流式模式的主识别器。

    与 paraformer 方案的关键差异：
    - **自带标点与 ITN**（textnorm=withitn），不需要额外的 ct-punc 模型；
    - 单模型覆盖 zh/en/yue/ja/ko，language="auto" 时由模型自行判定。

    这里没有直接复用 funasr_onnx.SenseVoiceSmall：该模块在 import 阶段就依赖
    torch 与 librosa，而本服务刻意不引入 torch。它真正用到 torch 的只有
    argmax / unique_consecutive 两步，用 numpy 重写即可，其余（fbank 前端、
    ONNX session）仍复用 funasr_onnx.utils。
    """

    MODEL_ALIASES = {
        # 官方 ONNX 导出，int8 量化，约 241MB
        "sensevoice-small":      "iic/SenseVoiceSmall-onnx",
        # 社区 fp32 导出，约 893MB；布局与官方一致
        "sensevoice-small-fp32": "manyeyes/sensevoice-small-onnx",
    }

    # 仅下载推理必需的文件，跳过仓库里的示例音频 / demo apk
    _ALLOW_PATTERNS = ["*.onnx", "am.mvn", "config.yaml", "tokens.json", "tokens.txt"]

    def __init__(
        self,
        model_name: str = "sensevoice-small",
        device: str = "cpu",
        intra_op_num_threads: int = 4,
        language: str = "auto",
        textnorm: str = "withitn",
    ):
        if language not in _SENSEVOICE_LID:
            raise ValueError(
                f"不支持的 SenseVoice 语言 {language!r}，可选: {sorted(_SENSEVOICE_LID)}"
            )
        if textnorm not in _SENSEVOICE_TEXTNORM:
            raise ValueError(
                f"不支持的 SenseVoice textnorm {textnorm!r}，"
                f"可选: {sorted(_SENSEVOICE_TEXTNORM)}"
            )

        self.model_name = model_name
        self.device = device
        self.intra_op_num_threads = intra_op_num_threads
        self.language = language
        self.textnorm = textnorm
        self._frontend = None
        self._session = None
        self._tokens: List[str] = []
        self._initialized = False

    # -- 生命周期 ----------------------------------------------------------

    def initialize(self):
        utils_mod    = _bypass_load("funasr_onnx.utils.utils")
        frontend_mod = _bypass_load("funasr_onnx.utils.frontend")

        model_dir_str, quantize = _prepare_model_dir(
            self.model_name, self.MODEL_ALIASES, allow_patterns=self._ALLOW_PATTERNS
        )
        model_dir = Path(model_dir_str)
        onnx_file = model_dir / ("model_quant.onnx" if quantize else "model.onnx")

        logger.info(f"[1/1] 加载 ONNX SenseVoice 模型: {onnx_file}")

        config = utils_mod.read_yaml(str(model_dir / "config.yaml"))
        frontend_conf = dict(config["frontend_conf"])
        frontend_conf["cmvn_file"] = str(model_dir / "am.mvn")
        # 推理阶段关闭 dither：默认值 1.0 会给每帧加随机噪声，
        # 导致同一段音频每次识别结果可能不同，不利于复现与对比。
        frontend_conf["dither"] = 0.0
        self._frontend = frontend_mod.WavFrontend(**frontend_conf)

        self._tokens = self._load_tokens(model_dir)
        self._session = utils_mod.OrtInferSession(
            str(onnx_file),
            _resolve_device_id(self.device),
            intra_op_num_threads=self.intra_op_num_threads,
        )

        logger.info(
            f"SenseVoice 就绪：vocab={len(self._tokens)} "
            f"language={self.language} textnorm={self.textnorm}（自带标点，无需 ct-punc）"
        )
        self._initialized = True

    @staticmethod
    def _load_tokens(model_dir: Path) -> List[str]:
        """读取词表。官方仓库给 tokens.json（JSON 数组），
        社区导出给 tokens.txt（每行 `piece\\t score`）。"""
        json_path = model_dir / "tokens.json"
        if json_path.exists():
            return json.loads(json_path.read_text(encoding="utf-8"))

        txt_path = model_dir / "tokens.txt"
        if txt_path.exists():
            tokens = []
            for line in txt_path.read_text(encoding="utf-8").splitlines():
                if not line:
                    continue
                # piece 本身可能含空格（如 "▁ "），只从右侧切掉分数列
                tokens.append(line.rsplit("\t", 1)[0])
            return tokens

        raise FileNotFoundError(f"未找到 SenseVoice 词表(tokens.json/tokens.txt): {model_dir}")

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._session is not None

    # -- 推理 --------------------------------------------------------------

    def recognize(self, audio: np.ndarray) -> str:
        if not self.is_ready:
            raise RuntimeError("SenseVoice 模型未初始化")
        if len(audio) == 0:
            return ""

        feats, feats_len = self._extract_feat(audio)
        if feats_len < 1:
            return ""

        ctc_logits, encoder_out_lens = self._session([
            feats,
            np.array([feats_len], dtype=np.int32),
            np.array([_SENSEVOICE_LID[self.language]], dtype=np.int32),
            np.array([_SENSEVOICE_TEXTNORM[self.textnorm]], dtype=np.int32),
        ])

        valid = int(encoder_out_lens[0])
        return self._ctc_greedy_decode(ctc_logits[0, :valid, :])

    def _extract_feat(self, audio: np.ndarray) -> Tuple[np.ndarray, int]:
        feat, _ = self._frontend.fbank(audio.astype(np.float32))
        feat, feat_len = self._frontend.lfr_cmvn(feat)
        return feat[None, :, :].astype(np.float32), int(feat_len)

    def _ctc_greedy_decode(self, logits: np.ndarray) -> str:
        """CTC 贪心解码：逐帧 argmax → 合并连续重复 → 去 blank → 拼 token。"""
        ids = logits.argmax(axis=-1)
        if len(ids) == 0:
            return ""
        # 等价于 torch.unique_consecutive：只保留与前一帧不同的位置
        ids = ids[np.concatenate(([True], ids[1:] != ids[:-1]))]
        ids = ids[ids != 0]  # blank_id = 0

        raw = "".join(self._tokens[i] for i in ids)
        return self._postprocess(raw)

    @staticmethod
    def _postprocess(raw: str) -> str:
        """SentencePiece 还原 + 剥离 <|...|> 标签 + 收敛空白 + 中英间距归一。"""
        text = raw.replace("▁", " ")          # ▁ 是 SentencePiece 的词首空格
        text = _RICH_TAG_RE.sub("", text)
        text = re.sub(r"\s+", " ", text).strip()
        text = _SPACE_AFTER_CJK_RE.sub("", text)
        text = _SPACE_BEFORE_CJK_RE.sub("", text)
        return text if _SUBSTANTIVE_RE.search(text) else ""

    # -- 会话（流式预览） --------------------------------------------------

    def new_session(self) -> "SenseVoiceSession":
        if not self.is_ready:
            raise RuntimeError("SenseVoice 模型未初始化")
        return SenseVoiceSession(self)


class SenseVoiceSession:
    """单次录音会话，对应一个 WebSocket 连接。每次新建，用完即弃。

    SenseVoice 本身是非流式模型，这里靠"对已累积音频整段重跑"来产出预览：
    RTF 约 0.01，重跑 10s 音频只要百毫秒量级，比维护一套流式 cache 简单得多，
    而且预览与最终文本出自同一个模型，措辞不会在松手瞬间跳变。

    代价是预览会**修正**先前的文字（这正是它更准的原因），所以 partial 只能是
    全量文本而非增量——见仓库根目录 PROTOCOL.md。
    """

    def __init__(self, owner: SenseVoiceRecognizer):
        self._owner = owner
        self._chunks: List[np.ndarray] = []
        self._cached: Optional[Tuple[int, str]] = None  # (已识别的样本数, 文本)

    def append(self, audio_chunk: np.ndarray) -> None:
        """累积音频。必须足够便宜——它跑在 WS 读取的主循环上。"""
        if len(audio_chunk):
            self._chunks.append(audio_chunk)

    def preview(self) -> str:
        """对目前累积到的全部音频重跑一次，返回**全量**预览文本。"""
        return self._recognize()

    def finalize(self) -> str:
        """松手后调用。与 preview 是同一次计算，只是可能多了尾音。

        客户端若在 finalize 前没有再补音频（尾块为空），这里会直接复用上一次
        预览的结果，省掉一次整段重跑——直接砍掉上屏前的等待。
        """
        return self._recognize()

    def _recognize(self) -> str:
        audio = self._audio()
        # 会话内音频只增不减，样本数足以唯一标识缓冲区状态
        if self._cached is not None and self._cached[0] == len(audio):
            return self._cached[1]

        text = self._owner.recognize(audio)
        self._cached = (len(audio), text)
        return text

    def _audio(self) -> np.ndarray:
        if not self._chunks:
            return np.zeros(0, dtype=np.float32)
        # 累积块数不多（每 600ms 一块），每次重新拼接的开销远小于一次推理
        return np.concatenate(self._chunks)


# ---------------------------------------------------------------------------
# 流式逐块识别器
# ---------------------------------------------------------------------------

class StreamingSpeechRecognizer:
    """基于 funasr-onnx paraformer_online_bin 的流式识别器。"""

    MODEL_ALIASES = {
        "paraformer-zh-streaming": "damo/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-onnx",
        "ct-punc":                 "damo/punc_ct-transformer_cn-en-common-vocab471067-large-onnx",
    }

    def __init__(
        self,
        model_name: str = "paraformer-zh-streaming",
        punc_model: Optional[str] = "ct-punc",
        device: str = "cpu",
        chunk_size: List[int] = None,
        intra_op_num_threads: int = 4,
        offline_recognizer: Optional["SpeechRecognizer"] = None,
    ):
        self.model_name = model_name
        self.punc_model_name = punc_model
        self.device = device
        self.chunk_size = chunk_size or [0, 10, 5]
        self.intra_op_num_threads = intra_op_num_threads
        # 离线整段识别器：流式只负责预览，松手后由它对完整音频复识别得出准确结果
        self._offline_recognizer = offline_recognizer
        self._model = None
        self._punc_model = None
        self._initialized = False

    def initialize(self):
        online_mod = _bypass_load("funasr_onnx.paraformer_online_bin")
        punc_mod   = _bypass_load("funasr_onnx.punc_bin")

        device_id = _resolve_device_id(self.device)
        asr_dir, asr_q   = _prepare_model_dir(self.model_name,      self.MODEL_ALIASES)
        punc_dir, punc_q = _prepare_model_dir(self.punc_model_name, self.MODEL_ALIASES)

        logger.info(f"[1/2] 加载 ONNX 流式 ASR 模型: {asr_dir}")
        self._model = online_mod.Paraformer(
            model_dir=asr_dir,
            batch_size=1,
            chunk_size=self.chunk_size,
            device_id=device_id,
            quantize=asr_q,
            intra_op_num_threads=self.intra_op_num_threads,
        )

        if punc_dir:
            logger.info(f"[2/2] 加载 ONNX 标点恢复模型: {punc_dir}")
            try:
                self._punc_model = punc_mod.CT_Transformer(
                    punc_dir,
                    device_id=device_id,
                    quantize=punc_q,
                    intra_op_num_threads=self.intra_op_num_threads,
                )
            except Exception as exc:
                logger.warning(f"标点模型加载失败，将跳过标点: {exc}")
        else:
            logger.info("[2/2] ONNX 标点恢复模型: 已禁用")

        self._initialized = True

    @property
    def is_ready(self) -> bool:
        return self._initialized and self._model is not None

    def new_session(self) -> "Session":
        if not self.is_ready:
            raise RuntimeError("ONNX 模型未初始化")
        return Session(self)

    def _extract_fragment(self, asr_result) -> str:
        return _extract_preds_text(asr_result)

    def _apply_punc(self, text: str) -> str:
        if not self._punc_model or not text.strip():
            return text
        try:
            return _extract_punc_text(self._punc_model(text)) or text
        except Exception as exc:
            logger.warning(f"标点恢复失败: {exc}")
            return text


class Session:
    """单次录音会话，对应一个 WebSocket 连接。每次新建，用完即弃。

    preview() 用流式模型产出预览（跟嘴）；同时累积原始音频，
    finalize() 时改用离线整段模型对完整音频复识别，得到准确的最终文本。

    与 SenseVoiceSession 保持同样的 append/preview/finalize 接口，
    使 WebSocket 处理逻辑无需区分底层模型。
    """

    def __init__(self, owner: StreamingSpeechRecognizer):
        self._owner = owner
        self._cache: dict = {}
        self._fragments: List[str] = []
        self._audio_buffer: List[np.ndarray] = []  # 累积原始音频，供离线复识别
        self._pending: List[np.ndarray] = []       # 尚未喂给流式模型的块

    def append(self, audio_chunk: np.ndarray) -> None:
        """累积音频。必须足够便宜——它跑在 WS 读取的主循环上。"""
        if len(audio_chunk):
            self._audio_buffer.append(audio_chunk)
            self._pending.append(audio_chunk)

    def preview(self) -> str:
        """把积压的块依次喂给流式模型，返回**全量**预览文本。

        协议约定（详见仓库根目录 PROTOCOL.md）：partial 是全量文本而非增量，
        客户端直接替换即可。流式模型本身只吐增量，这里拼成全量再返回。

        每块都必须喂进去以维持 cache 连续性，因此调用方跳过若干次 preview
        时，下一次会一并补上积压的块。异常会原样抛出交由调用方上报；
        音频仍在 _audio_buffer 中，finalize 可用离线模型兜底。
        """
        pending, self._pending = self._pending, []
        for chunk in pending:
            result = self._owner._model(
                chunk,
                param_dict={"is_final": False, "cache": self._cache},
            )
            raw = self._owner._extract_fragment(result)
            if not raw:
                continue

            # 防御性差分：若模型返回带历史前缀的累计串（不同版本行为不一致），
            # 仅取出真正新增的尾段。多数版本下 raw 已经只是 delta。
            already = "".join(self._fragments)
            delta = raw[len(already):] if raw.startswith(already) else raw
            if delta:
                self._fragments.append(delta)

        return "".join(self._fragments)

    def finalize(self) -> str:
        """松手后调用：用离线模型对完整音频复识别，得到准确的最终文本。"""
        full_audio = (
            np.concatenate(self._audio_buffer)
            if self._audio_buffer
            else np.zeros(0, dtype=np.float32)
        )

        offline = self._owner._offline_recognizer
        if offline is not None and offline.is_ready and len(full_audio) > 0:
            try:
                return offline.recognize(full_audio)
            except Exception as exc:
                logger.warning(f"离线复识别失败，回退到流式拼接结果: {exc}")

        return self._finalize_streaming()

    def _finalize_streaming(self) -> str:
        """回退路径：flush 流式 cache → 拼接碎片 → ct-punc。"""
        try:
            self._owner._model(
                np.zeros(0, dtype=np.float32),
                param_dict={"is_final": True, "cache": self._cache},
            )
        except Exception as exc:
            logger.debug(f"流式 finalize flush 跳过: {exc}")
        return self._owner._apply_punc("".join(self._fragments))
