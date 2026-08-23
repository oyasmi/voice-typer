#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_PATH="$ROOT_DIR/VoiceTyper.xcodeproj"
BUILD_DIR="$ROOT_DIR/build/xcode"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="VoiceTyper"
INSTALL_GUIDE_PATH="$ROOT_DIR/packaging/INSTALL.txt"
ENTITLEMENTS_PATH="$ROOT_DIR/Resources/VoiceTyper.entitlements"
ORT_FRAMEWORK_REL="Contents/Frameworks/onnxruntime.framework/Versions/A/onnxruntime"

# 签名与公证（可选，见 macos/README.md「签名与公证」一节）：
#
# - 默认（不设置任何下列变量）：完全保持此前行为——ad-hoc 本机签名，不做公证。
#   这也是绝大多数贡献者（没有付费 Apple Developer 账号）应该使用的路径。
# - 若设置 VOICETYPER_SIGN_IDENTITY 为一个真实的 "Developer ID Application: ..." 身份
#   （`security find-identity -v -p codesigning` 可查看本机可用身份），构建会：
#   1. 用该身份 + 强化运行时（--options runtime）分别签名每个内嵌 framework/dylib，
#      再签名主 App——而不是用 `--deep`（Apple 官方公证指南明确不推荐 `--deep`：
#      它会用相同的笼统参数签名所有内嵌代码，容易掩盖某个内嵌项本该有的独立签名要求）。
#   2. 若同时设置 VOICETYPER_NOTARY_PROFILE（一个已用
#      `xcrun notarytool store-credentials <profile> --apple-id ... --team-id ... --password ...`
#      存过的 Keychain profile 名），会把签名后的 App 提交公证、等待结果，
#      成功后用 `xcrun stapler staple` 把凭据订到 .app 上，再打包 zip/dmg
#      （必须在装订之后打包，否则发出去的产物不含公证凭据、离线时 Gatekeeper 仍会拦截）。
# - 三者都要求本机已安装 Xcode 命令行工具，且 VOICETYPER_SIGN_IDENTITY 对应的证书
#   已在本机 Keychain 中；这些前提本仓库无法在 CI/沙盒环境中验证，需要在真机走通一次
#   完整流程后再据此更新 README 的验证状态（R3-16）。
SIGN_IDENTITY="${VOICETYPER_SIGN_IDENTITY:--}"
NOTARY_PROFILE="${VOICETYPER_NOTARY_PROFILE:-}"

rm -rf "$BUILD_DIR" "$DIST_DIR"
mkdir -p "$BUILD_DIR" "$DIST_DIR"

# 解析依赖（Yams + onnxruntime-swift-package-manager）
xcodebuild \
  -resolvePackageDependencies \
  -project "$PROJECT_PATH" \
  -scheme "$APP_NAME"

# 只出 arm64（见 macos/DESIGN.md 决策记录 D2）：Intel Mac 上 int8 推理体验未验证，
# 不做承诺；也省去纯 x86_64 / universal 变体的打包与签名步骤。
# 构建期不签名（CODE_SIGNING_ALLOWED=NO）：签名统一放在下面手动完成，逐个内嵌项
# 单独签名 + 强化运行时，而不是让 Xcode 在构建期间签一次、随后又被 lipo 破坏、
# 最后再整体重签一次。
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
# lipo 会破坏签名，必须在 lipo 之后才签名（下面统一处理）。
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

sign_app_bundle() {
  if [ "$SIGN_IDENTITY" = "-" ]; then
    # ad-hoc：保持此前行为，`--deep` 在这里没有 Developer ID 场景那些顾虑
    # （反正没有强化运行时、也不会被公证），沿用最简单的写法。
    codesign --force --deep -s - "$APP_PATH"
    return
  fi

  echo "使用 Developer ID 身份签名: $SIGN_IDENTITY"
  # 逐个内嵌 framework/dylib 单独签名 + 强化运行时，而不是 `--deep`：
  # Apple 官方公证指南明确建议自底向上逐个签名，`--deep` 用同一套参数覆盖全部
  # 内嵌代码，容易掩盖某个内嵌项本该有的独立签名要求，也是公证时最常见的失败原因之一。
  find "$APP_PATH/Contents/Frameworks" -mindepth 1 -maxdepth 1 \
    \( -name "*.framework" -o -name "*.dylib" \) 2>/dev/null | while read -r item; do
    echo "  签名内嵌项: $(basename "$item")"
    codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$item"
  done

  codesign --force --options runtime --timestamp \
    --entitlements "$ENTITLEMENTS_PATH" \
    --sign "$SIGN_IDENTITY" "$APP_PATH"

  echo "校验签名…"
  codesign --verify --deep --strict --verbose=2 "$APP_PATH"
}

sign_app_bundle

notarize_and_staple() {
  [ -n "$NOTARY_PROFILE" ] || return 0
  [ "$SIGN_IDENTITY" != "-" ] || {
    echo "警告: 设置了 VOICETYPER_NOTARY_PROFILE 但未设置 VOICETYPER_SIGN_IDENTITY，跳过公证" >&2
    return 0
  }

  echo "提交公证（keychain profile: $NOTARY_PROFILE）…"
  local submission_zip="$BUILD_DIR/notarize-submission.zip"
  (cd "$(dirname "$APP_PATH")" && /usr/bin/ditto -c -k --keepParent "$APP_NAME.app" "$submission_zip")

  if ! xcrun notarytool submit "$submission_zip" --keychain-profile "$NOTARY_PROFILE" --wait; then
    echo "公证失败，尝试获取详细日志…" >&2
    local submission_id
    submission_id=$(xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" | awk '/id:/{print $2; exit}')
    [ -n "$submission_id" ] && xcrun notarytool log "$submission_id" --keychain-profile "$NOTARY_PROFILE" || true
    exit 1
  fi

  echo "公证通过，装订凭据到 .app…"
  xcrun stapler staple "$APP_PATH"
  rm -f "$submission_zip"
}

notarize_and_staple

ZIP_NAME="$APP_NAME-$VERSION-macOS-arm64.zip"
DMG_NAME="$APP_NAME-$VERSION-macOS-arm64.dmg"

# 必须在装订（若有）完成之后才打最终包，否则发出去的 zip/dmg 里的 .app 不含公证凭据，
# 用户离线时 Gatekeeper 仍会拦截。
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

if [ -n "$NOTARY_PROFILE" ] && [ "$SIGN_IDENTITY" != "-" ]; then
  # DMG 本身也支持单独装订；.app 内已装订，这一步是锦上添花，失败不影响发布
  # （用户解压/挂载后拿到的 .app 本身已经带凭据）。
  xcrun stapler staple "$DIST_DIR/$DMG_NAME" || true
fi

echo ""
echo "构建完成:"
ls -lh "$DIST_DIR"/*.zip "$DIST_DIR"/*.dmg
echo ""
echo "注意: 本次构建不包含语音模型（约 230MB），首次启动时应用会引导下载。"
echo "如需离线预置模型用于测试，参见 scripts/fetch_model.sh。"
if [ "$SIGN_IDENTITY" = "-" ]; then
  echo ""
  echo "当前为 ad-hoc 签名（默认）：每次更新都可能导致用户需要重新授权系统权限，"
  echo "且无法通过公证分发给其他用户直接双击打开。如需正式分发，参见 macos/README.md「签名与公证」。"
fi
