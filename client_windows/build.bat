@echo off
REM VoiceTyper Windows 构建脚本

echo ========================================
echo VoiceTyper Windows 构建脚本
echo ========================================
echo.

REM 检查 Python
python --version >nul 2>&1
if errorlevel 1 (
    echo 错误: 未找到 Python，请先安装 Python 3.9+
    exit /b 1
)

REM 安装依赖
echo [1/3] 安装依赖...
pip install -r requirements.txt -q
if errorlevel 1 (
    echo 错误: 依赖安装失败
    exit /b 1
)
echo 依赖安装完成
echo.

REM 检查图标文件
if not exist "assets\icon.ico" (
    echo 错误: 未找到 assets\icon.ico
    exit /b 1
)

REM 安装 PyInstaller
echo [2/3] 检查 PyInstaller...
pip show pyinstaller >nul 2>&1
if errorlevel 1 (
    echo 安装 PyInstaller...
    pip install pyinstaller -q
)
echo PyInstaller 就绪
echo.

REM 构建应用
echo [3/3] 构建 Windows 可执行文件...
pyinstaller voicetyper.spec --clean
if errorlevel 1 (
    echo 错误: 构建失败
    exit /b 1
)

echo.
echo ========================================
echo 构建完成!
echo ========================================
echo.
echo 可执行文件位置: dist\VoiceTyper.exe
echo.
echo 使用说明:
echo 1. 确保 ASR 服务器已启动
echo 2. 运行 VoiceTyper.exe
echo 3. 按住 Win+Space 开始语音输入
echo.
