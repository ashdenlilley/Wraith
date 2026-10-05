using RadeonLLM.Core;
using RadeonLLM.Runtime;
using Xunit;

namespace RadeonLLM.Tests;

/// <summary>Opt-in: set RADEONLLM_LIVE=1. Downloads the real llama.cpp Vulkan runtime and probes the GPU.</summary>
public class LiveRuntimeTests
{
    [Fact]
    public async Task InstallsRealRuntimeAndListsVulkanDevices()
    {
        if (Environment.GetEnvironmentVariable("RADEONLLM_LIVE") != "1") return;
        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName); paths.EnsureCreated();
        var store = new SettingsStore(paths);
        var rt = new RuntimeManager(paths, new LogChannel(paths, "runtime"), new NetworkGate(store));

        var rel = await rt.GetLatestAsync();
        await rt.InstallAsync(rel);
        Assert.True(rt.IsInstalled);
        Assert.True((await rt.VerifyAsync()).Ok);
        var devices = await rt.ListDevicesAsync();
        Console.WriteLine($"{rel.Version}: " + string.Join("; ", devices.Select(d => $"{d.Id} {d.Name} {d.TotalMiB}MiB")));
        Assert.Contains(devices, d => d.IsVulkan);
        Assert.Null(await rt.CheckForUpdateAsync(null));
    }
}

/// <summary>Opt-in end-to-end: RADEONLLM_LIVE=1 and RADEONLLM_TEST_GGUF=path. Real llama-server on the GPU through the API proxy.</summary>
public class LiveEndToEndTests
{
    [Fact]
    public async Task ServesModelThroughProxyWithAnalyticsAndBenchmark()
    {
        var gguf = Environment.GetEnvironmentVariable("RADEONLLM_TEST_GGUF");
        if (Environment.GetEnvironmentVariable("RADEONLLM_LIVE") != "1" || gguf is null) return;

        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName); paths.EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Current.Port = 18181; settings.Current.InternalPort = 18182;
        var logs = new Logs(paths);
        var rt = new RuntimeManager(paths, logs.Runtime, new NetworkGate(settings));
        await rt.InstallAsync(await rt.GetLatestAsync());
        var devs = await rt.ListDevicesAsync();
        var dev = devs.First(d => d.Name.Contains("6900"));

        var lib = new RadeonLLM.Models.ModelLibrary(paths);
        var model = await lib.ImportAsync(gguf, copy: false);
        var plan = RadeonLLM.Inference.ConfigBuilder.Build(settings.Current, model, dev.FreeMiB * 1048576L, 16, false, dev.Id);

        using var store = new RadeonLLM.Analytics.AnalyticsStore(paths);
        var tracker = new RadeonLLM.Analytics.SessionTracker(store, null);
        using var server = new RadeonLLM.Inference.LlamaServerManager(logs.Runtime, logs.Inference);
        await using var api = new RadeonLLM.Api.ApiHost(logs.Api, tracker);
        try
        {
            await server.StartAsync(rt.ServerExe!, plan.Config);
            Assert.Equal(ServerState.Running, server.State);
            await api.StartAsync(new RadeonLLM.Api.ApiOptions("127.0.0.1", 18181, 18182, false, null));
            tracker.Begin(model.Id, model.Quantization, plan.Config.Context, dev.Name, rt.Version);

            var client = new RadeonLLM.Analytics.LocalApiClient("http://127.0.0.1:18181/v1", null);
            var bench = await RadeonLLM.Analytics.BenchmarkRunner.RunAsync(client, store, null, model.Id, model.Quantization,
                plan.Config.Context, dev.Name, rt.Version, iterations: 2, genTokens: 128);
            Console.WriteLine($"BENCH gen={bench.GenAvg:0.0} tok/s prompt={bench.PromptTps:0} tok/s ttft={bench.Ttft * 1000:0}ms");
            Assert.True(bench.GenAvg > 5);

            // streaming through the proxy
            using var http = new HttpClient();
            var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:18181/v1/chat/completions")
            { Content = new StringContent("{\"model\":\"x\",\"stream\":true,\"max_tokens\":40,\"messages\":[{\"role\":\"user\",\"content\":\"Say hello.\"}]}", System.Text.Encoding.UTF8, "application/json") },
                HttpCompletionOption.ResponseHeadersRead);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("data: [DONE]", body);
            await Task.Delay(300);

            var s = tracker.Snapshot();
            Console.WriteLine($"SESSION requests={s.Requests} gen={s.GeneratedTokens} tps={s.GenerationTps:0.0} ttft={s.MedianTtft}");
            Assert.True(s.Requests >= 3);
            Assert.True(s.GeneratedTokens > 0);
            Assert.NotNull(s.MedianTtft);
            tracker.End();
        }
        finally { await server.StopAsync(); }
        Assert.Equal(ServerState.Stopped, server.State);
    }
}
