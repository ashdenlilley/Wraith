using System.Text.Json;
using Wraith.Core;
using Wraith.Inference;
using Wraith.Models;
using Wraith.Runtime;
using Xunit;

namespace Wraith.Tests;

public class BackendTests
{
    static JsonElement Release(params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n => $"{{\"name\":\"{n}\",\"browser_download_url\":\"https://x/{n}\",\"size\":10,\"digest\":\"sha256:abc\"}}"));
        return JsonDocument.Parse($"{{\"tag_name\":\"b1\",\"assets\":[{assets}]}}").RootElement;
    }

    static readonly string[] B11400 =
    [
        "cudart-llama-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-arm64.zip",
        "llama-b1-bin-win-cpu-arm64.zip", "llama-b1-bin-win-cpu-x64.zip", "llama-b1-bin-win-cuda-12.4-x64.zip",
        "llama-b1-bin-win-cuda-13.4-arm64.zip", "llama-b1-bin-win-cuda-13.4-x64.zip", "llama-b1-bin-win-rocm-10.0-x64.zip",
        "llama-b1-bin-win-sycl-x64.zip", "llama-b1-bin-win-vulkan-arm64.zip", "llama-b1-bin-win-vulkan-x64.zip"
    ];

    [Theory]
    [InlineData(RuntimeBackend.Vulkan, "llama-b1-bin-win-vulkan-x64.zip")]
    [InlineData(RuntimeBackend.Cpu, "llama-b1-bin-win-cpu-x64.zip")]
    [InlineData(RuntimeBackend.Rocm, "llama-b1-bin-win-rocm-10.0-x64.zip")]
    public void PicksTheRightX64Asset(RuntimeBackend backend, string expected)
    {
        var r = RuntimeManager.ParseRelease(Release(B11400), backend)!;
        Assert.Equal(expected, r.AssetName);
        Assert.Empty(r.Extras);
    }

    [Fact]
    public void CudaBringsItsRuntimeDllsAndRespectsDriverCap()
    {
        var r13 = RuntimeManager.ParseRelease(Release(B11400), RuntimeBackend.Cuda, "13.4")!;
        Assert.Equal("llama-b1-bin-win-cuda-13.4-x64.zip", r13.AssetName);
        Assert.Equal("cudart-llama-bin-win-cuda-13.4-x64.zip", Assert.Single(r13.Extras).Name);

        var r12 = RuntimeManager.ParseRelease(Release(B11400), RuntimeBackend.Cuda, "12.4")!; // older driver: never 13.x
        Assert.Equal("llama-b1-bin-win-cuda-12.4-x64.zip", r12.AssetName);

        // 13.4 main asset without its cudart must not be chosen
        var broken = Release("llama-b1-bin-win-cuda-13.4-x64.zip", "llama-b1-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip");
        Assert.Equal("llama-b1-bin-win-cuda-12.4-x64.zip", RuntimeManager.ParseRelease(broken, RuntimeBackend.Cuda, "13.4")!.AssetName);
        Assert.Null(RuntimeManager.ParseRelease(Release("llama-b1-bin-win-vulkan-x64.zip"), RuntimeBackend.Cuda));
    }

    [Theory]
    [InlineData("32.0.15.6094", 560.94)]
    [InlineData("32.0.15.8180", 581.80)]
    [InlineData("31.0.15.2802", 528.02)]
    [InlineData("garbage", null)]
    public void DecodesNvidiaDriverVersion(string raw, double? expected)
    {
        var v = BackendSelector.NvidiaDriverVersion(raw);
        if (expected is null) Assert.Null(v); else Assert.Equal(expected.Value, v!.Value, 2);
    }

    [Theory]
    [InlineData(581.80, "13.4")]
    [InlineData(560.94, "12.4")]
    [InlineData(500.0, null)]
    public void MapsDriverToCuda(double driver, string? cuda) => Assert.Equal(cuda, BackendSelector.CudaVersionFor(driver));

    static SystemInfo Sys(params GpuInfo[] gpus) => new(gpus, "cpu", 16, 32L << 30, true);

    [Fact]
    public void AutoPicksBackendPerHardware()
    {
        var nv = new GpuInfo("NVIDIA GeForce RTX 4090", "NVIDIA", 24L << 30, "32.0.15.8180");
        var amd = new GpuInfo("AMD Radeon RX 7900 XTX", "AMD", 24L << 30, "32.0.21.1");
        var oldNv = new GpuInfo("NVIDIA GeForce GTX 750", "NVIDIA", 2L << 30, "30.0.14.7111");

        Assert.Equal([RuntimeBackend.Cuda, RuntimeBackend.Vulkan, RuntimeBackend.Cpu], BackendSelector.Candidates(RuntimeBackend.Auto, Sys(nv)));
        Assert.Equal([RuntimeBackend.Vulkan, RuntimeBackend.Cpu], BackendSelector.Candidates(RuntimeBackend.Auto, Sys(amd)));
        Assert.Equal([RuntimeBackend.Vulkan, RuntimeBackend.Cpu], BackendSelector.Candidates(RuntimeBackend.Auto, Sys(oldNv))); // driver too old for CUDA
        Assert.Equal([RuntimeBackend.Cpu], BackendSelector.Candidates(RuntimeBackend.Auto, Sys()));
        Assert.Equal([RuntimeBackend.Rocm], BackendSelector.Candidates(RuntimeBackend.Rocm, Sys(amd)));
    }

    [Fact]
    public void DiscreteGpuBeatsIntegratedRegardlessOfVendor()
    {
        var igpu = new GpuInfo("AMD Radeon(TM) Graphics", "AMD", 512L << 20, null);
        var nv = new GpuInfo("NVIDIA GeForce RTX 3060", "NVIDIA", 12L << 30, "32.0.15.6094");
        Assert.Equal(nv, Sys(igpu, nv).PrimaryGpu);
    }

    [Fact]
    public void ParsesCudaAndRocmDeviceLines()
    {
        var d = DeviceParser.Parse("  CUDA0: NVIDIA GeForce RTX 4090 (24564 MiB, 23000 MiB free)\n  ROCm0: AMD Radeon RX 7900 XTX (24560 MiB, 24000 MiB free)");
        Assert.Equal(DeviceKind.Cuda, d[0].Kind);
        Assert.Equal(DeviceKind.Rocm, d[1].Kind);
        Assert.True(BackendSelector.Works(RuntimeBackend.Cuda, d));
        Assert.False(BackendSelector.Works(RuntimeBackend.Vulkan, d));
        Assert.True(BackendSelector.Works(RuntimeBackend.Cpu, []));
    }

    [Fact]
    public void CpuOnlyConfigUsesNoGpuLayersAndSmallBatches()
    {
        var m = new ModelEntry { Id = "m", Path = "m.gguf", SizeBytes = 4L << 30, BlockCount = 32, HeadCount = 32, HeadCountKv = 8, EmbeddingLength = 4096, ContextLength = 32768 };
        var plan = ConfigBuilder.Build(new AppSettings { Profile = PerformanceProfile.Maximum }, m, 20L << 30, 16, false, "Vulkan0", cpuOnly: true);
        Assert.Equal(0, plan.Config.GpuLayers);
        Assert.True(plan.Config.Context <= 16384);
        Assert.True(plan.Config.Batch <= 512);
    }

    [Fact]
    public void BackendSettingPersists()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName);
        var a = new SettingsStore(paths); a.Current.Backend = RuntimeBackend.Cuda; a.Save();
        var b = new SettingsStore(paths); b.Load();
        Assert.Equal(RuntimeBackend.Cuda, b.Current.Backend);
    }
}
