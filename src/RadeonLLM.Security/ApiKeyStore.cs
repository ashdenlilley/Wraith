using System.Security.Cryptography;
using RadeonLLM.Core;

namespace RadeonLLM.Security;

/// <summary>API key protected with Windows DPAPI (current user scope), never stored as plaintext.</summary>
public sealed class ApiKeyStore
{
    readonly AppPaths _paths;
    public ApiKeyStore(AppPaths paths) => _paths = paths;

    public string? Get()
    {
        try
        {
            if (!File.Exists(_paths.SecretFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_paths.SecretFile), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public void Set(string key)
    {
        Directory.CreateDirectory(_paths.Config);
        var enc = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_paths.SecretFile, enc);
    }

    public string Generate()
    {
        var key = "rllm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Set(key);
        return key;
    }

    public string GetOrCreate() => Get() ?? Generate();
}
