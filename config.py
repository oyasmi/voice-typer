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
    punc_model: Optional[str] = "ct-punc"
    device: str = "mps"


@dataclass
class HotkeyConfig:
    modifiers: List[str] = field(default_factory=lambda: ["ctrl"])
    key: str = "space"


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
    hotwords: List[str] = field(default_factory=list)  # 从文件加载后的实际词库
    ui: UIConfig = field(default_factory=UIConfig)


def get_config_path() -> Path:
    """获取配置文件路径"""
    user_config = Path.home() / ".config" / "voice_input" / "config.yaml"
    if user_config.exists():
        return user_config
    
    local_config = Path(__file__).parent / "config.yaml"
    if local_config.exists():
        return local_config
    
    return user_config


def resolve_path(file_path: str, base_dir: Path) -> Path:
    """解析文件路径，支持绝对路径、相对路径、~ 展开"""
    # 展开 ~
    expanded = os.path.expanduser(file_path)
    path = Path(expanded)
    
    # 如果是绝对路径，直接返回
    if path.is_absolute():
        return path
    
    # 相对路径，相对于配置文件目录
    return base_dir / path


def load_hotwords_from_file(file_path: Path) -> List[str]:
    """从文件加载热词列表"""
    words = []
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            for line in f:
                word = line.strip()
                # 跳过空行和注释行
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
    seen = set()  # 去重
    
    for file_path in file_paths:
        resolved = resolve_path(file_path, base_dir)
        words = load_hotwords_from_file(resolved)
        for word in words:
            if word not in seen:
                seen.add(word)
                all_words.append(word)
    
    return all_words


def load_config() -> AppConfig:
    """加载配置文件"""
    config_path = get_config_path()
    
    if not config_path.exists():
        config_path.parent.mkdir(parents=True, exist_ok=True)
        save_default_config(config_path)
        create_default_hotwords_file(config_path.parent)
        print(f"已创建默认配置文件: {config_path}")
    
    with open(config_path, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f) or {}
    
    config = parse_config(data)
    
    # 加载热词文件
    if config.hotword_files:
        print("加载用户词库...")
        config.hotwords = load_all_hotwords(config.hotword_files, config_path.parent)
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
    - "ctrl"
  key: "space"

# 用户自定义词库文件
# 支持多个文件，每个文件每行一个词
# 支持绝对路径或相对路径（相对于配置文件目录）
# 文件不存在时仅警告，不影响启动
hotword_files:
  - "hotwords.txt"
  # - "/path/to/custom_words.txt"
  # - "~/Documents/my_hotwords.txt"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
"""
    with open(path, 'w', encoding='utf-8') as f:
        f.write(default_yaml)


def create_default_hotwords_file(config_dir: Path):
    """创建默认热词文件"""
    hotwords_file = config_dir / "hotwords.txt"
    if not hotwords_file.exists():
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
        with open(hotwords_file, 'w', encoding='utf-8') as f:
            f.write(default_content)
        print(f"已创建默认词库文件: {hotwords_file}")


def get_hotwords_string(hotwords: List[str]) -> str:
    """将热词列表转换为空格分隔的字符串"""
    return ' '.join(hotwords) if hotwords else ''