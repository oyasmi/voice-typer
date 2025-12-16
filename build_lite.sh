#!/bin/bash
# VoiceTyper 轻量打包方案
# 创建一个启动器应用，依赖用户本地 Python 环境

APP_NAME="VoiceTyper"
VERSION="1.0.0"
DIST_DIR="dist"
APP_DIR="$DIST_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

echo "========================================"
echo "  VoiceTyper 轻量打包"
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
cp config.yaml "$RESOURCES_DIR/"

# 创建启动脚本
echo "[2/4] 创建启动脚本..."
cat > "$MACOS_DIR/$APP_NAME" << 'LAUNCHER'
#!/bin/bash
# VoiceTyper 启动脚本

# 获取 Resources 目录
DIR="$(cd "$(dirname "$0")/../Resources" && pwd)"
cd "$DIR"

# 日志文件
LOG_FILE="$HOME/.config/voice_input/app.log"
mkdir -p "$(dirname "$LOG_FILE")"

# 查找 Python（优先使用 Homebrew 或 pyenv 的 Python）
find_python() {
    # 优先级：pyenv > homebrew > system
    if [ -f "$HOME/.pyenv/shims/python3" ]; then
        echo "$HOME/.pyenv/shims/python3"
    elif [ -f "/opt/homebrew/bin/python3" ]; then
        echo "/opt/homebrew/bin/python3"
    elif [ -f "/usr/local/bin/python3" ]; then
        echo "/usr/local/bin/python3"
    elif command -v python3 &> /dev/null; then
        echo "python3"
    else
        echo ""
    fi
}

PYTHON=$(find_python)

if [ -z "$PYTHON" ]; then
    osascript -e 'display alert "VoiceTyper 错误" message "未找到 Python 3，请先安装 Python 3.9 或更高版本。\n\n推荐使用 Homebrew 安装：\nbrew install python@3.12" as critical'
    exit 1
fi

# 检查 Python 版本
PY_VERSION=$($PYTHON -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" 2>/dev/null)
PY_MAJOR=$($PYTHON -c "import sys; print(sys.version_info.major)" 2>/dev/null)
PY_MINOR=$($PYTHON -c "import sys; print(sys.version_info.minor)" 2>/dev/null)

if [ "$PY_MAJOR" -lt 3 ] || ([ "$PY_MAJOR" -eq 3 ] && [ "$PY_MINOR" -lt 9 ]); then
    osascript -e "display alert \"VoiceTyper 错误\" message \"Python 版本过低 ($PY_VERSION)，需要 Python 3.9 或更高版本。\" as critical"
    exit 1
fi

# 检查核心依赖
check_dependency() {
    $PYTHON -c "import $1" 2>/dev/null
    return $?
}

MISSING_DEPS=""

if ! check_dependency "funasr"; then
    MISSING_DEPS="${MISSING_DEPS}funasr "
fi

if ! check_dependency "torch"; then
    MISSING_DEPS="${MISSING_DEPS}torch "
fi

if ! check_dependency "sounddevice"; then
    MISSING_DEPS="${MISSING_DEPS}sounddevice "
fi

if ! check_dependency "pynput"; then
    MISSING_DEPS="${MISSING_DEPS}pynput "
fi

if ! check_dependency "rumps"; then
    MISSING_DEPS="${MISSING_DEPS}rumps "
fi

if [ -n "$MISSING_DEPS" ]; then
    osascript -e "display alert \"VoiceTyper 缺少依赖\" message \"请在终端中运行以下命令安装依赖：\n\npip3 install funasr torch torchaudio sounddevice pynput rumps pyobjc-framework-Cocoa PyYAML 'numpy<2.0'\" as critical"
    exit 1
fi

# 启动应用
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
    <string>com.voicetyper.app</string>
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

# 复制图标
echo "[4/4] 处理图标..."
if [ -f "assets/icon.icns" ]; then
    cp "assets/icon.icns" "$RESOURCES_DIR/icon.icns"
    echo "  已复制图标"
else
    echo "  未找到图标文件，使用默认图标"
fi

# 完成
echo ""
echo "========================================"
echo "  打包完成!"
echo "========================================"
echo ""
echo "应用位置: $APP_DIR"
APP_SIZE=$(du -sh "$APP_DIR" | cut -f1)
echo "应用大小: $APP_SIZE"
echo ""
echo "注意: 此为轻量版，需要用户系统已安装 Python 和依赖"
echo ""
echo "安装依赖命令:"
echo "  pip3 install funasr torch torchaudio sounddevice pynput rumps pyobjc-framework-Cocoa PyYAML 'numpy<2.0'"
echo ""
echo "测试运行:"
echo "  open $APP_DIR"
echo ""