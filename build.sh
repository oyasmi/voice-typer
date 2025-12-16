#!/bin/bash
# VoiceTyper 打包脚本 (PyInstaller)

set -e

APP_NAME="VoiceTyper"
VERSION="1.0.0"

echo "========================================"
echo "  VoiceTyper 打包脚本"
echo "  版本: $VERSION"
echo "========================================"
echo ""

# 检查 Python 环境
if ! command -v python &> /dev/null; then
    echo "错误: 未找到 Python"
    exit 1
fi

PYTHON_VERSION=$(python --version 2>&1)
echo "Python: $PYTHON_VERSION"

# 检查虚拟环境
if [ -z "$VIRTUAL_ENV" ]; then
    echo ""
    echo "警告: 建议在虚拟环境中运行"
    read -p "是否继续? [y/N] " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

# 安装打包依赖
echo ""
echo "[1/5] 检查打包依赖..."
pip install pyinstaller -q
echo "  PyInstaller 已就绪"

# 清理旧的构建
echo ""
echo "[2/5] 清理旧的构建..."
rm -rf build dist *.spec.bak

# 准备资源
echo ""
echo "[3/5] 准备资源文件..."
mkdir -p assets

if [ ! -f "assets/icon.icns" ]; then
    echo "  提示: 未找到图标文件 assets/icon.icns"
    echo "  将使用默认图标"
fi

# 打包
echo ""
echo "[4/5] 打包应用 (这可能需要几分钟)..."
echo ""

pyinstaller voicetyper.spec --noconfirm 2>&1 | while IFS= read -r line; do
    # 显示进度
    if [[ $line == *"INFO"* ]]; then
        # 简化输出
        if [[ $line == *"Building"* ]] || [[ $line == *"Copying"* ]] || [[ $line == *"completed"* ]]; then
            echo "  $line"
        fi
    elif [[ $line == *"WARNING"* ]]; then
        : # 忽略警告
    elif [[ $line == *"ERROR"* ]] || [[ $line == *"Error"* ]]; then
        echo "  错误: $line"
    fi
done

# 检查结果
echo ""
if [ -d "dist/$APP_NAME.app" ]; then
    echo "[5/5] 打包完成!"
    echo ""
    echo "========================================"
    
    APP_SIZE=$(du -sh "dist/$APP_NAME.app" | cut -f1)
    echo "  应用: dist/$APP_NAME.app"
    echo "  大小: $APP_SIZE"
    echo "========================================"
    echo ""
    echo "安装方法:"
    echo "  将 dist/$APP_NAME.app 拖到「应用程序」文件夹"
    echo ""
    echo "首次运行需授权:"
    echo "  系统设置 → 隐私与安全性 → 辅助功能"
    echo "  系统设置 → 隐私与安全性 → 麦克风"
    echo ""
    echo "测试运行:"
    echo "  open dist/$APP_NAME.app"
    echo ""
else
    echo "[5/5] 打包失败!"
    echo ""
    echo "请查看完整日志:"
    echo "  pyinstaller voicetyper.spec --noconfirm"
    exit 1
fi