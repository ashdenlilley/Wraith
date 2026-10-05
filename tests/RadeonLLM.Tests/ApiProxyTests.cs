using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RadeonLLM.Api;
using RadeonLLM.Core;
using RadeonLLM.Security;
using Xunit;

namespace RadeonLLM.Tests;

public class ApiProxyTests : IAsyncLifetime
{
    sealed class Obs : IRequestObserver
    {
        public readonly List<RequestRecord> Records = new();
        public void OnRequest(RequestRecord r) { lock (Records) Records.Add(r); }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    readonly int _upPort = FreePort(), _apiPort = FreePort();
    readonly Obs _obs = new();
    WebApplication _upstream = null!;
    ApiHost _api = null!;
    readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls($"http://127.0.0.1:{_upPort}");
        _upstream = b.Build();
        _upstream.MapGet("/v1/models", (HttpContext c) =>
            c.Request.Headers.Authorization.ToString() is var a && a.StartsWith("Bearer up-") && a != "Bearer up-good"
                ? Results.StatusCode(401) : Results.Json(new { data = new[] { new { id = "m" } }, auth = a }));
        _upstream.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var rd = new StreamReader(ctx.Request.Body);
            var body = await rd.ReadToEndAsync();
            if (body.Contains("\"stream\":true"))
            {
                ctx.Response.ContentType = "text/event-stream";
                for (int i = 0; i < 3; i++)
                {
                    await ctx.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"t{i}\"}}}}]}}\n\n");
                    await ctx.Response.Body.FlushAsync();
                    await Task.Delay(30);
                }
                await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":3},\"timings\":{\"prompt_per_second\":900.5,\"predicted_per_second\":42.5,\"prompt_ms\":12.2}}\n\ndata: [DONE]\n\n");
            }
            else
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    choices = new[] { new { message = new { role = "assistant", content = "hi" } } },
                    usage = new { prompt_tokens = 7, completion_tokens = 5 },
                    timings = new { prompt_per_second = 100.0, predicted_per_second = 30.0, prompt_ms = 70.0 }
                });
            }
        });
        await _upstream.StartAsync();

        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName);
        _api = new ApiHost(new LogChannel(paths, "api"), _obs);
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _upstream.DisposeAsync();
        _http.Dispose();
    }

    string Url(string p) => $"http://127.0.0.1:{_apiPort}{p}";

    [Fact]
    public async Task ProxiesNonStreamingAndRecordsUsage()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, _upPort, false, null));
        var resp = await _http.PostAsync(Url("/v1/chat/completions"),
            new StringContent("{\"model\":\"m\",\"messages\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"content\":\"hi\"", await resp.Content.ReadAsStringAsync());

        var r = Assert.Single(_obs.Records);
        Assert.Equal(7, r.PromptTokens); Assert.Equal(5, r.CompletionTokens);
        Assert.Equal(30, r.GenerationTps); Assert.Equal(0.07, r.TtftSeconds!.Value, 3);
        Assert.Equal("m", r.Model);
    }

    [Fact]
    public async Task ProxiesStreamingAndMeasuresTtft()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, _upPort, false, null));
        var req = new HttpRequestMessage(HttpMethod.Post, Url("/v1/chat/completions"))
        { Content = new StringContent("{\"model\":\"m\",\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", text);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        await Task.Delay(100);
        var r = Assert.Single(_obs.Records);
        Assert.Equal(11, r.PromptTokens); Assert.Equal(3, r.CompletionTokens);
        Assert.Equal(42.5, r.GenerationTps); Assert.Equal(900.5, r.PromptTps);
        Assert.NotNull(r.TtftSeconds);
    }

    [Fact]
    public async Task ModelsEndpointIsNotCountedAsInference()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, _upPort, false, null));
        var resp = await _http.GetAsync(Url("/v1/models"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(_obs.Records);
    }

    [Fact]
    public async Task EnforcesApiKeyWhenRequired()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, _upPort, true, "secret-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(Url("/v1/models"))).StatusCode);

        var bad = new HttpRequestMessage(HttpMethod.Get, Url("/v1/models")); bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(bad)).StatusCode);

        var good = new HttpRequestMessage(HttpMethod.Get, Url("/v1/models")); good.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(good)).StatusCode);
    }

    [Fact]
    public async Task ReplacesClientAuthWithUpstreamKey()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, _upPort, false, null, "up-good"));
        var req = new HttpRequestMessage(HttpMethod.Get, Url("/v1/models")); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-key");
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Bearer up-good", body);
        Assert.DoesNotContain("client-key", body);
    }

    [Fact]
    public async Task Returns503WhenUpstreamDown()
    {
        await _api.StartAsync(new ApiOptions("127.0.0.1", _apiPort, FreePort(), false, null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.GetAsync(Url("/v1/models"))).StatusCode);
    }
}

public class SecurityTests
{
    [Fact]
    public void ApiKeyIsEncryptedAtRest()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName);
        var store = new ApiKeyStore(paths);
        Assert.Null(store.Get());
        var key = store.Generate();
        Assert.StartsWith("rllm-", key);
        Assert.Equal(key, store.Get());
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(File.ReadAllBytes(paths.SecretFile)));
    }
}
