<#
.SYNOPSIS
    VoiceTyper Windows 构建脚本
.DESCRIPTION
    检查 GCC 环境并启用 CGO 进行构建
#>

$ErrorActionPreference = "Stop"

Write-Host "正在检查构建环境..." -ForegroundColor Cyan

# 1. 检查 GCC (CGO 需要)
if (Get-Command gcc -ErrorAction SilentlyContinue) {
    Write-Host "[OK] 检测到 GCC" -ForegroundColor Green
} else {
    Write-Host "[ERROR] 未找到 GCC！'robotgo' 和 'malgo' 需要 C 编译器。" -ForegroundColor Red
    Write-Host "请安装 MinGW-w64 或 MSYS2 并将其添加到 PATH。"
    Write-Host "下载地址: https://www.mingw-w64.org/downloads/"
    exit 1
}

# 2. 设置环境变量
$env:GOOS = "windows"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "1"
Write-Host "已设置 CGO_ENABLED=1" -ForegroundColor Gray

# 3. 构建
Write-Host "正在构建 VoiceTyper.exe..." -ForegroundColor Cyan
try {
    go build -ldflags="-s -w -H=windowsgui" -o VoiceTyper.exe main.go
    Write-Host "构建成功！可执行文件: .\VoiceTyper.exe" -ForegroundColor Green
} catch {
    Write-Host "构建失败。" -ForegroundColor Red
    exit 1
}
