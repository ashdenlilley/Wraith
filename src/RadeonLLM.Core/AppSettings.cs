using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadeonLLM.Core;

public enum PerformanceProfile { Safe, Balanced, Maximum, Custom }

public sealed class AppSettings
{
    public PerformanceProfile Profile { get; set; } = PerformanceProfile.Balanced;
    /// <summary>Requested context in tokens. Used by Custom; other profiles derive it.</summary>
    public int ContextSize { get; set; } = 16384;
    /// <summary>-1 = automatic maximum.</summary>
    public int GpuLayers { get; set; } = -1;
    /// <summary>0 = automatic.</summary>
    public int BatchSize { get; set; }
    public bool FlashAttention { get; set; } = true;
    /// <summary>0 = automatic.</summary>
    public int Threads { get; set; }

    public bool ApiEnabled { get; set; } = true;
    public bool LanAccess { get; set; }
    public int Port { get; set; } = 8080;
    /// <summary>Loopback port used by the managed llama-server behind the API front door.</summary>
    public int InternalPort { get; set; } = 18080;
    /// <summary>Key is enforced on localhost only if this is true; always enforced when LanAccess.</summary>
    public bool RequireApiKeyOnLocalhost { get; set; }

    public bool OfflineMode { get; set; }
    public string? DefaultModelId { get; set; }
    public string? IgnoredRuntimeVersion { get; set; }

    [JsonIgnore] public string BindAddress => LanAccess ? "0.0.0.0" : "127.0.0.1";
    [JsonIgnore] public bool AuthRequired => LanAccess || RequireApiKeyOnLocalhost;
    [JsonIgnore] public string LocalEndpoint => $"http://127.0.0.1:{Port}/v1";
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    readonly AppPaths _paths;
    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), Json) ?? new();
        }
        catch { Current = new(); }
        return Current;
    }

    public void Save()
    {
        Directory.CreateDirectory(_paths.Config);
        var tmp = _paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, _paths.SettingsFile, true);
        Changed?.Invoke();
    }
}
