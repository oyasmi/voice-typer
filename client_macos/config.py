"""
配置管理模块
"""
import os
import platform
import yaml
import logging
from dataclasses import dataclass, field
from typing import List, Optional
from pathlib import Path

APP_NAME = "VoiceTyper"
APP_VERSION = "1.4.1"
CONFIG_DIR_NAME = "voice_typer"

logger = logging.getLogger(APP_NAME)


@dataclass
class ServerConfig:
    host: str = "127.0.0.1"
    port: int = 6008
    timeout: float = 60.0
    api_key: Optional[str] = None
    llm_recorrect: bool = True  # 是否启用 LLM 修正
    
    def __post_init__(self):
        """验证配置值的有效性"""
        if not (1 <= self.port <= 65535):
            raise ValueError(f"端口必须在 1-65535 之间，当前值: {self.port}")
        if self.timeout <= 0:
            raise ValueError(f"超时时间必须为正数，当前值: {self.timeout}")
        if self.host not in ('localhost', '127.0.0.1'):
            # 简单的主机格式检查
            if not self.host or '..' in self.host or self.host.startswith('.') or self.host.endswith('.'):
                raise ValueError(f"主机地址格式无效: {self.host}")


@dataclass
class HotkeyConfig:
    modifiers: List[str] = field(default_factory=lambda: ["ctrl"])
    key: str = "f2"


@dataclass
class UIConfig:
    opacity: float = 0.85
    width: int = 240
    height: int = 70
    
    def __post_init__(self):
        """验证 UI 配置值的有效性"""
        if not (0.0 <= self.opacity <= 1.0):
            raise ValueError(f"透明度必须在 0.0-1.0 之间，当前值: {self.opacity}")
        if self.width <= 0:
            raise ValueError(f"宽度必须为正数，当前值: {self.width}")
        if self.height <= 0:
            raise ValueError(f"高度必须为正数，当前值: {self.height}")


@dataclass
class AppConfig:
    server: ServerConfig = field(default_factory=ServerConfig)
    hotkey: HotkeyConfig = field(default_factory=HotkeyConfig)
    hotword_files: List[str] = field(default_factory=list)
    hotwords: List[str] = field(default_factory=list)
    ui: UIConfig = field(default_factory=UIConfig)


def get_config_dir() -> Path:
    """获取配置目录"""
    if platform.system() == 'Windows':
        # Windows: %APPDATA%\voice_typer
        appdata = os.environ.get('APPDATA')
        if not appdata:
            # 后备到用户目录
            userprofile = os.environ.get('USERPROFILE', os.path.expanduser('~'))
            appdata = os.path.join(userprofile, 'AppData', 'Roaming')
        return Path(appdata) / CONFIG_DIR_NAME
    else:
        # macOS/Linux: ~/.config/voice_typer
        return Path.home() / ".config" / CONFIG_DIR_NAME


def get_config_path() -> Path:
    return get_config_dir() / "config.yaml"


def get_default_hotwords_path() -> Path:
    return get_config_dir() / "hotwords.txt"


def ensure_config_dir() -> Path:
    config_dir = get_config_dir()
    config_dir.mkdir(parents=True, exist_ok=True)
    return config_dir


def ensure_default_files():
    """确保默认配置文件存在"""
    config_dir = ensure_config_dir()
    config_path = get_config_path()
    hotwords_path = get_default_hotwords_path()

    if not config_path.exists():
        save_default_config(config_path)
        logger.info(f"已创建配置文件: {config_path}")

    if not hotwords_path.exists():
        create_default_hotwords_file(hotwords_path)
        logger.info(f"已创建词库文件: {hotwords_path}")


def load_hotwords_from_file(file_path: Path) -> List[str]:
    """从文件加载热词"""
    words = []
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            for line in f:
                word = line.strip()
                if word and not word.startswith('#'):
                    words.append(word)
    except FileNotFoundError:
        pass
    except Exception as e:
        logger.warning(f"警告: 加载词库失败 {file_path}: {e}")
    return words


def load_all_hotwords(file_paths: List[str], base_dir: Path) -> List[str]:
    """加载所有热词文件"""
    all_words = []
    seen = set()

    for file_path in file_paths:
        path = Path(os.path.expanduser(file_path))
        if not path.is_absolute():
            path = base_dir / path

        for word in load_hotwords_from_file(path):
            if word not in seen:
                seen.add(word)
                all_words.append(word)

    return all_words


def load_config() -> AppConfig:
    """加载配置"""
    ensure_default_files()

    config_path = get_config_path()
    with open(config_path, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f) or {}

    config = AppConfig()

    if 'server' in data:
        s = data['server']
        config.server = ServerConfig(
            host=s.get('host', config.server.host),
            port=s.get('port', config.server.port),
            timeout=s.get('timeout', config.server.timeout),
            api_key=s.get('api_key', config.server.api_key),
            llm_recorrect=s.get('llm_recorrect', config.server.llm_recorrect),
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

    # 加载热词
    if config.hotword_files:
        config.hotwords = load_all_hotwords(config.hotword_files, get_config_dir())

    return config


def save_default_config(path: Path):
    """保存默认配置"""
    content = """# VoiceTyper 客户端配置

# 语音识别服务地址
server:
  host: "127.0.0.1"
  port: 6008
  timeout: 60.0
  api_key: ""  # 设置API密钥用于连接远程服务器，本地连接可留空
  llm_recorrect: true  # 是否启用 LLM 修正识别错误（需要服务端支持）

# 热键配置
# 支持的修饰键: ctrl, alt, shift, cmd (macOS), win_l/win_r (Windows左/右Win键)
# 支持的按键: 字母 a-z, 数字 0-9, f1-f12, space, tab, fn (macOS Fn/地球仪键)
# 当 key 设为 "fn" 时, modifiers 应为空列表
hotkey:
  modifiers:
    - "ctrl"
  key: "f2"

# 用户词库文件（相对于配置目录）
hotword_files:
  - "hotwords.txt"

# UI 配置
ui:
  opacity: 0.85
  width: 240
  height: 70
"""
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)


def create_default_hotwords_file(path: Path):
    """创建默认热词文件"""
    content = """# VoiceTyper 用户自定义词库
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
        f.write(content)


def get_hotwords_string(hotwords: List[str]) -> str:
    """热词列表转字符串"""
    return ' '.join(hotwords) if hotwords else ''
