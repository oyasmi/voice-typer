#!/bin/bash
# VoiceTyper 客户端打包脚本 (PyInstaller)

set -e

APP_NAME="VoiceTyper"
VERSION="1.4.1"

echo "========================================"
echo "  $APP_NAME 客户端打包"
echo "  版本: $VERSION"
echo "========================================"
echo ""

# 检查 Python
if ! command -v python &> /dev/null; then
    echo "错误: 未找到 Python"
    exit 1
fi

echo "Python: $(python --version)"
echo ""

# 检查图标
if [ -f "assets/icon.icns" ]; then
    echo "图标: assets/icon.icns ✓"
else
    echo "图标: 未找到 (将使用默认图标)"
fi
echo ""

# 安装 pyinstaller
echo "[1/4] 检查 PyInstaller..."
pip install pyinstaller -q
echo "  PyInstaller 已就绪"

# 清理
echo "[2/4] 清理旧构建..."
rm -rf build dist *.spec.bak

# 打包
echo "[3/4] 打包应用 (请稍候)..."
echo ""
pyinstaller voicetyper.spec --noconfirm 2>&1 | while IFS= read -r line; do
    if [[ $line == *"Building"* ]] || [[ $line == *"completed"* ]]; then
        echo "  $line"
    elif [[ $line == *"ERROR"* ]] || [[ $line == *"Error"* ]]; then
        echo "  错误: $line"
    fi
done

# 检查结果
echo ""
if [ -d "dist/$APP_NAME.app" ]; then
    echo "[4/4] 打包完成!"
    echo ""
    echo "========================================"
    APP_SIZE=$(du -sh "dist/$APP_NAME.app" | cut -f1)
    echo "  应用: dist/$APP_NAME.app"
    echo "  大小: $APP_SIZE"
    echo "========================================"
    echo ""
    echo "安装: 将 dist/$APP_NAME.app 拖到「应用程序」文件夹"
    echo ""
    echo "注意: 首次运行需在「系统设置 → 隐私与安全性」中授权:"
    echo "  - 辅助功能"
    echo "  - 麦克风"
    echo ""
    echo "测试: open dist/$APP_NAME.app"
    echo ""
else
    echo "[4/4] 打包失败!"
    echo ""
    echo "请查看完整日志: pyinstaller voicetyper.spec --noconfirm"
    exit 1
fi
