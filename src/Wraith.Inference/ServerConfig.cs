using Wraith.Core;
using Wraith.Models;

namespace Wraith.Inference;

public sealed record ServerConfig(
    string ModelPath, string Alias, int Context, int GpuLayers, int Batch, int UBatch,
    bool FlashAttention, int Threads, string KvCacheType, int Port, string? DeviceId = null, string? ApiKey = null)
{
    public string Describe() =>
        $"ctx={Context} ngl={(GpuLayers >= 999 ? "max" : GpuLayers)} batch={Batch} fa={(FlashAttention ? "on" : "off")} kv={KvCacheType}";

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>
        {
            "-m", ModelPath, "--host", "127.0.0.1", "--port", Port.ToString(), "--alias", Alias,
            "-c", Context.ToString(), "-ngl", GpuLayers.ToString(), "-b", Batch.ToString(), "-ub", UBatch.ToString(),
            "--flash-attn", FlashAttention ? "on" : "off", "--jinja", "--no-webui"
        };
        if (DeviceId is not null) { a.Add("--device"); a.Add(DeviceId); a.Add("-sm"); a.Add("none"); }
        if (ApiKey is not null) { a.Add("--api-key"); a.Add(ApiKey); }
        if (Threads > 0) { a.Add("-t"); a.Add(Threads.ToString()); }
        if (KvCacheType != "f16") { a.Add("-ctk"); a.Add(KvCacheType); a.Add("-ctv"); a.Add(KvCacheType); }
        return a;
    }
}

public sealed record ConfigPlan(ServerConfig Config, PreflightResult Preflight, bool Adjusted, string? Note);

/// <summary>Turns a performance profile + model + GPU into concrete llama-server settings.</summary>
public static class ConfigBuilder
{
    public const int AutoLayers = 999;
    public static readonly int[] ContextPresets = { 4096, 8192, 16384, 32768, 65536, 131072 };

    /// <summary>Largest preset the model supports and the GPU (or RAM) holds with headroom. Capped at 32K: beyond that local prompt processing gets slow.</summary>
    public static int RecommendContext(ModelEntry m, long availableBytes, bool cpuOnly = false)
    {
        int cap = (int)Math.Min(m.ContextLength ?? 32768, cpuOnly ? 8192 : 32768);
        int best = 4096;
        foreach (var c in ContextPresets)
        {
            if (c > cap) break;
            if (VramPreflight.Estimate(m, c, (long)(availableBytes * 0.9), 1.0, 512).Safe) best = c;
        }
        return Math.Min(best, Math.Max(512, cap));
    }

    public static ConfigPlan Build(AppSettings s, ModelEntry m, long availableVramBytes, int cpuThreads, bool safeMode = false, string? deviceId = null, bool cpuOnly = false)
    {
        var profile = safeMode ? PerformanceProfile.Safe : s.Profile;
        int modelMax = (int)Math.Min(m.ContextLength ?? 32768, 131072);

        int ctx; bool fa; int batch = 2048; int ubatch = 512; double layerFraction = 1.0; string kv = "f16";
        switch (profile)
        {
            case PerformanceProfile.Safe:
                ctx = 4096; fa = false; batch = 512; layerFraction = safeMode ? 0.8 : 1.0; break;
            case PerformanceProfile.Maximum:
                ctx = 32768; fa = true; kv = "q8_0"; break;
            case PerformanceProfile.Custom:
                ctx = s.ContextSize; fa = s.FlashAttention; batch = s.BatchSize > 0 ? s.BatchSize : 2048; if (s.UBatchSize > 0) ubatch = s.UBatchSize;
                kv = fa && s.KvCacheType == "q8_0" ? "q8_0" : "f16"; break;
            default:
                ctx = 16384; fa = true; break;
        }
        if (profile != PerformanceProfile.Custom && !safeMode && s.ContextOverride >= 512) ctx = s.ContextOverride;
        ctx = Math.Clamp(ctx, 512, modelMax);
        if (profile == PerformanceProfile.Custom && s.GpuLayers >= 0 && m.BlockCount is { } bl && bl > 0)
            layerFraction = Math.Min(1.0, (double)s.GpuLayers / bl);
        ubatch = Math.Min(ubatch, batch);

        double kvBytes = kv == "q8_0" ? 1.0625 : 2.0;
        if (cpuOnly)
        {
            // No GPU: weights and KV live in system RAM, so keep batches small and context modest.
            ctx = Math.Min(ctx, 8192); batch = Math.Min(batch, 512); ubatch = Math.Min(ubatch, 512);
            if (profile == PerformanceProfile.Maximum) ctx = Math.Min(modelMax, 16384);
        }
        var pre = VramPreflight.Estimate(m, ctx, availableVramBytes, layerFraction, batch, kvBytes);
        bool adjusted = false; string? note = null;

        // Maximum and Custom are explicit user intent: warn only. Safe and Balanced auto-fit (spec section 16).
        if (!pre.Safe && profile is PerformanceProfile.Safe or PerformanceProfile.Balanced)
        {
            if (pre.RecommendedContext is { } rc)
            {
                note = $"Context {ctx / 1024}K to {rc / 1024}K to fit VRAM.";
                ctx = rc; adjusted = true;
            }
            if (pre.RecommendedGpuLayers is { } rl && m.BlockCount is { } total && total > 0)
            {
                layerFraction = (double)rl / total;
                note += $" GPU layers reduced to {rl}/{total}.";
            }
            if (!pre.Safe && batch > 512) { batch = 512; adjusted = true; }
            pre = VramPreflight.Estimate(m, ctx, availableVramBytes, layerFraction, batch, kvBytes);
        }

        int ngl = cpuOnly ? 0 : AutoLayers;
        if (!cpuOnly && layerFraction < 1.0 && m.BlockCount is { } bc && bc > 0) ngl = (int)Math.Floor(bc * layerFraction);
        if (!cpuOnly && profile == PerformanceProfile.Custom && s.GpuLayers >= 0) ngl = s.GpuLayers;

        var threads = s.Threads > 0 ? s.Threads : Math.Max(1, cpuThreads / 2);
        var cfg = new ServerConfig(m.Path, m.Id, ctx, ngl, batch, Math.Min(ubatch, batch), fa, threads, kv, s.InternalPort, deviceId);
        return new(cfg, pre, adjusted, note?.Trim());
    }
}
