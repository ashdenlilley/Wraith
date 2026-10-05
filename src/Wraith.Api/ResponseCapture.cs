using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Wraith.Core;

namespace Wraith.Api;

internal readonly record struct RequestMeta(string? Model, bool Stream)
{
    public static RequestMeta Parse(byte[] body)
    {
        if (body.Length == 0) return default;
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            return new(r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                r.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True);
        }
        catch { return default; }
    }
}

/// <summary>
/// Extracts token counts and timings from a proxied response without storing its content.
/// Streaming: parses SSE "data:" lines incrementally. Non-streaming: parses the JSON body once.
/// </summary>
internal sealed class ResponseCapture
{
    const int MaxBuffered = 8 * 1024 * 1024;
    readonly Stopwatch _sw;
    readonly bool _stream;
    readonly DateTime _started = DateTime.UtcNow;
    readonly MemoryStream _body = new();
    readonly StringBuilder _line = new();
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    readonly SortedDictionary<int, int> _tokensPerSecond = new();

    double? _firstTokenAt;
    int _promptTokens, _completionTokens;
    double? _promptTps, _genTps, _promptMs;

    public ResponseCapture(Stopwatch sw, bool stream) { _sw = sw; _stream = stream; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (!_stream)
        {
            if (_body.Length < MaxBuffered) _body.Write(data);
            return;
        }
        var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
        int n = _decoder.GetChars(data, chars, false);
        for (int i = 0; i < n; i++)
        {
            if (chars[i] == '\n') { ParseLine(_line.ToString()); _line.Clear(); }
            else if (chars[i] != '\r') _line.Append(chars[i]);
        }
    }

    void ParseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var json = line.AsSpan(5).Trim();
        if (json.Length == 0 || json.SequenceEqual("[DONE]")) return;
        try
        {
            using var d = JsonDocument.Parse(json.ToString());
            var root = d.RootElement;
            Harvest(root);
            if (HasContent(root))
            {
                var t = _sw.Elapsed.TotalSeconds;
                _firstTokenAt ??= t;
                var bucket = (int)(t - _firstTokenAt.Value);
                _tokensPerSecond[bucket] = _tokensPerSecond.GetValueOrDefault(bucket) + 1;
            }
        }
        catch (JsonException) { }
    }

    static bool HasContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0) return false;
        var c = ch[0];
        if (c.TryGetProperty("delta", out var delta))
        {
            foreach (var k in new[] { "content", "reasoning_content" })
                if (delta.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0) return true;
        }
        return c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0;
    }

    void Harvest(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) _promptTokens = p.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number) _completionTokens = c.GetInt32();
        }
        if (root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            if (t.TryGetProperty("prompt_per_second", out var pps) && pps.ValueKind == JsonValueKind.Number) _promptTps = pps.GetDouble();
            if (t.TryGetProperty("predicted_per_second", out var gps) && gps.ValueKind == JsonValueKind.Number) _genTps = gps.GetDouble();
            if (t.TryGetProperty("prompt_ms", out var pm) && pm.ValueKind == JsonValueKind.Number) _promptMs = pm.GetDouble();
            if (_promptTokens == 0 && t.TryGetProperty("prompt_n", out var pn) && pn.ValueKind == JsonValueKind.Number) _promptTokens = pn.GetInt32();
            if (_completionTokens == 0 && t.TryGetProperty("predicted_n", out var gn) && gn.ValueKind == JsonValueKind.Number) _completionTokens = gn.GetInt32();
        }
    }

    public RequestRecord Finish(string? model, string endpoint, int status)
    {
        if (!_stream && _body.Length > 0)
        {
            try { using var d = JsonDocument.Parse(_body.ToArray()); Harvest(d.RootElement); } catch (JsonException) { }
        }
        else if (_stream && _line.Length > 0) ParseLine(_line.ToString());

        var duration = _sw.Elapsed.TotalSeconds;
        double? ttft = _firstTokenAt ?? (_promptMs is { } ms ? ms / 1000 : null);

        double? peak = null, min = null;
        if (_stream && _tokensPerSecond.Count > 1)
        {
            // Last bucket is partial; drop it from min so a short tail is not reported as a slowdown.
            var full = _tokensPerSecond.OrderBy(k => k.Key).SkipLast(1).Select(k => (double)k.Value).ToList();
            if (full.Count > 0) { peak = full.Max(); min = full.Min(); }
        }
        return new RequestRecord(_started, model, endpoint, _promptTokens, _completionTokens, ttft, _promptTps, _genTps,
            duration, status, peak, min);
    }
}
