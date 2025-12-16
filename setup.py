from setuptools import setup

APP = ['main.py']
DATA_FILES = ['config.yaml']
OPTIONS = {
    'argv_emulation': False,
    'plist': {
        'CFBundleName': 'VoiceTyper',
        'CFBundleDisplayName': 'VoiceTyper',
        'CFBundleIdentifier': 'com.voicetyper.app',
        'CFBundleVersion': '1.0.0',
        'CFBundleShortVersionString': '1.0.0',
        'LSMinimumSystemVersion': '15.0',
        'LSUIElement': True,  # 无 Dock 图标，仅菜单栏
        'NSMicrophoneUsageDescription': 'VoiceTyper 需要使用麦克风进行语音识别',
        'NSAppleEventsUsageDescription': 'VoiceTyper 需要控制其他应用以输入文字',
    },
    'packages': [
        'funasr',
        'torch',
        'torchaudio',
        'sounddevice',
        'pynput',
        'rumps',
        'yaml',
        'numpy',
    ],
}

setup(
    name='VoiceTyper',
    app=APP,
    data_files=DATA_FILES,
    options={'py2app': OPTIONS},
    setup_requires=['py2app'],
)