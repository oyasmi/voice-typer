using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 读取 API Key 的结果分类。区分"从未保存过"（<see cref="NotSaved"/>，正常状态，不是错误）
/// 与真正的读取失败（<see cref="Failed"/>，例如 DPAPI 解密失败——常见于配置目录被跨用户/
/// 跨机器搬运）。此前 <c>LoadLlmApiKey()</c> 把两者都压成同一个空字符串返回值，导致
/// "智能纠错开着但一直 401"在界面上完全不可见，没有任何信号指向密钥本身（R4-06）。
/// </summary>
internal enum SecretReadStatus { Ok, NotSaved, Failed }

/// <summary>
/// LLM API Key 存储：DPAPI（<see cref="ProtectedData"/>），当前用户范围加密后落盘为
/// <c>%APPDATA%\VoiceTyper\llm_api_key.dat</c>。不落 YAML 明文——配置文件权限是 0644，
/// 长期有效的 API Key 明文写进去不合适。
///
/// 选 DPAPI 而不是 Windows 凭据管理器（Credential Manager）：后者需要约 120 行
/// <c>CredWrite</c>/<c>CredRead</c> P/Invoke 互操作，而两者底层同样是 DPAPI 保护，
/// 安全性无实质差别。DPAPI 文件方案代码量约 1/3。
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.voicetyper.app.llm_api_key");

    /// <summary>传入空字符串会删除已保存的密钥。返回是否真正落盘成功——调用方不应在
    /// 失败时仍告诉用户"设置已保存并生效"（R4-06）。</summary>
    public static bool SaveLlmApiKey(string apiKey)
    {
        try
        {
            Directory.CreateDirectory(AppConstants.ConfigDirectory);
            if (string.IsNullOrEmpty(apiKey))
            {
                if (File.Exists(AppConstants.SecretFilePath)) File.Delete(AppConstants.SecretFilePath);
                return true;
            }

            var plain = Encoding.UTF8.GetBytes(apiKey);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppConstants.SecretFilePath, protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("secret", "保存 LLM API Key 失败", ex);
            return false;
        }
    }

    /// <summary>区分"从未保存"（<see cref="SecretReadStatus.NotSaved"/>）与"读取失败"
    /// （<see cref="SecretReadStatus.Failed"/>，例如密钥文件损坏或跨机器搬运导致 DPAPI 解密失败）。
    /// 需要向用户展示真实原因的调用方（设置页）应使用本方法而非 <see cref="LoadLlmApiKey"/>。</summary>
    public static (SecretReadStatus Status, string ApiKey) LoadLlmApiKeyResult()
    {
        try
        {
            if (!File.Exists(AppConstants.SecretFilePath)) return (SecretReadStatus.NotSaved, "");
            var protectedBytes = File.ReadAllBytes(AppConstants.SecretFilePath);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return (SecretReadStatus.Ok, Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            AppLog.Warn("secret", $"读取 LLM API Key 失败（可能是跨用户/跨机器搬运的文件损坏）: {ex.Message}");
            return (SecretReadStatus.Failed, "");
        }
    }

    /// <summary>便利入口：不区分"从未保存"与"读取失败"，两者都返回空字符串。
    /// 需要区分二者的调用方应改用 <see cref="LoadLlmApiKeyResult"/>。</summary>
    public static string LoadLlmApiKey() => LoadLlmApiKeyResult().ApiKey;
}
