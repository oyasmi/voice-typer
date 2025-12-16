"""
VoiceTyper macOS 应用打包脚本
使用方法: python setup.py py2app
"""
from setuptools import setup
import os
import sys

# 应用信息
APP_NAME = "VoiceTyper"
APP_VERSION = "1.0.0"
APP_BUNDLE_ID = "com.voicetyper.app"

# 主程序入口
APP = ['main.py']

# 需要包含的数据文件
DATA_FILES = [
    ('', ['config.yaml']),  # 默认配置文件
]

# py2app 选项
OPTIONS = {
    'argv_emulation': False,
    'iconfile': 'assets/icon.icns',  # 应用图标（如果有）
    'plist': {
        'CFBundleName': APP_NAME,
        'CFBundleDisplayName': APP_NAME,
        'CFBundleIdentifier': APP_BUNDLE_ID,
        'CFBundleVersion': APP_VERSION,
        'CFBundleShortVersionString': APP_VERSION,
        'LSMinimumSystemVersion': '14.0',
        'LSUIElement': True,  # 无 Dock 图标，仅菜单栏
        'LSBackgroundOnly': False,
        'NSHighResolutionCapable': True,
        'NSMicrophoneUsageDescription': 'VoiceTyper 需要使用麦克风进行语音识别',
        'NSAppleEventsUsageDescription': 'VoiceTyper 需要控制键盘以输入识别的文字',
        'NSAccessibilityUsageDescription': 'VoiceTyper 需要辅助功能权限以监听热键和输入文字',
    },
    'packages': [
        'funasr',
        'torch',
        'torchaudio',
        'modelscope',
        'sounddevice',
        'pynput',
        'rumps',
        'yaml',
        'numpy',
        'objc',
        'AppKit',
        'Foundation',
    ],
    'includes': [
        'config',
        'controller',
        'recorder',
        'recognizer',
        'text_inserter',
        'indicator',
    ],
    'excludes': [
        'tkinter',
        'matplotlib',
        'scipy',
        'PIL',
        'cv2',
    ],
    'resources': [],
    'frameworks': [],
}

# 检查图标文件
if not os.path.exists('assets/icon.icns'):
    OPTIONS.pop('iconfile', None)
    print("提示: 未找到 assets/icon.icns，将使用默认图标")

setup(
    name=APP_NAME,
    version=APP_VERSION,
    app=APP,
    data_files=DATA_FILES,
    options={'py2app': OPTIONS},
    setup_requires=['py2app'],
)