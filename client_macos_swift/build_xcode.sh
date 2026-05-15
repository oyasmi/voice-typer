#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_PATH="$ROOT_DIR/VoiceTyper.xcodeproj"
BUILD_DIR="$ROOT_DIR/build/xcode"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="VoiceTyper"
INSTALL_GUIDE_PATH="$ROOT_DIR/packaging/INSTALL.txt"
EXECUTABLE_REL="Contents/MacOS/$APP_NAME"

rm -rf "$BUILD_DIR" "$DIST_DIR"
mkdir -p "$BUILD_DIR" "$DIST_DIR"

# 解析依赖
xcodebuild \
  -resolvePackageDependencies \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME"

# 构建 Universal Binary（arm64 + x86_64）
xcodebuild \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME" \
  -configuration Release \
  -derivedDataPath "$BUILD_DIR" \
  ARCHS="arm64 x86_64" \
  ONLY_ACTIVE_ARCH=NO \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  build

UNIVERSAL_APP="$BUILD_DIR/Build/Products/Release/$APP_NAME.app"

if [ ! -d "$UNIVERSAL_APP" ]; then
  echo "未找到构建产物: $UNIVERSAL_APP" >&2
  exit 1
fi

VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$UNIVERSAL_APP/Contents/Info.plist")
echo "检测到版本号: $VERSION"

# 为指定架构打包 zip + dmg
package_variant() {
  local arch="$1"
  local suffix="$2"
  local variant_dir="$BUILD_DIR/variants/$suffix"
  local app_dir="$variant_dir/$APP_NAME.app"
  local zip_name="$APP_NAME-$VERSION-macOS-$suffix.zip"
  local dmg_name="$APP_NAME-$VERSION-macOS-$suffix.dmg"
  local dmg_stage="$variant_dir/dmg-root"

  mkdir -p "$variant_dir"
  cp -R "$UNIVERSAL_APP" "$app_dir"

  # 非 universal 时用 lipo 提取单架构
  if [ "$arch" != "universal" ]; then
    lipo "$app_dir/$EXECUTABLE_REL" -thin "$arch" -output "$app_dir/$EXECUTABLE_REL.thin"
    mv "$app_dir/$EXECUTABLE_REL.thin" "$app_dir/$EXECUTABLE_REL"
  fi

  # adhoc 重签名（lipo 会破坏 linker-signed 签名，Apple Silicon 强制要求有效签名）
  codesign --force --deep -s - "$app_dir"

  # Zip
  (cd "$variant_dir" && /usr/bin/zip -r -q "$DIST_DIR/$zip_name" "$APP_NAME.app")

  # DMG
  mkdir -p "$dmg_stage"
  cp -R "$app_dir" "$dmg_stage/"
  ln -s /Applications "$dmg_stage/Applications"
  cp "$INSTALL_GUIDE_PATH" "$dmg_stage/INSTALL.txt"
  /usr/bin/hdiutil create \
    -volname "$APP_NAME" \
    -srcfolder "$dmg_stage" \
    -ov \
    -format UDZO \
    "$DIST_DIR/$dmg_name" >/dev/null
  rm -rf "$dmg_stage"

  echo "  $suffix: $DIST_DIR/$zip_name, $DIST_DIR/$dmg_name"
}

echo ""
echo "正在打包各架构分发包..."
package_variant "arm64" "arm64"
package_variant "x86_64" "x86_64"
package_variant "universal" "universal"

echo ""
echo "构建完成:"
ls -lh "$DIST_DIR"/*.zip "$DIST_DIR"/*.dmg
