namespace RadeonLLM.Core;

/// <summary>Data directory layout under %LOCALAPPDATA%\RadeonLLM (override with RADEONLLM_HOME).</summary>
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
        Root = root
            ?? Environment.GetEnvironmentVariable("RADEONLLM_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonLLM");
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Runtime, Models, Config, Analytics, Logs, Cache })
            Directory.CreateDirectory(d);
    }
}
