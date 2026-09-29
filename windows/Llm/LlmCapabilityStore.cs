using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VoiceTyper.Support;

namespace VoiceTyper.Llm;

/// <summary>
/// 记录"某个 LLM 接口是否拒绝 <c>thinking</c> 字段"，避免每次校对都白挨一次 400 再重发。
/// 对应 macOS 的 <c>LLMCapabilityStoring</c>。
/// </summary>
internal interface ILlmCapabilityStore
{
    bool IsThinkingParameterUnsupported(string fingerprint);
    void SetThinkingParameterUnsupported(bool unsupported, string fingerprint);
}

internal static class LlmCapabilityFingerprint
{
    /// <summary>
    /// 接口指纹：<c>SHA-256(请求地址 + "\n" + 模型名)</c> 的前 16 位十六进制。
    /// <b>不含 API Key</b>：换 Key 不改变接口能力，也避免把密钥的任何派生物写进磁盘。
    /// </summary>
    public static string Make(Uri chatUrl, string model)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{chatUrl.AbsoluteUri}\n{model}"));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// 文件版实现，落在 <c>%LOCALAPPDATA%\VoiceTyper\llm_capabilities.txt</c>：每行一个指纹。
/// 这是应用状态，不是用户配置，所以不进 <c>config.yaml</c>（也不随域漫游）；控制器每次保存配置都会
/// 重建 <see cref="LlmCorrector"/>，因此缓存不能只放在实例里。
/// </summary>
internal sealed class FileLlmCapabilityStore : ILlmCapabilityStore
{
    public static FileLlmCapabilityStore Shared { get; } = new(
        Path.Combine(AppConstants.LocalDataDirectory, "llm_capabilities.txt"));

    /// <summary>最多保留的指纹数，超出丢最旧的。</summary>
    private const int MaxEntries = 32;

    private readonly string _path;
    private readonly object _lock = new();

    public FileLlmCapabilityStore(string path) => _path = path;

    public bool IsThinkingParameterUnsupported(string fingerprint)
    {
        lock (_lock) return Read().Contains(fingerprint);
    }

    public void SetThinkingParameterUnsupported(bool unsupported, string fingerprint)
    {
        lock (_lock)
        {
            var entries = Read().Where(e => e != fingerprint).ToList();
            if (unsupported)
            {
                entries.Add(fingerprint);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllLines(_path, entries);
            }
            catch (Exception ex)
            {
                // 缓存写不进去只意味着下次多挨一次 400，不是故障。
                AppLog.Debug("llm", $"写入 LLM 能力缓存失败（忽略）: {ex.Message}");
            }
        }
    }

    private List<string> Read()
    {
        try
        {
            return File.Exists(_path)
                ? File.ReadAllLines(_path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>测试与不需要持久化的场景用。</summary>
internal sealed class InMemoryLlmCapabilityStore : ILlmCapabilityStore
{
    private readonly HashSet<string> _unsupported = new();

    public bool IsThinkingParameterUnsupported(string fingerprint)
    {
        lock (_unsupported) return _unsupported.Contains(fingerprint);
    }

    public void SetThinkingParameterUnsupported(bool unsupported, string fingerprint)
    {
        lock (_unsupported)
        {
            if (unsupported) _unsupported.Add(fingerprint);
            else _unsupported.Remove(fingerprint);
        }
    }
}
