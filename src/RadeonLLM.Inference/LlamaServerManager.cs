using System.Collections.Concurrent;
using System.Diagnostics;
using RadeonLLM.Core;

namespace RadeonLLM.Inference;

public sealed record ServerFailure(FriendlyError Error, bool Repeated);

/// <summary>
/// Owns the llama-server.exe child process: start, stop, restart, capture output,
/// detect crashes, verify health and recover automatically (spec sections 18 and 35).
/// </summary>
public sealed class LlamaServerManager : IServerStatus, IDisposable
{
    const int MaxCrashesInWindow = 3;
    static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);

    readonly LogChannel _runtimeLog, _inferenceLog;
    readonly JobObject _job = new();
    readonly ConcurrentQueue<string> _recentErrors = new();
    readonly List<DateTime> _crashTimes = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    Process? _proc;
    string? _exe;
    ServerConfig? _config;
    bool _stopRequested;
    CancellationTokenSource? _startCts;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public int CrashCount { get; private set; }
    public int GpuErrorCount { get; private set; }
    public ServerConfig? Config => _config;
    public int RestartAttempts { get; private set; }

    public event Action<ServerState>? StateChanged;
    /// <summary>Raised on any unexpected exit. Repeated=true means auto-recovery gave up (offer Safe Mode).</summary>
    public event Action<ServerFailure>? Failed;
    /// <summary>Raised after the server became healthy (initial start or recovery).</summary>
    public event Action? Ready;

    public LlamaServerManager(LogChannel runtimeLog, LogChannel inferenceLog)
    {
        _runtimeLog = runtimeLog; _inferenceLog = inferenceLog;
    }

    public string RecentOutput => string.Join(Environment.NewLine, _recentErrors);

    void SetState(ServerState s)
    {
        if (State == s) return;
        State = s;
        _inferenceLog.Info($"Server state: {s}");
        StateChanged?.Invoke(s);
    }

    /// <summary>Starts the server and waits until /v1/models answers. Throws on failed startup.</summary>
    public async Task StartAsync(string serverExe, ServerConfig config, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_proc is { HasExited: false }) await StopCoreAsync();
            _exe = serverExe; _config = config; _stopRequested = false;
            RestartAttempts = 0;
            await LaunchAsync(ct);
        }
        finally { _gate.Release(); }
    }

    async Task LaunchAsync(CancellationToken ct)
    {
        KillOrphans();
        SetState(ServerState.Starting);
        _recentErrors.Clear();
        var exe = _exe!; var cfg = _config!;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var a in cfg.ToArgs()) psi.ArgumentList.Add(a);
        _inferenceLog.Info($"Launching: {Path.GetFileName(exe)} {cfg.Describe()}");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => OnExited(p);
        if (!p.Start())
        {
            SetState(ServerState.Failed);
            throw new InvalidOperationException("llama-server failed to start.");
        }
        _job.Add(p);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        _proc = p;

        _startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _startCts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await WaitHealthyAsync(p, cfg.Port, cfg.ApiKey, _startCts.Token);
        }
        catch (Exception ex)
        {
            if (!p.HasExited) try { p.Kill(true); } catch { }
            var raw = RecentOutput;
            SetState(ServerState.Failed);
            Failed?.Invoke(new ServerFailure(ErrorTranslator.Translate(raw.Length > 0 ? raw : ex.Message), false));
            throw new InvalidOperationException(ErrorTranslator.Translate(raw.Length > 0 ? raw : ex.Message).Title, ex);
        }
        SetState(ServerState.Running);
        Ready?.Invoke();
    }

    async Task WaitHealthyAsync(Process p, int port, string? apiKey, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (p.HasExited) throw new InvalidOperationException($"llama-server exited during startup (code {p.ExitCode}).");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (apiKey is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var r = await _http.SendAsync(req, ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct);
        }
    }

    void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _runtimeLog.Raw(line);
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("VK_", StringComparison.Ordinal)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _recentErrors.Enqueue(line);
            while (_recentErrors.Count > 40) _recentErrors.TryDequeue(out _);
            if (ErrorTranslator.IsGpuError(line)) GpuErrorCount++;
        }
    }

    void OnExited(Process p)
    {
        if (_stopRequested || p != _proc) return;
        _ = Task.Run(() => HandleCrashAsync(p));
    }

    async Task HandleCrashAsync(Process p)
    {
        // A failed startup is reported by LaunchAsync, not here.
        if (State == ServerState.Starting) return;

        CrashCount++;
        var raw = RecentOutput;
        _inferenceLog.Error($"llama-server exited unexpectedly (code {SafeExit(p)}). {raw.Split('\n').LastOrDefault()}");
        var now = DateTime.UtcNow;
        lock (_crashTimes)
        {
            _crashTimes.Add(now);
            _crashTimes.RemoveAll(t => now - t > CrashWindow);
        }
        bool repeated; lock (_crashTimes) repeated = _crashTimes.Count >= MaxCrashesInWindow;
        var friendly = ErrorTranslator.Translate(raw);

        if (repeated)
        {
            SetState(ServerState.Failed);
            Failed?.Invoke(new ServerFailure(friendly, true));
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_stopRequested) return;
            SetState(ServerState.Recovering);
            Failed?.Invoke(new ServerFailure(friendly, false));
            RestartAttempts++;
            await Task.Delay(1500);
            await LaunchAsync(CancellationToken.None);
        }
        catch (Exception ex) { _inferenceLog.Error("Automatic restart failed", ex); }
        finally { _gate.Release(); }
    }

    static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    async Task StopCoreAsync()
    {
        _stopRequested = true;
        _startCts?.Cancel();
        var p = _proc; _proc = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(new CancellationTokenSource(5000).Token); }
            }
            catch { }
            p.Dispose();
        }
        lock (_crashTimes) _crashTimes.Clear();
        SetState(ServerState.Stopped);
    }

    public async Task RestartAsync(ServerConfig? newConfig = null, CancellationToken ct = default)
    {
        var exe = _exe ?? throw new InvalidOperationException("Server was never started.");
        await StartAsync(exe, newConfig ?? _config!, ct);
    }

    /// <summary>Kill leftover llama-server processes from a previous crashed session.</summary>
    void KillOrphans()
    {
        if (_exe is null) return;
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase) && p.Id != _proc?.Id)
                { p.Kill(true); _inferenceLog.Warn($"Killed orphaned llama-server (pid {p.Id})."); }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        try { _proc?.Kill(true); } catch { }
        _job.Dispose();
        _http.Dispose();
    }
}
