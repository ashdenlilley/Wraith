namespace Wraith.Core;

/// <summary>Data directory layout under %LOCALAPPDATA%\Wraith (override with WRAITH_HOME).</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Runtime => Path.Combine(Root, "runtime", "llama.cpp");
    public string Models => Path.Combine(Root, "models");
    public string Config => Path.Combine(Root, "config");
    public string Analytics => Path.Combine(Root, "analytics");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");
    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string ModelsFile => Path.Combine(Config, "models.json");
    public string SecretFile => Path.Combine(Config, "api-key.bin");
    public string Database => Path.Combine(Analytics, "sessions.db");

    public AppPaths(string? root = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Root = root
            ?? Environment.GetEnvironmentVariable("WRAITH_HOME")
            ?? Path.Combine(local, "Wraith");
        if (root is null && Environment.GetEnvironmentVariable("WRAITH_HOME") is null) MigrateLegacyFolder(local);
    }

    /// <summary>v0.1 stored data under the previous product-name folder in %LOCALAPPDATA%; move it once so models and history survive the rename.</summary>
    void MigrateLegacyFolder(string local)
    {
        var old = Path.Combine(local, "Radeon" + "LLM");
        try { if (Directory.Exists(old) && !Directory.Exists(Root)) Directory.Move(old, Root); }
        catch { /* leave the old folder; a fresh one is created */ }
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Runtime, Models, Config, Analytics, Logs, Cache })
            Directory.CreateDirectory(d);
    }
}
