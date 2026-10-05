using Wraith.Core;

namespace Wraith.Analytics;

public static class Stats
{
    public static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return null;
        var s = sorted.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}

public sealed record SessionStats(
    TimeSpan Duration, int Requests, long PromptTokens, long GeneratedTokens, double? PromptTps, double? GenerationTps,
    double? PeakTps, double? MinTps, double? MedianTtft, double? LastGenerationTps);

/// <summary>
/// One session = one running server instance. Observes every proxied API request
/// (including the built-in chat, which uses the same API) and persists metadata only.
/// </summary>
public sealed class SessionTracker : IRequestObserver
{
    readonly AnalyticsStore _store;
    readonly ITelemetryProvider? _telemetry;
    readonly object _lock = new();

    long? _sessionId;
    ITelemetryWindow? _window;
    DateTime _start;
    int _requests;
    long _promptTokens, _genTokens;
    double _promptSeconds, _genSeconds;
    double? _peak, _min, _last;
    readonly List<double> _ttfts = new();

    public event Action? Updated;

    public SessionTracker(AnalyticsStore store, ITelemetryProvider? telemetry)
    {
        _store = store; _telemetry = telemetry;
    }

    public bool Active => _sessionId is not null;

    public void Begin(string model, string? quant, int ctx, string gpu, string? runtime)
    {
        lock (_lock)
        {
            End();
            _sessionId = _store.InsertSession(model, quant, ctx, gpu, runtime);
            _window = _telemetry?.BeginWindow();
            _start = DateTime.UtcNow;
            _requests = 0; _promptTokens = _genTokens = 0; _promptSeconds = _genSeconds = 0;
            _peak = _min = _last = null; _ttfts.Clear();
        }
        Updated?.Invoke();
    }

    public void End()
    {
        lock (_lock)
        {
            if (_sessionId is { } id) _store.UpdateSession(id, Snapshot(), _window?.Summarize() ?? TelemetrySummary.Empty);
            _window?.Dispose(); _window = null; _sessionId = null;
        }
    }

    public void OnRequest(RequestRecord r)
    {
        lock (_lock)
        {
            // Requests outside a session are still counted in the DB so API analytics stay complete.
            _store.InsertRequest(_sessionId ?? 0, r);
            if (_sessionId is null) return;
            _requests++;
            _promptTokens += r.PromptTokens; _genTokens += r.CompletionTokens;
            if (r.PromptTps is > 0) _promptSeconds += r.PromptTokens / r.PromptTps.Value;
            if (r.GenerationTps is > 0) { _genSeconds += r.CompletionTokens / r.GenerationTps.Value; _last = r.GenerationTps; }
            if (r.PeakTps is { } pk) _peak = Math.Max(_peak ?? pk, pk);
            if (r.MinTps is { } mn) _min = Math.Min(_min ?? mn, mn);
            if (r.TtftSeconds is { } t) _ttfts.Add(t);
            _store.UpdateSession(_sessionId.Value, Snapshot(), _window?.Summarize() ?? TelemetrySummary.Empty);
        }
        Updated?.Invoke();
    }

    public SessionStats Snapshot()
    {
        lock (_lock)
            return new(_sessionId is null ? TimeSpan.Zero : DateTime.UtcNow - _start, _requests, _promptTokens, _genTokens,
                _promptSeconds > 0 ? _promptTokens / _promptSeconds : null,
                _genSeconds > 0 ? _genTokens / _genSeconds : null,
                _peak, _min, Stats.Median(_ttfts), _last);
    }
}
