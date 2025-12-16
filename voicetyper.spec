# -*- mode: python ; coding: utf-8 -*-
"""
VoiceTyper PyInstaller 打包配置
"""
import sys
import os
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

block_cipher = None

# 收集依赖数据
datas = []
datas += collect_data_files('funasr')
datas += collect_data_files('modelscope')
datas += collect_data_files('torch')
datas += collect_data_files('torchaudio')
datas += collect_data_files('jieba')

# 添加配置文件
datas += [('config.yaml', '.')]

# 收集隐式导入
hiddenimports = []
hiddenimports += collect_submodules('funasr')
hiddenimports += collect_submodules('modelscope')
hiddenimports += collect_submodules('torch')
hiddenimports += collect_submodules('torchaudio')
hiddenimports += collect_submodules('jieba')
hiddenimports += [
    'pynput.keyboard._darwin',
    'pynput.mouse._darwin',
    'rumps',
    'sounddevice',
    'yaml',
    'numpy',
    'objc',
    'AppKit',
    'Foundation',
]

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        'tkinter',
        'matplotlib',
        'scipy',
        'PIL',
        'cv2',
        'IPython',
        'jupyter',
        'notebook',
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='VoiceTyper',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,  # 无控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name='VoiceTyper',
)

app = BUNDLE(
    coll,
    name='VoiceTyper.app',
    icon='assets/icon.icns' if os.path.exists('assets/icon.icns') else None,
    bundle_identifier='com.voicetyper.app',
    info_plist={
        'CFBundleName': 'VoiceTyper',
        'CFBundleDisplayName': 'VoiceTyper',
        'CFBundleVersion': '1.0.0',
        'CFBundleShortVersionString': '1.0.0',
        'LSMinimumSystemVersion': '14.0',
        'LSUIElement': True,  # 菜单栏应用，无 Dock 图标
        'NSHighResolutionCapable': True,
        'NSMicrophoneUsageDescription': 'VoiceTyper 需要使用麦克风进行语音识别',
        'NSAppleEventsUsageDescription': 'VoiceTyper 需要控制键盘以输入识别的文字',
    },
)