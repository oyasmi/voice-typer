#!/usr/bin/env bash
# 在 Linux/macOS 上交叉发布 Windows 版（framework-dependent 目录式部署）。
#
# 只证明"能编译、能发布、产物结构正确"，不能运行，也不能替代 Windows 真机验证。
# 同时打出便携版 zip（需要 zip 命令）。不生成 Inno Setup 安装包，也不签名；这两步仍需在 Windows 上用 build.bat 完成。
#
# 用法：scripts/cross_publish_win_from_linux.sh [win-x64|win-arm64 ...]   （默认两个 RID 都发布）
set -euo pipefail

cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[ERROR] 未找到 dotnet，请安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
fi

if ! command -v zip >/dev/null 2>&1; then
    echo "[ERROR] 未找到 zip 命令，无法打包便携版" >&2
    exit 1
fi

rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
    rids=(win-x64 win-arm64)
fi

version=$(dotnet msbuild VoiceTyper.csproj -getProperty:Version -nologo 2>/dev/null | tr -d '[:space:]')
version=${version:-unknown}

# 与 scripts/verify_publish.ps1 的必需文件清单保持一致（该脚本只能在装有 PowerShell 的环境运行）。
required_files=(
    VoiceTyper.exe VoiceTyper.dll VoiceTyper.deps.json VoiceTyper.runtimeconfig.json
    onnxruntime.dll Microsoft.ML.OnnxRuntime.dll NAudio.Core.dll NAudio.Wasapi.dll
    YamlDotNet.dll Assets/icon.ico Resources/correction.md
)

for rid in "${rids[@]}"; do
    case "$rid" in
        win-x64|win-arm64) ;;
        *) echo "[ERROR] 不支持的 RID：$rid（仅支持 win-x64、win-arm64）" >&2; exit 1 ;;
    esac

    out="dist-cross/$rid"
    echo "[cross] 发布 $rid（$version）→ $out"
    rm -rf "$out"
    dotnet publish VoiceTyper.csproj -c Release -r "$rid" \
        --self-contained false \
        -p:PublishSingleFile=false \
        -p:EnableWindowsTargeting=true \
        -o "$out" --nologo -v q

    for f in "${required_files[@]}"; do
        if [ ! -f "$out/$f" ]; then
            echo "[ERROR] 发布目录缺少必需文件：$rid/$f" >&2
            exit 1
        fi
    done
    echo "[cross] $rid 产物检查通过"

    # 便携版 zip：命名与内容和 build.bat 一致（发布目录全部文件，无外层目录）。
    zip_path="$PWD/dist-cross/VoiceTyper-$version-$rid-portable.zip"
    rm -f "$zip_path"
    (cd "$out" && zip -qr "$zip_path" .)
    echo "[cross] 便携版：dist-cross/$(basename "$zip_path")"
done

echo "[cross] 完成。产物在 windows/dist-cross/，未经 Windows 真机验证。"
