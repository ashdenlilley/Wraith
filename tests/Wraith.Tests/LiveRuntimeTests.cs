using System.Net.Http;
using Wraith.Core;
using Wraith.Runtime;
using Xunit;

namespace Wraith.Tests;

/// <summary>Opt-in: set WRAITH_LIVE=1. Downloads the real llama.cpp Vulkan runtime and probes the GPU.</summary>
[Collection("env")]
public class LiveRuntimeTests
{
    [Fact]
    public async Task InstallsRealRuntimeAndListsVulkanDevices()
    {
        if (Environment.GetEnvironmentVariable("WRAITH_LIVE") != "1") return;
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

/// <summary>Opt-in end-to-end: WRAITH_LIVE=1 and WRAITH_TEST_GGUF=path. Real llama-server on the GPU through the API proxy.</summary>
[Collection("env")]
public class LiveEndToEndTests
{
    [Fact]
    public async Task ServesModelThroughProxyWithAnalyticsAndBenchmark()
    {
        var gguf = Environment.GetEnvironmentVariable("WRAITH_TEST_GGUF");
        if (Environment.GetEnvironmentVariable("WRAITH_LIVE") != "1" || gguf is null) return;

        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName); paths.EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Current.Port = 18181; settings.Current.InternalPort = 18182;
        var logs = new Logs(paths);
        var rt = new RuntimeManager(paths, logs.Runtime, new NetworkGate(settings));
        await rt.InstallAsync(await rt.GetLatestAsync());
        var devs = await rt.ListDevicesAsync();
        var dev = devs.First(d => d.Name.Contains("6900"));

        var lib = new Wraith.Models.ModelLibrary(paths);
        var model = await lib.ImportAsync(gguf, copy: false);
        var plan = Wraith.Inference.ConfigBuilder.Build(settings.Current, model, dev.FreeMiB * 1048576L, 16, false, dev.Id);

        using var store = new Wraith.Analytics.AnalyticsStore(paths);
        var tracker = new Wraith.Analytics.SessionTracker(store, null);
        using var server = new Wraith.Inference.LlamaServerManager(logs.Runtime, logs.Inference);
        await using var api = new Wraith.Api.ApiHost(logs.Api, tracker);
        try
        {
            await server.StartAsync(rt.ServerExe!, plan.Config with { ApiKey = "up-key" });
            Assert.Equal(ServerState.Running, server.State);
            await api.StartAsync(new Wraith.Api.ApiOptions("127.0.0.1", 18181, 18182, false, null, "up-key"));
            tracker.Begin(model.Id, model.Quantization, plan.Config.Context, dev.Name, rt.Version);

            var client = new Wraith.Analytics.LocalApiClient("http://127.0.0.1:18181/v1", null);
            var bench = await Wraith.Analytics.BenchmarkRunner.RunAsync(client, store, null, model.Id, model.Quantization,
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

    [Fact]
    public async Task RecoversAfterServerProcessIsKilledAndRunsStressTest()
    {
        var gguf = Environment.GetEnvironmentVariable("WRAITH_TEST_GGUF");
        if (Environment.GetEnvironmentVariable("WRAITH_LIVE") != "1" || gguf is null) return;

        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName); paths.EnsureCreated();
        var settings = new SettingsStore(paths);
        var logs = new Logs(paths);
        var rt = new RuntimeManager(paths, logs.Runtime, new NetworkGate(settings));
        await rt.InstallAsync(await rt.GetLatestAsync());
        var dev = (await rt.ListDevicesAsync()).First(d => d.Name.Contains("6900"));
        var lib = new Wraith.Models.ModelLibrary(paths);
        var model = await lib.ImportAsync(gguf, copy: false);
        var plan = Wraith.Inference.ConfigBuilder.Build(settings.Current, model, dev.FreeMiB * 1048576L, 16, false, dev.Id);

        using var store = new Wraith.Analytics.AnalyticsStore(paths);
        using var server = new Wraith.Inference.LlamaServerManager(logs.Runtime, logs.Inference);
        var recovered = new TaskCompletionSource();
        int readyCount = 0;
        server.Ready += () => { if (++readyCount == 2) recovered.TrySetResult(); };
        var cfg = plan.Config with { Port = 18192, ApiKey = "k" };
        try
        {
            await server.StartAsync(rt.ServerExe!, cfg);
            // kill the child hard, as a GPU driver reset would
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("llama-server")) p.Kill(true);
            await Task.WhenAny(recovered.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            Assert.True(recovered.Task.IsCompletedSuccessfully, "server did not auto-recover");
            Assert.Equal(ServerState.Running, server.State);
            Assert.Equal(1, server.CrashCount);

            var client = new Wraith.Analytics.LocalApiClient("http://127.0.0.1:18192/v1", "k");
            var r = await Wraith.Analytics.StressTester.RunAsync(client, store, null, server, model.Id, TimeSpan.FromSeconds(8));
            Console.WriteLine($"STRESS tokens={r.Tokens} avg={r.AvgTps:0.0} errors={r.Errors} crashes={r.Crashes} pass={r.Passed}");
            Assert.True(r.Passed);
            Assert.True(r.Tokens > 0);
        }
        finally { await server.StopAsync(); }
    }
}

/// <summary>Opt-in: backend selection, CPU inference and failed-backend rollback through the real AppController.</summary>
[Collection("env")]
public class LiveBackendTests
{
    [Fact]
    public async Task AutoPicksVulkanThenCpuRunsThenBadRocmRollsBack()
    {
        var gguf = Environment.GetEnvironmentVariable("WRAITH_TEST_GGUF");
        if (Environment.GetEnvironmentVariable("WRAITH_LIVE") != "1" || gguf is null) return;

        Environment.SetEnvironmentVariable("WRAITH_HOME", Directory.CreateTempSubdirectory().FullName);
        using var c = new Wraith.App.AppController();
        var progress = new Progress<(string, double)>();

        // 1. Auto on an AMD box chooses Vulkan and finds the GPU.
        Assert.Equal(RuntimeBackend.Auto, c.Settings.Backend);
        Assert.Equal(RuntimeBackend.Vulkan, await c.SetupRuntimeAsync(progress));
        Assert.Equal(RuntimeBackend.Vulkan, c.Runtime.ActiveBackend);
        Assert.NotNull(c.Device);

        // 2. Switch to CPU and serve a model with no GPU involved.
        c.Settings.Backend = RuntimeBackend.Cpu;
        Assert.Equal(RuntimeBackend.Cpu, await c.SetupRuntimeAsync(progress));
        Assert.True(c.IsCpuBackend); Assert.Null(c.Device);
        Assert.True(c.Runtime.HasBuild(c.Runtime.Version!, RuntimeBackend.Vulkan)); // both builds coexist

        var m = await c.Library.ImportAsync(gguf, copy: false);
        c.Settings.DefaultModelId = m.Id; c.Settings.Port = 18281;
        await c.StartAsync();
        try
        {
            Assert.Equal(0, c.LastPlan!.Config.GpuLayers);
            var r = await Wraith.Analytics.BenchmarkRunner.RunAsync(c.CreateApiClient(), c.Store, null, m.Id, m.Quantization,
                c.LastPlan.Config.Context, "CPU", c.Runtime.Version, iterations: 1, genTokens: 64);
            Console.WriteLine($"CPU BENCH gen={r.GenAvg:0.0} tok/s");
            Assert.True(r.GenAvg > 1);
        }
        finally { await c.StopAsync(); }

        // 3. ROCm cannot see the 6900 XT: setup must fail and leave CPU active.
        c.Settings.Backend = RuntimeBackend.Rocm;
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.SetupRuntimeAsync(progress));
        Assert.Equal(RuntimeBackend.Cpu, c.Runtime.ActiveBackend);
    }
}
