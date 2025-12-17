"""
配置加载与管理模块
"""
import os
import yaml
from dataclasses import dataclass, field
from typing import List, Optional
from pathlib import Path

# 应用信息
APP_NAME = "VoiceTyper"
APP_VERSION = "1.0.0"
CONFIG_DIR_NAME = "voice_typer"


@dataclass
class ModelConfig:
    name: str = "paraformer-zh"
    punc_model: Optional[str] = "ct-punc"
    device: str = "mps"


@dataclass
class HotkeyConfig:
    modifiers: List[str] = field(default_factory=lambda: ["ctrl"])
    key: str = "tab"


@dataclass
class UIConfig:
    opacity: float = 0.85
    width: int = 240
    height: int = 70


@dataclass
class AppConfig:
    model: ModelConfig = field(default_factory=ModelConfig)
    hotkey: HotkeyConfig = field(default_factory=HotkeyConfig)
    hotword_files: List[str] = field(default_factory=list)
    hotwords: List[str] = field(default_factory=list)
    ui: UIConfig = field(default_factory=UIConfig)


def get_config_dir() -> Path:
    """获取配置目录 ~/.config/voice_typer"""
    config_dir = Path.home() / ".config" / CONFIG_DIR_NAME
    return config_dir


def get_config_path() -> Path:
    """获取配置文件路径"""
    return get_config_dir() / "config.yaml"


def get_default_hotwords_path() -> Path:
    """获取默认热词文件路径"""
    return get_config_dir() / "hotwords.txt"


def ensure_config_dir():
    """确保配置目录存在"""
    config_dir = get_config_dir()
    config_dir.mkdir(parents=True, exist_ok=True)
    return config_dir


def resolve_path(file_path: str, base_dir: Path) -> Path:
    """解析文件路径，支持绝对路径、相对路径、~ 展开"""
    expanded = os.path.expanduser(file_path)
    path = Path(expanded)
    
    if path.is_absolute():
        return path
    
    return base_dir / path


def load_hotwords_from_file(file_path: Path) -> List[str]:
    """从文件加载热词列表"""
    words = []
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            for line in f:
                word = line.strip()
                if word and not word.startswith('#'):
                    words.append(word)
        print(f"  已加载词库: {file_path} ({len(words)} 词)")
    except FileNotFoundError:
        print(f"  警告: 词库文件不存在: {file_path}")
    except PermissionError:
        print(f"  警告: 词库文件无权访问: {file_path}")
    except Exception as e:
        print(f"  警告: 加载词库文件失败 {file_path}: {e}")
    
    return words


def load_all_hotwords(file_paths: List[str], base_dir: Path) -> List[str]:
    """加载所有热词文件"""
    all_words = []
    seen = set()
    
    for file_path in file_paths:
        resolved = resolve_path(file_path, base_dir)
        words = load_hotwords_from_file(resolved)
        for word in words:
            if word not in seen:
                seen.add(word)
                all_words.append(word)
    
    return all_words


def ensure_default_files():
    """确保默认配置文件和热词文件存在"""
    config_dir = ensure_config_dir()
    config_path = get_config_path()
    hotwords_path = get_default_hotwords_path()
    
    # 创建默认配置文件
    if not config_path.exists():
        save_default_config(config_path)
        print(f"已创建默认配置文件: {config_path}")
    
    # 创建默认热词文件
    if not hotwords_path.exists():
        create_default_hotwords_file(hotwords_path)
        print(f"已创建默认词库文件: {hotwords_path}")


def load_config() -> AppConfig:
    """加载配置文件"""
    # 确保配置目录和默认文件存在
    ensure_default_files()
    
    config_path = get_config_path()
    
    with open(config_path, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f) or {}
    
    config = parse_config(data)
    
    # 加载热词文件
    if config.hotword_files:
        print("加载用户词库...")
        config_dir = get_config_dir()
        config.hotwords = load_all_hotwords(config.hotword_files, config_dir)
        if config.hotwords:
            print(f"  词库加载完成，共 {len(config.hotwords)} 个词")
        else:
            print(f"  词库为空")
    
    return config


def parse_config(data: dict) -> AppConfig:
    """解析配置字典"""
    config = AppConfig()
    
    if 'model' in data:
        m = data['model']
        config.model = ModelConfig(
            name=m.get('name', config.model.name),
            punc_model=m.get('punc_model'),
            device=m.get('device', config.model.device),
        )
    
    if 'hotkey' in data:
        h = data['hotkey']
        config.hotkey = HotkeyConfig(
            modifiers=h.get('modifiers', config.hotkey.modifiers),
            key=h.get('key', config.hotkey.key),
        )
    
    if 'hotword_files' in data:
        config.hotword_files = data['hotword_files'] or []
    
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
  # 模型：paraformer-zh (中文), SenseVoiceSmall (多语言)
  name: "paraformer-zh"
  # 标点恢复模型，设为 null 可加快启动
  punc_model: "ct-punc"
  # 设备: mps (Apple Silicon), cpu
  device: "mps"

# 热键配置
# 支持的修饰键: ctrl, alt/option, shift, cmd/command
# 支持的按键: space, tab, a-z, 0-9, f1-f12 等
hotkey:
  modifiers:
    - "cmd"
  key: "space"

# 用户自定义词库文件
# 支持多个文件，每个文件每行一个词
# 支持绝对路径或相对路径（相对于 ~/.config/voice_typer/）
hotword_files:
  - "hotwords.txt"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
"""
    with open(path, 'w', encoding='utf-8') as f:
        f.write(default_yaml)


def create_default_hotwords_file(path: Path):
    """创建默认热词文件"""
    default_content = """# VoiceTyper 用户自定义词库
# 每行一个词，支持中英文
# 以 # 开头的行为注释

# 技术术语示例
FunASR
Python
GitHub
OpenAI
ChatGPT

# 在下方添加你的自定义词汇...
"""
    with open(path, 'w', encoding='utf-8') as f:
        f.write(default_content)


def get_hotwords_string(hotwords: List[str]) -> str:
    """将热词列表转换为空格分隔的字符串"""
    return ' '.join(hotwords) if hotwords else ''