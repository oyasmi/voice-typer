# 开发/测试用：命令行下载 SenseVoice-Small 模型到应用运行时会用到的同一个位置
# (%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\)，供跑金标准测试或跳过首启下载引导时使用。
#
# 生产环境下应用本身会在首次启动时通过设置窗口引导用户完成同样的下载
# （见 Asr/ModelDownloader.cs），这里只是等价的命令行版本，端点、文件清单、sha256
# 必须与 ModelDownloader.cs 保持一致（也与 macos/scripts/fetch_model.sh 完全相同——
# 两个平台共用同一份模型文件）。
#
# 用法： powershell -ExecutionPolicy Bypass -File scripts\fetch_model.ps1

$ErrorActionPreference = "Stop"

$DestDir = Join-Path $env:LOCALAPPDATA "VoiceTyper\models\sensevoice-small"
$BaseUrl = "https://www.modelscope.cn/api/v1/models/iic/SenseVoiceSmall-onnx/repo"

New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

$Files = @(
    @{ Name = "config.yaml";       Sha256 = "f71e239ba36705564b5bf2d2ffd07eece07b8e3f2bbf6d2c99d8df856339ac19" }
    @{ Name = "am.mvn";            Sha256 = "29b3c740a2c0cfc6b308126d31d7f265fa2be74f3bb095cd2f143ea970896ae5" }
    @{ Name = "tokens.json";       Sha256 = "a2594fc1474e78973149cba8cd1f603ebed8c39c7decb470631f66e70ce58e97" }
    @{ Name = "model_quant.onnx";  Sha256 = "21dc965f689a78d1604717bf561e40d5a236087c85a95584567835750549e822" }
)

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

foreach ($file in $Files) {
    $name = $file.Name
    $expected = $file.Sha256.ToLowerInvariant()
    $dest = Join-Path $DestDir $name
    $part = "$dest.part"

    if (Test-Path $dest) {
        $actual = Get-FileSha256 $dest
        if ($actual -eq $expected) {
            Write-Host "已存在且校验通过，跳过: $name"
            continue
        }
        Write-Host "已存在但校验不通过，重新下载: $name"
    }

    Write-Host "下载: $name"
    $url = "${BaseUrl}?Revision=master&FilePath=$name"
    Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing

    $actual = Get-FileSha256 $part
    if ($actual -ne $expected) {
        Write-Error "校验失败: $name (期望 $expected，实际 $actual)"
        Remove-Item $part -Force -ErrorAction SilentlyContinue
        exit 1
    }
    Move-Item -Force $part $dest
    Write-Host "  校验通过: $name"
}

Write-Host ""
Write-Host "模型已就绪: $DestDir"
