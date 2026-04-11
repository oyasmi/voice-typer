#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_PATH="$ROOT_DIR/VoiceTyper.xcodeproj"
BUILD_DIR="$ROOT_DIR/build/xcode"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="VoiceTyper"
ZIP_NAME="$APP_NAME-macOS.zip"
DMG_NAME="$APP_NAME-macOS.dmg"
DMG_STAGE_DIR="$BUILD_DIR/dmg-root"
INSTALL_GUIDE_PATH="$ROOT_DIR/packaging/INSTALL.txt"

ruby "$ROOT_DIR/scripts/generate_xcodeproj.rb"

rm -rf "$BUILD_DIR" "$DIST_DIR/$APP_NAME.app" "$DIST_DIR/$ZIP_NAME" "$DIST_DIR/$DMG_NAME"
mkdir -p "$BUILD_DIR" "$DIST_DIR"

xcodebuild \
  -resolvePackageDependencies \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME"

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
/usr/bin/zip -r -q "$ZIP_NAME" "$APP_NAME.app"

mkdir -p "$DMG_STAGE_DIR"
cp -R "$APP_PATH" "$DMG_STAGE_DIR/"
ln -s /Applications "$DMG_STAGE_DIR/Applications"
cp "$INSTALL_GUIDE_PATH" "$DMG_STAGE_DIR/INSTALL.txt"

/usr/bin/hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_STAGE_DIR" \
  -ov \
  -format UDZO \
  "$DIST_DIR/$DMG_NAME" >/dev/null

rm -rf "$DMG_STAGE_DIR"

echo "构建完成:"
echo "  App: $DIST_DIR/$APP_NAME.app"
echo "  Zip: $DIST_DIR/$ZIP_NAME"
echo "  DMG: $DIST_DIR/$DMG_NAME"
