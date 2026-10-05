using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wraith.Core;

namespace Wraith.Analytics;

public sealed record BenchmarkResult(
    long Id, DateTime Timestamp, string Model, string? Quantization, int ContextSize, string Gpu, string? RuntimeVersion,
    int Iterations, int PromptTokens, int GeneratedTokens,
    double GenAvg, double GenMedian, double GenMin, double GenMax, double PromptTps, double Ttft,
    TelemetrySummary Hardware, double? PerfPerWatt, string? Label);

public sealed record StabilityResult(
    DateTime Timestamp, string Model, TimeSpan Duration, long Tokens, double AvgTps, double MinTps,
    int Errors, int Crashes, int GpuErrors, TelemetrySummary Hardware)
{
    public bool Passed => Errors == 0 && Crashes == 0 && GpuErrors == 0;
}

public sealed record CompletionTimings(int PromptTokens, int GeneratedTokens, double PromptTps, double GenerationTps, double TtftSeconds, double WallSeconds);

/// <summary>Talks to the same OpenAI-compatible endpoint external clients use.</summary>
public sealed class LocalApiClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly string _baseUrl;

    public LocalApiClient(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        if (!string.IsNullOrEmpty(apiKey)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>Raw /v1/completions call with a fixed seed. ignore_eos forces the full token count.</summary>
    public async Task<CompletionTimings> CompleteAsync(string model, string prompt, int maxTokens, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model, prompt, max_tokens = maxTokens, temperature = 0, seed = 42, stream = false, ignore_eos = true, cache_prompt = false
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.PostAsync(_baseUrl + "/completions", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(200, text.Length)]}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        int pt = 0, gt = 0; double pps = 0, gps = 0, ttft = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            pt = u.TryGetProperty("prompt_tokens", out var a) ? a.GetInt32() : 0;
            gt = u.TryGetProperty("completion_tokens", out var b) ? b.GetInt32() : 0;
        }
        if (root.TryGetProperty("timings", out var t))
        {
            pps = t.TryGetProperty("prompt_per_second", out var a) ? a.GetDouble() : 0;
            gps = t.TryGetProperty("predicted_per_second", out var b) ? b.GetDouble() : 0;
            // Time to first token = prompt processing time (+ first sample, negligible).
            ttft = t.TryGetProperty("prompt_ms", out var c) ? c.GetDouble() / 1000 : 0;
            if (pt == 0 && t.TryGetProperty("prompt_n", out var d)) pt = d.GetInt32();
            if (gt == 0 && t.TryGetProperty("predicted_n", out var e)) gt = e.GetInt32();
        }
        if (gps == 0 && gt > 0) gps = gt / sw.Elapsed.TotalSeconds;
        return new(pt, gt, pps, gps, ttft, sw.Elapsed.TotalSeconds);
    }
}

public static class BenchmarkRunner
{
    // Fixed prompt: identical across runs so results are comparable (about 128 tokens).
    public static readonly string StandardPrompt = string.Join(' ', Enumerable.Repeat(
        "The quick brown fox jumps over the lazy dog while the engineer measures throughput.", 8))
        + " Continue this text in detail:";

    public const int StandardGenTokens = 1024;
    public const int StandardIterations = 3;

    public static async Task<BenchmarkResult> RunAsync(
        LocalApiClient api, AnalyticsStore store, ITelemetryProvider? telemetry,
        string model, string? quant, int ctx, string gpu, string? runtime,
        IProgress<string>? progress = null, CancellationToken ct = default,
        string? prompt = null, int genTokens = StandardGenTokens, int iterations = StandardIterations, string? label = null)
    {
        using var window = telemetry?.BeginWindow();
        var runs = new List<CompletionTimings>();

        progress?.Report("Warm-up...");
        await api.CompleteAsync(model, prompt ?? StandardPrompt, 16, ct);

        for (int i = 1; i <= iterations; i++)
        {
            progress?.Report($"Iteration {i}/{iterations}...");
            runs.Add(await api.CompleteAsync(model, prompt ?? StandardPrompt, genTokens, ct));
        }

        var gen = runs.Select(r => r.GenerationTps).ToList();
        var hw = window?.Summarize() ?? TelemetrySummary.Empty;
        var avg = gen.Average();
        var result = new BenchmarkResult(0, DateTime.UtcNow, model, quant, ctx, gpu, runtime, iterations,
            runs[0].PromptTokens, (int)runs.Average(r => r.GeneratedTokens),
            avg, Stats.Median(gen) ?? avg, gen.Min(), gen.Max(), runs.Average(r => r.PromptTps), runs.Average(r => r.TtftSeconds),
            hw, hw.AvgPowerW is > 0 ? avg / hw.AvgPowerW : null, label);
        var id = store.InsertBenchmark(result);
        return result with { Id = id };
    }

    public sealed record Comparison(double GenDeltaPct, double? PowerDeltaPct, double? TempDeltaC, double? VramDeltaBytes);

    public static Comparison Compare(BenchmarkResult current, BenchmarkResult previous) => new(
        previous.GenAvg > 0 ? (current.GenAvg - previous.GenAvg) / previous.GenAvg * 100 : 0,
        current.Hardware.AvgPowerW is { } cp && previous.Hardware.AvgPowerW is { } pp && pp > 0 ? (cp - pp) / pp * 100 : null,
        current.Hardware.PeakTempC is { } ct && previous.Hardware.PeakTempC is { } pt ? ct - pt : null,
        current.Hardware.PeakVramBytes is { } cv && previous.Hardware.PeakVramBytes is { } pv ? cv - pv : null);
}

public static class StressTester
{
    public static async Task<StabilityResult> RunAsync(
        LocalApiClient api, AnalyticsStore store, ITelemetryProvider? telemetry, IServerStatus server,
        string model, TimeSpan duration, IProgress<(TimeSpan elapsed, long tokens, int errors)>? progress = null, CancellationToken ct = default)
    {
        using var window = telemetry?.BeginWindow();
        int crashes0 = server.CrashCount, gpu0 = server.GpuErrorCount;
        long tokens = 0; int errors = 0, ok = 0; double tpsSum = 0, tpsMin = double.MaxValue;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            try
            {
                var r = await api.CompleteAsync(model, BenchmarkRunner.StandardPrompt, 256, ct);
                tokens += r.PromptTokens + r.GeneratedTokens; ok++;
                tpsSum += r.GenerationTps; tpsMin = Math.Min(tpsMin, r.GenerationTps);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                errors++;
                // Back off while the server auto-recovers.
                try { await Task.Delay(2000, ct); } catch { break; }
            }
            progress?.Report((sw.Elapsed, tokens, errors));
        }
        sw.Stop();

        var result = new StabilityResult(DateTime.UtcNow, model, sw.Elapsed, tokens,
            ok > 0 ? tpsSum / ok : 0, ok > 0 ? tpsMin : 0, errors, server.CrashCount - crashes0, server.GpuErrorCount - gpu0,
            window?.Summarize() ?? TelemetrySummary.Empty);
        store.InsertStability(result);
        return result;
    }
}
