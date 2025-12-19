#!/bin/bash
# VoiceTyper 客户端打包脚本

set -e

APP_NAME="VoiceTyper"
VERSION="1.0.0"
BUNDLE_ID="com.voicetyper.app"

DIST_DIR="dist"
APP_DIR="$DIST_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

echo "========================================"
echo "  $APP_NAME 客户端打包"
echo "========================================"
echo ""

rm -rf "$DIST_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# 复制源码
echo "[1/3] 复制源代码..."
for f in main.py config.py controller.py recorder.py asr_client.py text_inserter.py indicator.py; do
    cp "$f" "$RESOURCES_DIR/"
done
echo "  已复制 7 个文件"

# 创建启动脚本
echo "[2/3] 创建启动脚本..."
cat > "$MACOS_DIR/$APP_NAME" << 'LAUNCHER'
#!/bin/bash

DIR="$(cd "$(dirname "$0")/../Resources" && pwd)"
cd "$DIR"

LOG_DIR="$HOME/.config/voice_typer"
LOG_FILE="$LOG_DIR/app.log"
mkdir -p "$LOG_DIR"

# 查找 Python
find_python() {
    for p in "$HOME/.pyenv/shims/python3" "/opt/homebrew/bin/python3" "/usr/local/bin/python3"; do
        [ -x "$p" ] && echo "$p" && return
    done
    command -v python3 2>/dev/null
}

PYTHON=$(find_python)

if [ -z "$PYTHON" ]; then
    osascript -e 'display alert "VoiceTyper" message "未找到 Python 3\n\n请安装: brew install python@3.12" as critical'
    exit 1
fi

# 检查依赖
check_dep() { $PYTHON -c "import $1" 2>/dev/null; }

MISSING=""
check_dep "tornado" || MISSING="${MISSING}tornado "
check_dep "sounddevice" || MISSING="${MISSING}sounddevice "
check_dep "pynput" || MISSING="${MISSING}pynput "
check_dep "rumps" || MISSING="${MISSING}rumps "
check_dep "yaml" || MISSING="${MISSING}PyYAML "

if [ -n "$MISSING" ]; then
    osascript -e "display alert \"VoiceTyper\" message \"缺少依赖: $MISSING\n\n请运行:\npip3 install tornado sounddevice pynput rumps pyobjc-framework-Cocoa PyYAML numpy\" as critical"
    exit 1
fi

exec $PYTHON main.py >> "$LOG_FILE" 2>&1
LAUNCHER

chmod +x "$MACOS_DIR/$APP_NAME"

# 创建 Info.plist
echo "[3/3] 创建 Info.plist..."
cat > "$CONTENTS_DIR/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSMicrophoneUsageDescription</key><string>VoiceTyper 需要麦克风进行语音输入</string>
</dict>
</plist>
PLIST

# 复制图标
[ -f "assets/icon.icns" ] && cp "assets/icon.icns" "$RESOURCES_DIR/"

echo ""
echo "========================================"
echo "  打包完成: $APP_DIR"
echo "========================================"
echo ""
echo "测试: open $APP_DIR"
echo ""