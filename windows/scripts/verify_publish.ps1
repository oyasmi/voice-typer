param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

# 构建与 CI 共用的发布验收：防止重新混入运行时、测试和开发用文件。
$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$requiredFiles = @(
    'VoiceTyper.exe', 'VoiceTyper.dll', 'VoiceTyper.deps.json',
    'VoiceTyper.runtimeconfig.json', 'onnxruntime.dll',
    'Microsoft.ML.OnnxRuntime.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll',
    'YamlDotNet.dll', 'Assets\icon.ico', 'Resources\correction.md'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $name) -PathType Leaf)) {
        throw "发布目录缺少必需文件：$name"
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'VoiceTyper.runtimeconfig.json') -Raw |
    ConvertFrom-Json
$frameworkNames = @($runtimeConfig.runtimeOptions.frameworks | ForEach-Object { $_.name })
if ($runtimeConfig.runtimeOptions.includedFrameworks -or
    'Microsoft.NETCore.App' -notin $frameworkNames -or
    'Microsoft.WindowsDesktop.App' -notin $frameworkNames) {
    throw '发布产物必须依赖系统安装的 .NET 与 WindowsDesktop 运行时。'
}
foreach ($framework in $runtimeConfig.runtimeOptions.frameworks) {
    if ($framework.version -notmatch '^10\.0\.\d+$') {
        throw "非预期的运行时版本：$($framework.name) $($framework.version)"
    }
}

$forbiddenNames = @(
    'coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll', 'System.Windows.Forms.dll',
    'NAudio.dll', 'NAudio.Asio.dll', 'NAudio.Midi.dll',
    'NAudio.WinForms.dll', 'NAudio.WinMM.dll', 'VoiceTyper.Tests.dll'
)
$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$offenders = @($files | Where-Object {
    $_.Name -in $forbiddenNames -or $_.Name -like 'xunit.*.dll' -or
    $_.Extension -in @('.pdb', '.lib', '.onnx')
})
if ($offenders.Count -gt 0) {
    throw "发布目录含不应分发的文件：$($offenders.Name -join ', ')"
}

$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ('发布验收通过：{0} 个文件，{1:N2} MiB；不包含 .NET 运行时或语音模型。' -f
    $files.Count, ($totalBytes / 1MB))
