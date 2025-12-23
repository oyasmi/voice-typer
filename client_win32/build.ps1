# VoiceTyper Windows 打包脚本 (PowerShell)
# 用法: .\build.ps1

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "VoiceTyper Windows 打包脚本" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 检查 Python
try {
    $pythonVersion = python --version 2>&1
    Write-Host "✓ 发现 Python: $pythonVersion" -ForegroundColor Green
} catch {
    Write-Host "✗ 错误: 未找到 Python，请先安装 Python 3.8+" -ForegroundColor Red
    exit 1
}

# 创建虚拟环境
if (-not (Test-Path "venv")) {
    Write-Host "创建虚拟环境..." -ForegroundColor Yellow
    python -m venv venv
}

# 激活虚拟环境
Write-Host "激活虚拟环境..." -ForegroundColor Yellow
& "venv\Scripts\Activate.ps1"

# 升级 pip
Write-Host "升级 pip..." -ForegroundColor Yellow
python -m pip install --upgrade pip --quiet

# 安装依赖
Write-Host "安装依赖包..." -ForegroundColor Yellow
pip install -r requirements.txt --quiet

# 清理旧构建
Write-Host "清理旧构建..." -ForegroundColor Yellow
if (Test-Path "build") { Remove-Item -Recurse -Force "build" }
if (Test-Path "dist") { Remove-Item -Recurse -Force "dist" }
if (Test-Path "main.spec") { Remove-Item -Force "main.spec" }

# 创建图标
if (-not (Test-Path "icon.ico")) {
    Write-Host "创建默认图标..." -ForegroundColor Yellow
    python -c @"
from PIL import Image, ImageDraw
img = Image.new('RGB', (256, 256), 'white')
draw = ImageDraw.Draw(img)
draw.ellipse([32, 32, 224, 224], fill='#4CAF50')
draw.ellipse([96, 80, 160, 144], fill='white')
draw.rectangle([96, 112, 160, 160], fill='white')
draw.rectangle([120, 160, 132, 192], fill='white')
draw.rectangle([96, 192, 160, 208], fill='white')
img.save('icon.ico')
"@
}

# 打包
Write-Host "开始打包..." -ForegroundColor Yellow
pyinstaller --clean `
    --onefile `
    --noconsole `
    --name VoiceTyper `
    --icon icon.ico `
    --add-data "config.py;." `
    --hidden-import tornado `
    --hidden-import keyboard `
    --hidden-import pynput `
    --hidden-import win32clipboard `
    --hidden-import win32con `
    --hidden-import pystray `
    --hidden-import PIL `
    main.py

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "✗ 打包失败！" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "✓ 打包完成！" -ForegroundColor Green
Write-Host "可执行文件位置: dist\VoiceTyper.exe" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "使用说明:" -ForegroundColor Cyan
Write-Host "1. 运行 dist\VoiceTyper.exe"
Write-Host "2. 程序会在系统托盘显示图标"
Write-Host "3. 配置文件自动创建在: %APPDATA%\VoiceTyper\"
Write-Host "4. 默认热键: Ctrl+Space（可在配置文件中修改）"
Write-Host ""

# 询问是否运行
$run = Read-Host "是否立即运行程序？(Y/N)"
if ($run -eq "Y" -or $run -eq "y") {
    Start-Process "dist\VoiceTyper.exe"
}