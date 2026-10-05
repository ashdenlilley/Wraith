namespace Wraith.Core;

public sealed record GpuInfo(string Name, string Vendor, long VramBytes, string? DriverVersion)
{
    public double VramGb => VramBytes / 1073741824.0;
}

public sealed record SystemInfo(
    IReadOnlyList<GpuInfo> Gpus, string CpuName, int CpuThreads, long RamBytes, bool VulkanLoaderPresent)
{
    /// <summary>Best GPU: most dedicated VRAM (so a discrete card beats an iGPU), any vendor.</summary>
    public GpuInfo? PrimaryGpu => Gpus.OrderByDescending(g => g.VramBytes).ThenByDescending(g => g.Vendor is "AMD" or "NVIDIA").FirstOrDefault();
}

public sealed record TelemetrySample(
    DateTime Time, double? GpuUtil, long? VramUsedBytes, double? TempC, double? HotspotC,
    double? GpuClockMhz, double? MemClockMhz, double? PowerW, double? FanRpm,
    double? CpuUtil, double? RamUtil);

public sealed record TelemetrySummary(
    int Samples, double? AvgGpuUtil, long? PeakVramBytes, double? AvgPowerW, double? PeakTempC,
    double? PeakHotspotC, double? AvgGpuClockMhz, double? AvgMemClockMhz)
{
    public static readonly TelemetrySummary Empty = new(0, null, null, null, null, null, null, null);
}

/// <summary>Implemented by Hardware; consumed by Analytics/UI without a project dependency.</summary>
public interface ITelemetryProvider
{
    TelemetrySample? Latest { get; }
    /// <summary>Begin accumulating samples; call Summarize on the window at any time.</summary>
    ITelemetryWindow BeginWindow();
}

public interface ITelemetryWindow : IDisposable
{
    TelemetrySummary Summarize();
}

/// <summary>Observes proxied API traffic.</summary>
public interface IRequestObserver
{
    void OnRequest(RequestRecord record);
}

public sealed record RequestRecord(
    DateTime Timestamp, string? Model, string Endpoint, int PromptTokens, int CompletionTokens,
    double? TtftSeconds, double? PromptTps, double? GenerationTps, double DurationSeconds, int Status,
    double? PeakTps = null, double? MinTps = null);

public enum ServerState { Stopped, Starting, Running, Recovering, Failed }

public interface IServerStatus
{
    ServerState State { get; }
    int CrashCount { get; }
    int GpuErrorCount { get; }
}

/// <summary>Blocks external network use when Offline Mode is on. Localhost is always allowed.</summary>
public sealed class NetworkGate
{
    readonly SettingsStore _settings;
    public NetworkGate(SettingsStore s) => _settings = s;
    public void EnsureAllowed(Uri uri)
    {
        if (uri.IsLoopback) return;
        if (_settings.Current.OfflineMode)
            throw new InvalidOperationException("Offline Mode is on. Turn it off in Settings to download.");
    }
}

public static class Fmt
{
    public static string Bytes(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";
    public static string Opt(double? v, string unit, string f = "0") => v is null ? "n/a" : v.Value.ToString(f) + " " + unit;
}
