#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_PATH="$ROOT_DIR/VoiceTyper.xcodeproj"
BUILD_DIR="$ROOT_DIR/build/xcode"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="VoiceTyper"

ruby "$ROOT_DIR/scripts/generate_xcodeproj.rb"

rm -rf "$BUILD_DIR" "$DIST_DIR/$APP_NAME.app" "$DIST_DIR/$APP_NAME-macOS.zip"
mkdir -p "$BUILD_DIR" "$DIST_DIR"

xcodebuild \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME" \
  -configuration Release \
  -derivedDataPath "$BUILD_DIR" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  build

APP_PATH="$BUILD_DIR/Build/Products/Release/$APP_NAME.app"

if [ ! -d "$APP_PATH" ]; then
  echo "未找到构建产物: $APP_PATH" >&2
  exit 1
fi

cp -R "$APP_PATH" "$DIST_DIR/"
cd "$DIST_DIR"
/usr/bin/zip -r -q "$APP_NAME-macOS.zip" "$APP_NAME.app"

echo "构建完成:"
echo "  App: $DIST_DIR/$APP_NAME.app"
echo "  Zip: $DIST_DIR/$APP_NAME-macOS.zip"
