using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RadeonLLM.Core;

namespace RadeonLLM.Api;

public sealed record ApiOptions(string BindAddress, int Port, int UpstreamPort, bool AuthRequired, string? ApiKey);

/// <summary>
/// Public OpenAI-compatible front door. Transparently proxies /v1/* to the managed llama-server,
/// enforces the API key when required and reports per-request metadata to analytics.
/// Request and response bodies are passed through untouched and never stored.
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate", "Host", "Content-Length"
    };

    readonly LogChannel _log;
    readonly IRequestObserver? _observer;
    readonly HttpClient _upstream = new(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.None })
    { Timeout = Timeout.InfiniteTimeSpan };
    WebApplication? _app;
    ApiOptions _opt = new("127.0.0.1", 8080, 18080, false, null);

    public bool Running => _app is not null;

    public ApiHost(LogChannel log, IRequestObserver? observer)
    {
        _log = log; _observer = observer;
    }

    public async Task StartAsync(ApiOptions options)
    {
        await StopAsync();
        _opt = options;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
            var ip = System.Net.IPAddress.Parse(options.BindAddress);
            k.Listen(ip, options.Port);
        });
        var app = builder.Build();
        app.Map("/v1/{**rest}", HandleAsync);
        app.MapGet("/", () => Results.Json(new { name = "RadeonLLM", api = "/v1" }));
        await app.StartAsync();
        _app = app;
        _log.Info($"API listening on {options.BindAddress}:{options.Port} (auth {(options.AuthRequired ? "required" : "off")}).");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app; _app = null;
        try { await app.StopAsync(new CancellationTokenSource(2000).Token); await app.DisposeAsync(); } catch { }
        _log.Info("API stopped.");
    }

    async Task HandleAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var path = req.Path.Value ?? "/v1";

        if (_opt.AuthRequired && !IsAuthorized(req))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Invalid or missing API key.", type = "invalid_request_error", code = "invalid_api_key" } });
            _log.Warn($"401 {req.Method} {path} from {ctx.Connection.RemoteIpAddress}");
            return;
        }

        var sw = Stopwatch.StartNew();
        byte[] body = [];
        if (req.ContentLength != 0 && (HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method)))
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ctx.RequestAborted);
            body = ms.ToArray();
        }
        var meta = RequestMeta.Parse(body);

        var msg = new HttpRequestMessage(new HttpMethod(req.Method), $"http://127.0.0.1:{_opt.UpstreamPort}{path}{req.QueryString}");
        if (body.Length > 0 || HttpMethods.IsPost(req.Method))
        {
            msg.Content = new ByteArrayContent(body);
            if (req.ContentType is { } ct) msg.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }
        foreach (var h in req.Headers)
        {
            if (HopByHop.Contains(h.Key) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            msg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _upstream.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.Error($"Upstream unavailable for {path}", ex);
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Inference server is not running.", type = "server_error", code = "server_unavailable" } });
            return;
        }

        using (resp)
        {
            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers) if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in resp.Content.Headers) if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();

            bool track = HttpMethods.IsPost(req.Method) && IsInferencePath(path);
            var capture = track ? new ResponseCapture(sw, meta.Stream) : null;
            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var buf = new byte[16 * 1024]; int n;
                while ((n = await src.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    capture?.Feed(buf.AsSpan(0, n));
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    if (meta.Stream) await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client went away */ }
            catch (Exception ex) { _log.Error($"Streaming {path} failed", ex); }
            finally
            {
                if (capture is not null)
                {
                    var r = capture.Finish(meta.Model, path, (int)resp.StatusCode);
                    _log.Info($"{r.Status} {path} prompt={r.PromptTokens} gen={r.CompletionTokens} {r.GenerationTps?.ToString("0.0") ?? "-"} tok/s {r.DurationSeconds:0.00}s");
                    try { _observer?.OnRequest(r); } catch (Exception ex) { _log.Error("Analytics observer failed", ex); }
                }
            }
        }
    }

    static bool IsInferencePath(string p) =>
        p.EndsWith("/chat/completions", StringComparison.Ordinal) || p.EndsWith("/completions", StringComparison.Ordinal)
        || p.EndsWith("/embeddings", StringComparison.Ordinal) || p.EndsWith("/responses", StringComparison.Ordinal);

    bool IsAuthorized(HttpRequest req)
    {
        if (string.IsNullOrEmpty(_opt.ApiKey)) return false;
        var h = req.Headers.Authorization.ToString();
        var supplied = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : req.Headers["x-api-key"].ToString();
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(_opt.ApiKey));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _upstream.Dispose();
    }
}
