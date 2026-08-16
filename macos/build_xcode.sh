#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_PATH="$ROOT_DIR/VoiceTyper.xcodeproj"
BUILD_DIR="$ROOT_DIR/build/xcode"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="VoiceTyper"
INSTALL_GUIDE_PATH="$ROOT_DIR/packaging/INSTALL.txt"
EXECUTABLE_REL="Contents/MacOS/$APP_NAME"
ORT_FRAMEWORK_REL="Contents/Frameworks/onnxruntime.framework/Versions/A/onnxruntime"

rm -rf "$BUILD_DIR" "$DIST_DIR"
mkdir -p "$BUILD_DIR" "$DIST_DIR"

# 解析依赖（Yams + onnxruntime-swift-package-manager）
xcodebuild \
  -resolvePackageDependencies \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME"

# 只出 arm64（见 macos/DESIGN.md 决策记录 D2）：Intel Mac 上 int8 推理体验未验证，
# 不做承诺；也省去纯 x86_64 / universal 变体的打包与签名步骤。
xcodebuild \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME" \
  -configuration Release \
  -derivedDataPath "$BUILD_DIR" \
  ARCHS="arm64" \
  ONLY_ACTIVE_ARCH=NO \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  build

APP_PATH="$BUILD_DIR/Build/Products/Release/$APP_NAME.app"

if [ ! -d "$APP_PATH" ]; then
  echo "未找到构建产物: $APP_PATH" >&2
  exit 1
fi

VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP_PATH/Contents/Info.plist")
echo "检测到版本号: $VERSION"

# ONNX Runtime 的 xcframework 只提供 macos-arm64_x86_64 通用切片，Xcode 会原样拷进
# Contents/Frameworks/；瘦成单 arm64 能把这部分从 90MB 降到 45MB，占最终包体积近一半。
# lipo 会破坏 linker-signed 签名，之后必须重新签名。
ORT_BINARY="$APP_PATH/$ORT_FRAMEWORK_REL"
if [ -f "$ORT_BINARY" ]; then
  ARCHS_IN_BINARY=$(lipo -archs "$ORT_BINARY")
  if [ "$ARCHS_IN_BINARY" != "arm64" ]; then
    echo "瘦身 onnxruntime.framework: $ARCHS_IN_BINARY -> arm64"
    lipo "$ORT_BINARY" -thin arm64 -output "$ORT_BINARY.thin"
    mv "$ORT_BINARY.thin" "$ORT_BINARY"
  fi
else
  echo "警告: 未找到 onnxruntime 二进制，跳过瘦身: $ORT_BINARY" >&2
fi

# adhoc 重签名（lipo 会破坏 linker-signed 签名，Apple Silicon 强制要求有效签名）
codesign --force --deep -s - "$APP_PATH"

ZIP_NAME="$APP_NAME-$VERSION-macOS-arm64.zip"
DMG_NAME="$APP_NAME-$VERSION-macOS-arm64.dmg"

(cd "$(dirname "$APP_PATH")" && /usr/bin/zip -r -q "$DIST_DIR/$ZIP_NAME" "$APP_NAME.app")

DMG_STAGE="$BUILD_DIR/dmg-root"
mkdir -p "$DMG_STAGE"
cp -R "$APP_PATH" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"
cp "$INSTALL_GUIDE_PATH" "$DMG_STAGE/INSTALL.txt"
/usr/bin/hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_STAGE" \
  -ov \
  -format UDZO \
  "$DIST_DIR/$DMG_NAME" >/dev/null
rm -rf "$DMG_STAGE"

echo ""
echo "构建完成:"
ls -lh "$DIST_DIR"/*.zip "$DIST_DIR"/*.dmg
echo ""
echo "注意: 本次构建不包含语音模型（约 230MB），首次启动时应用会引导下载。"
echo "如需离线预置模型用于测试，参见 scripts/fetch_model.sh。"
