using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VoiceTyper.Support;
using YamlDotNet.RepresentationModel;

namespace VoiceTyper.Asr;

/// <summary>一份可用的 SenseVoice 模型目录：onnx 权重 + 词表 + CMVN 统计 + 前端参数。</summary>
internal sealed class ModelBundle
{
    public required string ModelDir { get; init; }
    public required string OnnxFilePath { get; init; }
    public required string TokensPath { get; init; }
    public required string CmvnPath { get; init; }
    public required bool Quantized { get; init; }
    /// <summary>从 config.yaml 的 frontend_conf 解析，解析失败则用 FbankOptions 的默认值。</summary>
    public required FbankOptions FbankOptions { get; init; }
    public required int LfrM { get; init; }
    public required int LfrN { get; init; }
}

/// <summary>
/// 按优先级定位模型目录，第一个通过校验的立即返回：
/// 1. 用户在设置里显式指定的 <c>asr.model_dir</c>
/// 2. App 首启下载的落点 <c>%LOCALAPPDATA%\VoiceTyper\models\sensevoice-small\</c>
/// 3. Python 服务端已下载过的 ModelScope 缓存
///    <c>%USERPROFILE%\.cache\modelscope\hub\models\iic\SenseVoiceSmall-onnx\</c>
///    （跑过 client-server/server/ 的机器零下载；同时探测旧版布局 <c>hub\iic\SenseVoiceSmall-onnx\</c>）
///
/// 都没有命中时返回 null，调用方据此进入 ModelMissing 状态触发首启下载引导。
/// </summary>
internal static class ModelLocator
{
    public static string DownloadDestination => AppConstants.ModelDownloadDestination;

    private static string LegacyModelScopeCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "modelscope", "hub", "iic", "SenseVoiceSmall-onnx"
    );

    public static ModelBundle? Locate(string explicitDir)
    {
        var trimmed = (explicitDir ?? "").Trim();
        if (trimmed.Length > 0)
        {
            var explicitBundle = Validate(trimmed);
            if (explicitBundle is not null) return explicitBundle;
        }

        var downloaded = Validate(DownloadDestination);
        if (downloaded is not null) return downloaded;

        var cached = Validate(AppConstants.ModelScopeCacheDirectory);
        if (cached is not null) return cached;

        var legacyCached = Validate(LegacyModelScopeCacheDirectory);
        if (legacyCached is not null) return legacyCached;

        return null;
    }

    private static ModelBundle? Validate(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var quantPath = Path.Combine(dir, "model_quant.onnx");
        var fullPath = Path.Combine(dir, "model.onnx");
        string onnxPath;
        bool quantized;
        if (File.Exists(quantPath))
        {
            onnxPath = quantPath;
            quantized = true;
        }
        else if (File.Exists(fullPath))
        {
            onnxPath = fullPath;
            quantized = false;
        }
        else
        {
            return null;
        }

        var cmvnPath = Path.Combine(dir, "am.mvn");
        var tokensJsonPath = Path.Combine(dir, "tokens.json");
        var tokensTxtPath = Path.Combine(dir, "tokens.txt");
        string tokensPath;
        if (File.Exists(tokensJsonPath)) tokensPath = tokensJsonPath;
        else if (File.Exists(tokensTxtPath)) tokensPath = tokensTxtPath;
        else return null;

        if (!File.Exists(cmvnPath)) return null;

        var (fbankOptions, lfrM, lfrN) = ParseFrontendConf(Path.Combine(dir, "config.yaml"));
        return new ModelBundle
        {
            ModelDir = dir,
            OnnxFilePath = onnxPath,
            TokensPath = tokensPath,
            CmvnPath = cmvnPath,
            Quantized = quantized,
            FbankOptions = fbankOptions,
            LfrM = lfrM,
            LfrN = lfrN,
        };
    }

    /// <summary>
    /// 读 config.yaml 的 frontend_conf；缺失或解析失败时静默回落到验证过的默认值
    /// （fs=16000, n_mels=80, lfr_m=7, lfr_n=6, hamming window）。
    /// </summary>
    private static (FbankOptions, int lfrM, int lfrN) ParseFrontendConf(string configPath)
    {
        int lfrM = 7, lfrN = 6;
        double sampleRate = 16000, frameLengthMs = 25, frameShiftMs = 10;
        int numMelBins = 80;

        if (!File.Exists(configPath))
        {
            return (new FbankOptions
            {
                SampleRate = sampleRate,
                FrameLengthMs = frameLengthMs,
                FrameShiftMs = frameShiftMs,
                NumMelBins = numMelBins,
            }, lfrM, lfrN);
        }

        try
        {
            using var reader = new StreamReader(configPath);
            var yamlStream = new YamlStream();
            yamlStream.Load(reader);
            if (yamlStream.Documents.Count > 0 && yamlStream.Documents[0].RootNode is YamlMappingNode root)
            {
                if (TryGetMapping(root, "frontend_conf", out var fc))
                {
                    sampleRate = TryGetDouble(fc, "fs", sampleRate);
                    frameLengthMs = TryGetDouble(fc, "frame_length", frameLengthMs);
                    frameShiftMs = TryGetDouble(fc, "frame_shift", frameShiftMs);
                    numMelBins = (int)TryGetDouble(fc, "n_mels", numMelBins);
                    lfrM = (int)TryGetDouble(fc, "lfr_m", lfrM);
                    lfrN = (int)TryGetDouble(fc, "lfr_n", lfrN);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("model", $"解析 config.yaml 失败，使用默认前端参数: {ex.Message}");
        }

        return (new FbankOptions
        {
            SampleRate = sampleRate,
            FrameLengthMs = frameLengthMs,
            FrameShiftMs = frameShiftMs,
            NumMelBins = numMelBins,
        }, lfrM, lfrN);
    }

    private static bool TryGetMapping(YamlMappingNode node, string key, out YamlMappingNode result)
    {
        result = null!;
        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode s && s.Value == key && entry.Value is YamlMappingNode m)
            {
                result = m;
                return true;
            }
        }
        return false;
    }

    private static double TryGetDouble(YamlMappingNode node, string key, double fallback)
    {
        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode s && s.Value == key && entry.Value is YamlScalarNode v
                && double.TryParse(v.Value, out var parsed))
            {
                return parsed;
            }
        }
        return fallback;
    }

    public static IReadOnlyList<string> LoadTokens(string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(path);
            var tokens = JsonSerializer.Deserialize<List<string>>(json);
            if (tokens is null)
            {
                throw new InvalidDataException($"tokens.json 格式不是字符串数组: {path}");
            }
            return tokens;
        }

        // tokens.txt：每行 "piece\tscore"，piece 本身可能含空格，只从右侧切掉分数列。
        var result = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var tabIndex = line.LastIndexOf('\t');
            result.Add(tabIndex >= 0 ? line[..tabIndex] : line);
        }
        return result;
    }
}
