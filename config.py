"""
配置加载与管理模块
"""
import os
import yaml
from dataclasses import dataclass, field
from typing import List, Optional
from pathlib import Path


@dataclass
class ModelConfig:
    name: str = "paraformer-zh"
    streaming_name: str = "paraformer-zh-streaming"
    punc_model: Optional[str] = "ct-punc"
    device: str = "mps"


@dataclass
class HotkeyConfig:
    modifiers: List[str] = field(default_factory=lambda: ["ctrl"])
    key: str = "space"


@dataclass
class StreamingConfig:
    threshold_seconds: float = 7.0
    chunk_size: List[int] = field(default_factory=lambda: [0, 10, 5])


@dataclass
class UIConfig:
    opacity: float = 0.85
    width: int = 240
    height: int = 70


@dataclass
class AppConfig:
    model: ModelConfig = field(default_factory=ModelConfig)
    hotkey: HotkeyConfig = field(default_factory=HotkeyConfig)
    streaming: StreamingConfig = field(default_factory=StreamingConfig)
    hotwords: List[str] = field(default_factory=list)
    ui: UIConfig = field(default_factory=UIConfig)


def get_config_path() -> Path:
    """获取配置文件路径"""
    # 优先使用用户目录下的配置
    user_config = Path.home() / ".config" / "voice_input" / "config.yaml"
    if user_config.exists():
        return user_config
    
    # 其次使用当前目录
    local_config = Path(__file__).parent / "config.yaml"
    if local_config.exists():
        return local_config
    
    # 返回用户配置路径（将创建默认配置）
    return user_config


def load_config() -> AppConfig:
    """加载配置文件"""
    config_path = get_config_path()
    
    if not config_path.exists():
        # 创建默认配置
        config_path.parent.mkdir(parents=True, exist_ok=True)
        save_default_config(config_path)
        print(f"已创建默认配置文件: {config_path}")
    
    with open(config_path, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f) or {}
    
    return parse_config(data)


def parse_config(data: dict) -> AppConfig:
    """解析配置字典"""
    config = AppConfig()
    
    if 'model' in data:
        m = data['model']
        config.model = ModelConfig(
            name=m.get('name', config.model.name),
            streaming_name=m.get('streaming_name', config.model.streaming_name),
            punc_model=m.get('punc_model'),
            device=m.get('device', config.model.device),
        )
    
    if 'hotkey' in data:
        h = data['hotkey']
        config.hotkey = HotkeyConfig(
            modifiers=h.get('modifiers', config.hotkey.modifiers),
            key=h.get('key', config.hotkey.key),
        )
    
    if 'streaming' in data:
        s = data['streaming']
        config.streaming = StreamingConfig(
            threshold_seconds=s.get('threshold_seconds', config.streaming.threshold_seconds),
            chunk_size=s.get('chunk_size', config.streaming.chunk_size),
        )
    
    if 'hotwords' in data:
        config.hotwords = data['hotwords'] or []
    
    if 'ui' in data:
        u = data['ui']
        config.ui = UIConfig(
            opacity=u.get('opacity', config.ui.opacity),
            width=u.get('width', config.ui.width),
            height=u.get('height', config.ui.height),
        )
    
    return config


def save_default_config(path: Path):
    """保存默认配置"""
    default_yaml = """# VoiceTyper 配置文件

# 模型配置
model:
  # 可选: paraformer-zh, paraformer-en, SenseVoiceSmall
  name: "paraformer-zh"
  # 流式模型 (当录音超过阈值时使用)
  streaming_name: "paraformer-zh-streaming"
  # 标点恢复模型 (可选，设为 null 禁用)
  punc_model: "ct-punc"
  # 设备: mps (Apple Silicon), cpu
  device: "mps"

# 热键配置
# 支持的修饰键: ctrl, alt, shift, cmd
# 支持的按键: space, tab, a-z, 0-9, f1-f12 等
hotkey:
  modifiers:
    - "ctrl"
  key: "space"

# 流式识别配置
streaming:
  # 切换到流式识别的时间阈值 (秒)
  threshold_seconds: 7
  # chunk 大小配置 [0, 10, 5] = 600ms 延迟
  chunk_size: [0, 10, 5]

# 用户自定义词库 (热词)
hotwords:
  - "FunASR"
  - "Python"
  - "macOS"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
"""
    with open(path, 'w', encoding='utf-8') as f:
        f.write(default_yaml)


def get_hotwords_string(hotwords: List[str]) -> str:
    """将热词列表转换为 FunASR 所需的字符串格式"""
    return ' '.join(hotwords) if hotwords else ''


if __name__ == "__main__":
    # 测试配置加载
    config = load_config()
    print(f"模型: {config.model.name}")
    print(f"热键: {config.hotkey.modifiers} + {config.hotkey.key}")
    print(f"流式阈值: {config.streaming.threshold_seconds}s")
    print(f"热词: {config.hotwords}")