using System.IO;
using Wraith.Analytics;
using Wraith.Api;
using Wraith.Core;
using Wraith.Hardware;
using Wraith.Inference;
using Wraith.Models;
using Wraith.Runtime;
using Wraith.Security;

namespace Wraith.App;

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
        Logs.App.Info($"Wraith started. GPU: {Hw.PrimaryGpu?.Name ?? "none"}");
    }

    public GpuInfo? Gpu => Hw.PrimaryGpu;
    public string GpuLabel => Gpu is { } g ? g.Name : "No GPU detected";
    public ModelEntry? SelectedModel => Library.Find(Settings.DefaultModelId) ?? Library.Models.FirstOrDefault();

    public void RefreshSystem() { Hw = HardwareDetector.Detect(); StateChanged?.Invoke(); }

    public bool IsCpuBackend => Runtime.ActiveBackend == RuntimeBackend.Cpu;

    public string BackendLabel => Runtime.ActiveBackend is { } b ? BackendSelector.Label(b) : "not installed";

    /// <summary>Memory we can plan to use: free VRAM on a GPU backend, free system RAM on CPU.</summary>
    public long AvailableVram()
    {
        if (IsCpuBackend)
        {
            var (total, usedFraction) = HardwareDetector.Ram();
            return (long)(total * (1 - usedFraction) * 0.85);
        }
        long vram = Device is { TotalMiB: > 0 } d ? d.TotalMiB * 1048576L : Gpu?.VramBytes ?? 0;
        if (vram == 0) return 0;
        long used = Server.State == ServerState.Running ? 0 : Monitor.Latest?.VramUsedBytes ?? 0;
        return Math.Max(vram - used - (256L << 20), vram / 4);
    }

    public ConfigPlan PlanFor(ModelEntry m) =>
        ConfigBuilder.Build(Settings, m, AvailableVram(), Hw.CpuThreads, SafeMode, IsCpuBackend ? null : Device?.Id, IsCpuBackend);

    // ---------- setup ----------

    /// <summary>
    /// Installs (or switches to) a backend. Auto walks the hardware-appropriate candidates and keeps the first
    /// whose --list-devices shows a working device; an explicit choice is verified and rolled back on failure.
    /// </summary>
    public async Task<RuntimeBackend> SetupRuntimeAsync(IProgress<(string, double)> progress, CancellationToken ct = default)
    {
        var requested = Settings.Backend;
        var candidates = BackendSelector.Candidates(requested, Hw);
        var priorVersion = Runtime.Version; var priorBackend = Runtime.ActiveBackend;
        var cuda = BackendSelector.CudaVersionFor(BackendSelector.NvidiaDriverVersion(Gpu?.DriverVersion));
        Exception? last = null;

        foreach (var backend in candidates)
        {
            try
            {
                progress.Report(($"Preparing {BackendSelector.Label(backend)} runtime...", 0.02));
                var latest = await Runtime.GetLatestAsync(backend, cuda, ct);
                if (!(Runtime.HasBuild(latest.Version, backend) && Runtime.Activate(latest.Version, backend)))
                {
                    // Prefer a build we already have over forcing a re-download on a backend switch.
                    var existing = Runtime.InstalledBuilds(backend).FirstOrDefault();
                    if (existing is not null && requested != RuntimeBackend.Auto && Runtime.Activate(existing, backend)) { }
                    else await Runtime.InstallAsync(latest, progress, ct);
                }
                progress.Report(("Testing GPU...", 0.97));
                var ok = await ProbeDeviceAsync(ct);
                if (BackendSelector.Works(backend, ok)) { progress.Report(("Runtime ready.", 1)); StateChanged?.Invoke(); return backend; }
                last = new InvalidOperationException($"The {BackendSelector.Label(backend)} runtime started but found no compatible device.");
                Logs.App.Warn(last.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Logs.App.Error($"{BackendSelector.Label(backend)} setup failed", ex);
            }
        }

        // Nothing worked: restore what was active before so the app keeps functioning.
        if (priorVersion is not null && priorBackend is { } pb) { Runtime.Activate(priorVersion, pb); await ProbeDeviceAsync(ct); }
        StateChanged?.Invoke();
        throw new InvalidOperationException(last?.Message ?? "Runtime setup failed.");
    }

    /// <summary>Lists devices for the active backend and picks the one matching the detected GPU.</summary>
    public async Task<IReadOnlyList<RuntimeDevice>> ProbeDeviceAsync(CancellationToken ct = default)
    {
        Device = null;
        if (!Runtime.IsInstalled) return [];
        try
        {
            var devs = await Runtime.ListDevicesAsync(ct);
            var gpus = devs.Where(d => d.Kind != DeviceKind.Other).ToList();
            var want = Gpu?.Name;
            // Pick by name, never by size alone: iGPUs can report huge shared memory.
            Device = (want is null ? null : gpus.FirstOrDefault(d => Norm(d.Name) == Norm(want) || Norm(d.Name).Contains(Norm(want)) || Norm(want).Contains(Norm(d.Name))))
                ?? gpus.FirstOrDefault();
            if (IsCpuBackend) Device = null;
            Logs.App.Info(Device is null ? $"{BackendLabel}: no GPU device (CPU inference)." : $"{BackendLabel} device: {Device.Id} {Device.Name} ({Device.TotalMiB} MiB)");
            return devs;
        }
        catch (Exception ex) { Logs.App.Error("Device probe failed", ex); return []; }
    }

    static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
