using System.IO;
using RadeonLLM.Analytics;
using RadeonLLM.Api;
using RadeonLLM.Core;
using RadeonLLM.Hardware;
using RadeonLLM.Inference;
using RadeonLLM.Models;
using RadeonLLM.Runtime;
using RadeonLLM.Security;

namespace RadeonLLM.App;

/// <summary>Composition root and orchestration for the whole app. The UI talks only to this.</summary>
public sealed class AppController : IDisposable
{
    public AppPaths Paths { get; } = new();
    public SettingsStore SettingsStore { get; }
    public Logs Logs { get; }
    public NetworkGate Gate { get; }
    public SystemInfo Hw { get; private set; }
    public HardwareMonitor Monitor { get; }
    public RuntimeManager Runtime { get; }
    public ModelLibrary Library { get; }
    public ModelDownloader Downloader { get; }
    public LlamaServerManager Server { get; }
    public AnalyticsStore Store { get; }
    public SessionTracker Tracker { get; }
    public ApiHost Api { get; }
    public ApiKeyStore Keys { get; }

    public AppSettings Settings => SettingsStore.Current;
    public ConfigPlan? LastPlan { get; private set; }
    public bool SafeMode { get; private set; }
    public RuntimeDevice? Device { get; private set; }

    int _internalPort;
    string _internalKey = "";

    static int FreePort()
    {
        var l = new global::System.Net.Sockets.TcpListener(global::System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((global::System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public event Action<ServerFailure>? ServerFailed;
    public event Action? StateChanged;

    public AppController()
    {
        Paths.EnsureCreated();
        SettingsStore = new SettingsStore(Paths);
        SettingsStore.Load();
        Logs = new Logs(Paths);
        Gate = new NetworkGate(SettingsStore);
        Hw = HardwareDetector.Detect();
        Monitor = new HardwareMonitor(Logs.App, Hw.PrimaryGpu?.Name);
        Runtime = new RuntimeManager(Paths, Logs.Runtime, Gate);
        Library = new ModelLibrary(Paths);
        Downloader = new ModelDownloader(Paths, Gate, Library, Logs.App);
        Server = new LlamaServerManager(Logs.Runtime, Logs.Inference);
        Store = new AnalyticsStore(Paths);
        Tracker = new SessionTracker(Store, Monitor);
        Keys = new ApiKeyStore(Paths);
        Api = new ApiHost(Logs.Api, Tracker);

        Server.StateChanged += _ => StateChanged?.Invoke();
        Server.Failed += f => { Logs.App.Error($"Server failure: {f.Error.Title}"); ServerFailed?.Invoke(f); };
        Logs.App.Info($"RadeonLLM started. GPU: {Hw.PrimaryGpu?.Name ?? "none"}");
    }

    public GpuInfo? Gpu => Hw.PrimaryGpu;
    public string GpuLabel => Gpu is { } g ? g.Name : "No GPU detected";
    public ModelEntry? SelectedModel => Library.Find(Settings.DefaultModelId) ?? Library.Models.FirstOrDefault();

    public void RefreshSystem() { Hw = HardwareDetector.Detect(); StateChanged?.Invoke(); }

    /// <summary>VRAM we can plan to use: total minus what other apps already hold.</summary>
    public long AvailableVram()
    {
        long total = Device is { TotalMiB: > 0 } d ? d.TotalMiB * 1048576L : Gpu?.VramBytes ?? 0;
        if (total == 0) return 0;
        long used = Server.State == ServerState.Running ? 0 : Monitor.Latest?.VramUsedBytes ?? 0;
        return Math.Max(total - used - (256L << 20), total / 4);
    }

    public ConfigPlan PlanFor(ModelEntry m) =>
        ConfigBuilder.Build(Settings, m, AvailableVram(), Hw.CpuThreads, SafeMode, Device?.Id);

    // ---------- setup ----------

    public async Task SetupRuntimeAsync(IProgress<(string, double)> progress, CancellationToken ct = default)
    {
        var rel = await Runtime.GetLatestAsync(ct);
        await Runtime.InstallAsync(rel, progress, ct);
        progress.Report(("Testing GPU...", 0.97));
        await ProbeDeviceAsync(ct);
        progress.Report(("Runtime ready.", 1));
        StateChanged?.Invoke();
    }

    static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task ProbeDeviceAsync(CancellationToken ct = default)
    {
        try
        {
            var devs = await Runtime.ListDevicesAsync(ct);
            // Pick the GPU RadeonLLM detected (iGPUs can report huge shared memory, so never choose by size alone).
            var vk = devs.Where(d => d.IsVulkan).ToList();
            var want = Gpu?.Name;
            Device = (want is null ? null : vk.FirstOrDefault(d => Norm(d.Name) == Norm(want) || Norm(d.Name).Contains(Norm(want)) || Norm(want).Contains(Norm(d.Name))))
                ?? vk.FirstOrDefault();
            Logs.App.Info(Device is null ? "Runtime reports no Vulkan device." : $"Vulkan device: {Device.Name} ({Device.TotalMiB} MiB)");
        }
        catch (Exception ex) { Logs.App.Error("Device probe failed", ex); }
    }

    // ---------- run ----------

    public async Task StartAsync(CancellationToken ct = default)
    {
        var exe = Runtime.ServerExe ?? throw new InvalidOperationException("Runtime is not installed. Run Setup first.");
        var model = SelectedModel ?? throw new InvalidOperationException("Import or download a model first.");
        if (!File.Exists(model.Path)) throw new FileNotFoundException("Model file is missing.", model.Path);

        // llama-server is only reachable from this app: random loopback port + per-run key.
        _internalPort = Settings.InternalPort > 0 ? Settings.InternalPort : FreePort();
        _internalKey = Convert.ToHexString(global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var plan = PlanFor(model);
        plan = plan with { Config = plan.Config with { Port = _internalPort, ApiKey = _internalKey } };
        LastPlan = plan;
        Logs.App.Info($"Plan: {plan.Config.Describe()} est {Fmt.Bytes(plan.Preflight.TotalBytes)} of {Fmt.Bytes(plan.Preflight.AvailableBytes)}{(plan.Note is null ? "" : " - " + plan.Note)}");

        await Server.StartAsync(exe, plan.Config, ct);
        await StartApiAsync();
        Tracker.Begin(model.Id, model.Quantization, plan.Config.Context, GpuLabel, Runtime.Version);
        StateChanged?.Invoke();
    }

    public async Task StartApiAsync()
    {
        if (!Settings.ApiEnabled) { await Api.StopAsync(); return; }
        string? key = Settings.AuthRequired ? Keys.GetOrCreate() : Keys.Get();
        await Api.StartAsync(new ApiOptions(Settings.BindAddress, Settings.Port, _internalPort, Settings.AuthRequired, key, _internalKey));
    }

    public async Task StopAsync()
    {
        Tracker.End();
        await Api.StopAsync();
        await Server.StopAsync();
        StateChanged?.Invoke();
    }

    public async Task EnterSafeModeAsync(CancellationToken ct = default)
    {
        SafeMode = true;
        await StopAsync();
        await StartAsync(ct);
    }

    public void ExitSafeMode() { SafeMode = false; StateChanged?.Invoke(); }

    /// <summary>Apply "Reduce Context" from the failure dialog: halve and persist as Custom.</summary>
    public void ReduceContext()
    {
        var cur = LastPlan?.Config.Context ?? Settings.ContextSize;
        Settings.Profile = PerformanceProfile.Custom;
        Settings.ContextSize = Math.Max(2048, cur / 2);
        SettingsStore.Save();
    }

    public void ReduceGpuOffload()
    {
        var layers = SelectedModel?.BlockCount ?? 32;
        var cur = LastPlan?.Config.GpuLayers is { } g && g < 999 ? g : layers;
        Settings.Profile = PerformanceProfile.Custom;
        Settings.GpuLayers = Math.Max(0, (int)(cur * 0.8));
        SettingsStore.Save();
    }

    public sealed record TuneResult(int Batch, int UBatch, double PromptTps, double GenTps);

    static readonly string LongPrompt = string.Join(' ', Enumerable.Repeat(
        "Throughput depends on how the prompt is split into batches that the GPU processes in parallel.", 110));

    /// <summary>
    /// Sweeps batch/ubatch by restarting the server with each pair and timing a long prompt.
    /// Talks to llama-server directly (not the public API) so session analytics stay clean.
    /// Saves the fastest pair as a Custom profile and restores the original server config.
    /// </summary>
    public async Task<(IReadOnlyList<TuneResult> Results, TuneResult Best)> TuneBatchAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        if (Server.State != ServerState.Running || LastPlan is not { } plan || Runtime.ServerExe is not { } exe)
            throw new InvalidOperationException("Start the server first.");
        var baseCfg = plan.Config;
        var results = new List<TuneResult>();
        var pairs = new (int b, int ub)[] { (512, 256), (512, 512), (1024, 512), (2048, 512), (2048, 1024), (4096, 1024) };
        var client = new LocalApiClient($"http://127.0.0.1:{_internalPort}/v1", _internalKey);
        try
        {
            int i = 0;
            foreach (var (b, ub) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"Testing batch {b} / ubatch {ub}  ({++i}/{pairs.Length})...");
                await Server.StartAsync(exe, baseCfg with { Batch = b, UBatch = ub }, ct);
                await client.CompleteAsync(baseCfg.Alias, "warm up", 8, ct);
                var runs = new List<CompletionTimings>();
                for (int k = 0; k < 2; k++) runs.Add(await client.CompleteAsync(baseCfg.Alias, LongPrompt, 64, ct));
                results.Add(new TuneResult(b, ub, runs.Average(r => r.PromptTps), runs.Average(r => r.GenerationTps)));
            }
        }
        finally
        {
            try { await Server.StartAsync(exe, baseCfg, CancellationToken.None); } catch (Exception ex) { Logs.App.Error("Restoring server after tune failed", ex); }
        }
        var best = results.OrderByDescending(r => r.PromptTps).First();
        Settings.Profile = PerformanceProfile.Custom;
        Settings.ContextSize = baseCfg.Context;
        Settings.FlashAttention = baseCfg.FlashAttention;
        Settings.KvCacheType = baseCfg.KvCacheType;
        Settings.BatchSize = best.Batch; Settings.UBatchSize = best.UBatch;
        SettingsStore.Save();
        Logs.App.Info($"Batch tune: best {best.Batch}/{best.UBatch} at {best.PromptTps:0} prompt tok/s");
        return (results, best);
    }

    public LocalApiClient CreateApiClient() =>
        new(Settings.LocalEndpoint, Settings.AuthRequired ? Keys.GetOrCreate() : Keys.Get());

    public string? LanAddress()
    {
        try
        {
            return global::System.Net.Dns.GetHostEntry(global::System.Net.Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork
                    && !global::System.Net.IPAddress.IsLoopback(a))?.ToString();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { Tracker.End(); } catch { }
        Api.DisposeAsync().AsTask().Wait(3000);
        Server.Dispose();
        Monitor.Dispose();
        Store.Dispose();
    }
}
