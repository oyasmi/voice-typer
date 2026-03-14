# -*- mode: python ; coding: utf-8 -*-
"""
VoiceTyper PyInstaller 打包配置
"""
import os
import sys

block_cipher = None

# 源文件
sources = [
    'main.py',
    'config.py',
    'controller.py',
    'recorder.py',
    'asr_client.py',
    'text_inserter.py',
    'indicator.py',
]

# 图标路径
icon_path = 'assets/icon.icns' if os.path.exists('assets/icon.icns') else None

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[
        'pynput.keyboard._darwin',
        'pynput.mouse._darwin',
        'rumps',
        'tornado',
        'tornado.httpclient',
        'sounddevice',
        'numpy',
        'yaml',
        'objc',
        'AppKit',
        'Foundation',
    ],
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
    console=False,
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
    icon=icon_path,
    bundle_identifier='com.voicetyper.app',
    info_plist={
        'CFBundleName': 'VoiceTyper',
        'CFBundleDisplayName': 'VoiceTyper',
        'CFBundleVersion': '1.4.1',
        'CFBundleShortVersionString': '1.4.1',
        'LSMinimumSystemVersion': '14.0',
        'LSUIElement': True,
        'NSHighResolutionCapable': True,
        'NSMicrophoneUsageDescription': 'VoiceTyper 需要使用麦克风进行语音识别',
        'NSAppleEventsUsageDescription': 'VoiceTyper 需要控制键盘以输入文字',
    },
)
