# -*- mode: python ; coding: utf-8 -*-
"""
VoiceTyper Windows 客户端 PyInstaller 配置
"""
import sys
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

# 收集sounddevice的PortAudio依赖
datas = [
    ('assets', 'assets'),
]
datas += collect_data_files('sounddevice')

block_cipher = None

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=datas,
    hiddenimports=[
        'pynput.keyboard._win32',
        'pynput.mouse._win32',
        'pystray',
        'pyperclip',
        'PIL._imaging',
        'PIL._tkinter_finder',
        'sounddevice',
        'numpy',
        'tornado',
        'yaml',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        'matplotlib',
        'scipy',
        'PIL.ImageTk',
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
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='VoiceTyper',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,  # 无控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='assets/icon.ico',
)
