#!/bin/bash
# 开发/测试用：命令行下载 SenseVoice-Small 模型到应用运行时会用到的同一个位置
# （~/Library/Application Support/VoiceTyper/models/sensevoice-small/），
# 供跑金标准测试或跳过首启下载引导时使用。
#
# 生产环境下应用本身会在首次启动时通过设置窗口引导用户完成同样的下载
# （见 Sources/VoiceTyper/ASR/ModelDownloader.swift），这里只是等价的命令行版本，
# 端点、文件清单、sha256 必须与 ModelDownloader.swift 保持一致。
set -euo pipefail

DEST_DIR="$HOME/Library/Application Support/VoiceTyper/models/sensevoice-small"
BASE_URL="https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo"

mkdir -p "$DEST_DIR"

declare -a FILES=(
  "config.yaml:f71e239ba36705564b5bf2d2ffd07eece07b8e3f2bbf6d2c99d8df856339ac19"
  "am.mvn:29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5"
  "tokens.json:a2594fc1474e78973149cba8cd1f603ebed8c39c7decb470631f66e70ce58e97"
  "model_quant.onnx:21dc965f689a78d1604717bf561e40d5a236087c85a95584567835750549e822"
)

for entry in "${FILES[@]}"; do
  name="${entry%%:*}"
  expected_sha="${entry##*:}"
  dest="$DEST_DIR/$name"

  if [ -f "$dest" ]; then
    actual_sha=$(shasum -a 256 "$dest" | awk '{print $1}')
    if [ "$actual_sha" = "$expected_sha" ]; then
      echo "已存在且校验通过，跳过: $name"
      continue
    fi
    echo "已存在但校验不通过，重新下载: $name"
  fi

  echo "下载: $name"
  curl -fL --retry 3 -o "$dest.part" "${BASE_URL}?Revision=master&FilePath=${name}"

  actual_sha=$(shasum -a 256 "$dest.part" | awk '{print $1}')
  if [ "$actual_sha" != "$expected_sha" ]; then
    echo "校验失败: $name (期望 $expected_sha，实际 $actual_sha)" >&2
    rm -f "$dest.part"
    exit 1
  fi
  mv "$dest.part" "$dest"
  echo "  校验通过: $name"
done

echo ""
echo "模型已就绪: $DEST_DIR"
