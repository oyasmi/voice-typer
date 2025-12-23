@echo off
REM VoiceTyper Windows 打包脚本
echo ========================================
echo VoiceTyper Windows 打包脚本
echo ========================================
echo.

REM 检查 Python 是否安装
python --version >nul 2>&1
if errorlevel 1 (
    echo 错误: 未找到 Python，请先安装 Python 3.8+
    pause
    exit /b 1
)

REM 创建虚拟环境（如果不存在）
if not exist "venv" (
    echo 创建虚拟环境...
    python -m venv venv
)

REM 激活虚拟环境
echo 激活虚拟环境...
call venv\Scripts\activate.bat

REM 升级 pip
echo 升级 pip...
python -m pip install --upgrade pip

REM 安装依赖
echo 安装依赖包...
pip install -r requirements.txt

REM 清理之前的构建
echo 清理旧构建...
if exist "build" rmdir /s /q build
if exist "dist" rmdir /s /q dist
if exist "main.spec" del main.spec

REM 创建图标（如果不存在）
if not exist "icon.ico" (
    echo 创建默认图标...
    python -c "from PIL import Image, ImageDraw; img = Image.new('RGB', (256, 256), 'white'); draw = ImageDraw.Draw(img); draw.ellipse([32, 32, 224, 224], fill='#4CAF50'); draw.ellipse([96, 80, 160, 144], fill='white'); draw.rectangle([96, 112, 160, 160], fill='white'); draw.rectangle([120, 160, 132, 192], fill='white'); draw.rectangle([96, 192, 160, 208], fill='white'); img.save('icon.ico')"
)

REM 打包
echo 开始打包...
pyinstaller --clean ^
    --onefile ^
    --noconsole ^
    --name VoiceTyper ^
    --icon icon.ico ^
    --add-data "config.py;." ^
    --hidden-import tornado ^
    --hidden-import keyboard ^
    --hidden-import pynput ^
    --hidden-import win32clipboard ^
    --hidden-import win32con ^
    --hidden-import pystray ^
    --hidden-import PIL ^
    main.py

if errorlevel 1 (
    echo.
    echo 打包失败！
    pause
    exit /b 1
)

echo.
echo ========================================
echo 打包完成！
echo 可执行文件位置: dist\VoiceTyper.exe
echo ========================================
echo.
echo 使用说明:
echo 1. 运行 dist\VoiceTyper.exe
echo 2. 程序会在系统托盘显示图标
echo 3. 配置文件自动创建在: %%APPDATA%%\VoiceTyper\
echo 4. 默认热键: Ctrl+Space（可在配置文件中修改）
echo.

REM 询问是否运行
set /p run="是否立即运行程序？(Y/N): "
if /i "%run%"=="Y" (
    start "" "dist\VoiceTyper.exe"
)

pause