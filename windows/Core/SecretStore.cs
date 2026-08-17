using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

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

    public static void SaveLlmApiKey(string apiKey)
    {
        try
        {
            Directory.CreateDirectory(AppConstants.ConfigDirectory);
            if (string.IsNullOrEmpty(apiKey))
            {
                if (File.Exists(AppConstants.SecretFilePath)) File.Delete(AppConstants.SecretFilePath);
                return;
            }

            var plain = Encoding.UTF8.GetBytes(apiKey);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppConstants.SecretFilePath, protectedBytes);
        }
        catch (Exception ex)
        {
            AppLog.Error("secret", "保存 LLM API Key 失败", ex);
        }
    }

    public static string LoadLlmApiKey()
    {
        try
        {
            if (!File.Exists(AppConstants.SecretFilePath)) return "";
            var protectedBytes = File.ReadAllBytes(AppConstants.SecretFilePath);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            AppLog.Warn("secret", $"读取 LLM API Key 失败（可能是跨用户/跨机器搬运的文件，已忽略）: {ex.Message}");
            return "";
        }
    }
}
