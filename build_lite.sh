#!/bin/bash
# VoiceTyper 轻量打包脚本

set -e

APP_NAME="VoiceTyper"
VERSION="1.0.0"
BUNDLE_ID="com.voicetyper.app"
CONFIG_DIR_NAME="voice_typer"

DIST_DIR="dist"
APP_DIR="$DIST_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

echo "========================================"
echo "  $APP_NAME 轻量打包"
echo "  版本: $VERSION"
echo "========================================"
echo ""

# 清理
rm -rf "$DIST_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# 复制源代码
echo "[1/4] 复制源代码..."
cp main.py "$RESOURCES_DIR/"
cp config.py "$RESOURCES_DIR/"
cp controller.py "$RESOURCES_DIR/"
cp recorder.py "$RESOURCES_DIR/"
cp recognizer.py "$RESOURCES_DIR/"
cp text_inserter.py "$RESOURCES_DIR/"
cp indicator.py "$RESOURCES_DIR/"

echo "  已复制 7 个源文件"

# 创建启动脚本
echo "[2/4] 创建启动脚本..."
cat > "$MACOS_DIR/$APP_NAME" << 'LAUNCHER'
#!/bin/bash
# VoiceTyper 启动脚本

DIR="$(cd "$(dirname "$0")/../Resources" && pwd)"
cd "$DIR"

# 日志
LOG_DIR="$HOME/.config/voice_typer"
LOG_FILE="$LOG_DIR/app.log"
mkdir -p "$LOG_DIR"

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" >> "$LOG_FILE"
}

log "========== 启动 VoiceTyper =========="

# 查找 Python
find_python() {
    if [ -f "$HOME/.pyenv/shims/python3" ]; then
        echo "$HOME/.pyenv/shims/python3"
    elif [ -f "/opt/homebrew/bin/python3" ]; then
        echo "/opt/homebrew/bin/python3"
    elif [ -f "/usr/local/bin/python3" ]; then
        echo "/usr/local/bin/python3"
    elif command -v python3 &> /dev/null; then
        which python3
    else
        echo ""
    fi
}

PYTHON=$(find_python)
log "Python 路径: $PYTHON"

if [ -z "$PYTHON" ]; then
    log "错误: 未找到 Python"
    osascript -e 'display alert "VoiceTyper 错误" message "未找到 Python 3\n\n请安装 Python 3.9 或更高版本\n\n推荐使用 Homebrew:\nbrew install python@3.12" as critical'
    exit 1
fi

# 检查 Python 版本
PY_VERSION=$($PYTHON -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" 2>/dev/null)
log "Python 版本: $PY_VERSION"

PY_MAJOR=$($PYTHON -c "import sys; print(sys.version_info.major)" 2>/dev/null)
PY_MINOR=$($PYTHON -c "import sys; print(sys.version_info.minor)" 2>/dev/null)

if [ "$PY_MAJOR" -lt 3 ] || ([ "$PY_MAJOR" -eq 3 ] && [ "$PY_MINOR" -lt 9 ]); then
    log "错误: Python 版本过低"
    osascript -e "display alert \"VoiceTyper 错误\" message \"Python 版本过低 ($PY_VERSION)\n\n需要 Python 3.9 或更高版本\" as critical"
    exit 1
fi

# 检查依赖
check_dep() {
    $PYTHON -c "import $1" 2>/dev/null
    return $?
}

MISSING=""
check_dep "funasr" || MISSING="${MISSING}funasr "
check_dep "torch" || MISSING="${MISSING}torch "
check_dep "sounddevice" || MISSING="${MISSING}sounddevice "
check_dep "pynput" || MISSING="${MISSING}pynput "
check_dep "rumps" || MISSING="${MISSING}rumps "
check_dep "yaml" || MISSING="${MISSING}PyYAML "

if [ -n "$MISSING" ]; then
    log "错误: 缺少依赖: $MISSING"
    osascript -e "display alert \"VoiceTyper 缺少依赖\" message \"请在终端运行:\n\npip3 install funasr torch torchaudio sounddevice pynput rumps pyobjc-framework-Cocoa PyYAML 'numpy<2.0'\" as critical"
    exit 1
fi

log "依赖检查通过"

# 启动
log "启动应用..."
exec $PYTHON main.py >> "$LOG_FILE" 2>&1
LAUNCHER

chmod +x "$MACOS_DIR/$APP_NAME"

# 创建 Info.plist
echo "[3/4] 创建 Info.plist..."
cat > "$CONTENTS_DIR/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleIconFile</key>
    <string>icon</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>VoiceTyper 需要使用麦克风进行语音识别</string>
    <key>NSAppleEventsUsageDescription</key>
    <string>VoiceTyper 需要控制键盘以输入文字</string>
</dict>
</plist>
PLIST

# 处理图标
echo "[4/4] 处理资源..."
if [ -f "assets/icon.icns" ]; then
    cp "assets/icon.icns" "$RESOURCES_DIR/icon.icns"
    echo "  已复制应用图标"
else
    echo "  提示: 未找到 assets/icon.icns，将使用默认图标"
fi

# 完成
echo ""
echo "========================================"
echo "  打包完成!"
echo "========================================"
echo ""

APP_SIZE=$(du -sh "$APP_DIR" | cut -f1)
echo "应用: $APP_DIR"
echo "大小: $APP_SIZE"
echo ""
echo "配置目录: ~/.config/$CONFIG_DIR_NAME/"
echo ""
echo "========================================" 
echo "安装步骤:"
echo "========================================" 
echo ""
echo "1. 安装 Python 依赖 (如未安装):"
echo "   pip3 install funasr torch torchaudio sounddevice \\"
echo "                pynput rumps pyobjc-framework-Cocoa \\"
echo "                PyYAML 'numpy<2.0'"
echo ""
echo "2. 将应用拖到「应用程序」文件夹"
echo ""
echo "3. 首次运行需在「系统设置 → 隐私与安全性」中授权:"
echo "   - 辅助功能"
echo "   - 麦克风"
echo ""
echo "测试运行:"
echo "  open $APP_DIR"
echo ""