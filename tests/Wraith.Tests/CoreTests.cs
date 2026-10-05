using System.Text;
using Wraith.Analytics;
using Wraith.Core;
using Wraith.Inference;
using Wraith.Models;
using Wraith.Runtime;
using Xunit;

namespace Wraith.Tests;

public class GgufTests
{
    internal static string WriteSyntheticGguf(string dir, string arch = "qwen2", string name = "Qwen2.5 Coder 14B", int fileType = 17)
    {
        var path = Path.Combine(dir, "model-Q5_K_M.gguf");
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void KvStr(string k, string v) { Str(k); w.Write(8); Str(v); }
        void KvU32(string k, uint v) { Str(k); w.Write(4); w.Write(v); }
        w.Write("GGUF"u8.ToArray()); w.Write(3); w.Write((ulong)0); w.Write((ulong)10);
        KvStr("general.architecture", arch);
        KvStr("general.name", name);
        KvU32("general.file_type", (uint)fileType);
        KvU32($"{arch}.context_length", 32768);
        KvU32($"{arch}.block_count", 48);
        KvU32($"{arch}.attention.head_count", 40);
        KvU32($"{arch}.attention.head_count_kv", 8);
        KvU32($"{arch}.embedding_length", 5120);
        // an array that must be skipped (tokenizer-like)
        Str("tokenizer.ggml.tokens"); w.Write(9); w.Write(8); w.Write((ulong)3); Str("a"); Str("bb"); Str("ccc");
        Str("tokenizer.ggml.scores"); w.Write(9); w.Write(6); w.Write((ulong)2); w.Write(1f); w.Write(2f);
        w.Write(new byte[1024]); // pretend tensor data
        return path;
    }

    [Fact]
    public void ReadsMetadataAndSkipsArrays()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var m = GgufReader.Read(WriteSyntheticGguf(dir));
        Assert.Equal("qwen2", m.Architecture);
        Assert.Equal("Q5_K_M", m.Quantization);
        Assert.Equal(32768, m.ContextLength);
        Assert.Equal(48, m.BlockCount);
        Assert.Equal(8, m.HeadCountKv);
        Assert.Equal(5120, m.EmbeddingLength);
    }

    [Fact]
    public void RejectsNonGguf()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "not a model");
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(f));
        Assert.False(GgufReader.HasMagic(f));
    }

    [Theory]
    [InlineData("Qwen2.5 Coder 14B", "Q5_K_M", "qwen2.5-coder-14b-q5km")]
    [InlineData("DeepSeek R1 14B Q4_K_M", "Q4_K_M", "deepseek-r1-14b-q4km")]
    [InlineData("", "Q8_0", "model-q80")]
    public void MakesStableApiIds(string display, string quant, string expected)
    {
        var id = ModelLibrary.MakeId(display, quant, "model.gguf");
        Assert.Equal(expected, id);
    }

    [Fact]
    public async Task LibraryImportRemoveVerifyRoundTrip()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var paths = new AppPaths(Path.Combine(root, "home")); paths.EnsureCreated();
        var src = WriteSyntheticGguf(Directory.CreateTempSubdirectory().FullName);
        var lib = new ModelLibrary(paths);

        var e = await lib.ImportAsync(src, copy: true);
        Assert.Equal("qwen2.5-coder-14b-q5km", e.Id);
        Assert.True(File.Exists(e.Path));
        Assert.True((await lib.VerifyAsync(e.Id)).Ok);

        Assert.Single(new ModelLibrary(paths).Models); // persisted
        lib.Rename(e.Id, "My Qwen");
        Assert.Equal("My Qwen", lib.Find(e.Id)!.DisplayName);
        Assert.Equal(e.Id, lib.Find(e.Id)!.Id); // id stays stable

        lib.Remove(e.Id);
        Assert.False(File.Exists(e.Path));
        Assert.Empty(lib.Models);
    }
}

public class PreflightAndConfigTests
{
    static ModelEntry Qwen14B() => new()
    {
        Id = "qwen", Path = "x.gguf", SizeBytes = (long)(10.8 * 1073741824), BlockCount = 48, HeadCount = 40, HeadCountKv = 8,
        EmbeddingLength = 5120, ContextLength = 32768, Quantization = "Q5_K_M"
    };

    [Fact]
    public void KvCacheMatchesFormula()
    {
        // 2 * 48 layers * 16384 ctx * 8 kv heads * 128 dim * 2 bytes
        var r = VramPreflight.Estimate(Qwen14B(), 16384, long.MaxValue, batch: 2048);
        Assert.Equal(48L * 16384 * 8 * 256 * 2, r.KvCacheBytes);
    }

    [Fact]
    public void FitsOn16GbAndRejectsOn8Gb()
    {
        Assert.True(VramPreflight.Estimate(Qwen14B(), 8192, 15L << 30).Safe);
        var tight = VramPreflight.Estimate(Qwen14B(), 16384, 13L << 30);
        Assert.False(tight.Safe);
        Assert.NotNull(tight.RecommendedContext);
        Assert.True(VramPreflight.Estimate(Qwen14B(), tight.RecommendedContext!.Value, 13L << 30).Safe);
    }

    [Fact]
    public void BalancedAutoReducesContextButMaximumOnlyWarns()
    {
        var m = Qwen14B();
        var s = new AppSettings { Profile = PerformanceProfile.Balanced };
        var plan = ConfigBuilder.Build(s, m, 12L << 30, 16);
        Assert.True(plan.Adjusted);
        Assert.True(plan.Config.Context < 16384);
        Assert.True(plan.Preflight.Safe);

        s.Profile = PerformanceProfile.Maximum;
        var max = ConfigBuilder.Build(s, m, 12L << 30, 16);
        Assert.Equal(32768, max.Config.Context);
        Assert.False(max.Preflight.Safe);
    }

    [Fact]
    public void ArgsUseInternalLoopbackPortAndAlias()
    {
        var plan = ConfigBuilder.Build(new AppSettings { InternalPort = 19000 }, Qwen14B(), 15L << 30, 16);
        var a = plan.Config.ToArgs();
        Assert.Equal("127.0.0.1", a[a.ToList().IndexOf("--host") + 1]);
        Assert.Equal("19000", a[a.ToList().IndexOf("--port") + 1]);
        Assert.Equal("qwen", a[a.ToList().IndexOf("--alias") + 1]);
    }

    [Fact]
    public void MaximumUsesQuantizedKvAndHalvesKvEstimate()
    {
        var m = Qwen14B();
        var max = ConfigBuilder.Build(new AppSettings { Profile = PerformanceProfile.Maximum }, m, 64L << 30, 16);
        Assert.Equal("q8_0", max.Config.KvCacheType);
        var args = max.Config.ToArgs().ToList();
        Assert.Equal("q8_0", args[args.IndexOf("-ctk") + 1]);
        Assert.Equal("q8_0", args[args.IndexOf("-ctv") + 1]);
        var f16 = VramPreflight.Estimate(m, 32768, long.MaxValue, kvBytesPerElement: 2.0).KvCacheBytes;
        Assert.True(max.Preflight.KvCacheBytes < f16 * 0.6);
        // q8_0 needs flash attention: Custom without FA falls back to f16
        var custom = ConfigBuilder.Build(new AppSettings { Profile = PerformanceProfile.Custom, FlashAttention = false, KvCacheType = "q8_0" }, m, 64L << 30, 16);
        Assert.Equal("f16", custom.Config.KvCacheType);
    }

    [Fact]
    public void ApiKeyAndDeviceArgsAreEmitted()
    {
        var plan = ConfigBuilder.Build(new AppSettings(), Qwen14B(), 15L << 30, 16, false, "Vulkan0");
        var a = (plan.Config with { ApiKey = "k123" }).ToArgs().ToList();
        Assert.Equal("Vulkan0", a[a.IndexOf("--device") + 1]);
        Assert.Equal("none", a[a.IndexOf("-sm") + 1]);
        Assert.Equal("k123", a[a.IndexOf("--api-key") + 1]);
    }

    [Fact]
    public void SafeModeIsConservative()
    {
        var plan = ConfigBuilder.Build(new AppSettings { Profile = PerformanceProfile.Maximum }, Qwen14B(), 15L << 30, 16, safeMode: true);
        Assert.Equal(4096, plan.Config.Context);
        Assert.False(plan.Config.FlashAttention);
        Assert.True(plan.Config.GpuLayers < ConfigBuilder.AutoLayers);
    }
}

public class MiscTests
{
    [Fact]
    public void ParsesLlamaDeviceList()
    {
        const string output = """
            load_backend: loaded Vulkan backend
            Available devices:
              Vulkan0: AMD Radeon RX 6900 XT (16368 MiB, 16100 MiB free)
              Vulkan1: AMD Radeon(TM) Graphics (2048 MiB, 2000 MiB free)
            """;
        var d = DeviceParser.Parse(output);
        Assert.Equal(2, d.Count);
        Assert.Equal("AMD Radeon RX 6900 XT", d[0].Name);
        Assert.Equal(16368, d[0].TotalMiB);
        Assert.True(d[0].IsVulkan);
    }

    [Fact]
    public void TranslatesGpuErrors()
    {
        var e = ErrorTranslator.Translate("ggml_vulkan: Device lost: VK_ERROR_DEVICE_LOST");
        Assert.Contains("GPU inference stopped", e.Title);
        Assert.Contains("Reduce Context", e.Actions);
        Assert.True(ErrorTranslator.IsGpuError("VK_ERROR_DEVICE_LOST"));
        Assert.Contains("fit in GPU memory", ErrorTranslator.Translate("vk::Device::allocateMemory: ErrorOutOfDeviceMemory").Title);
    }

    [Fact]
    public void OfflineModeBlocksExternalButNotLoopback()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName);
        var store = new SettingsStore(paths); store.Current.OfflineMode = true;
        var gate = new NetworkGate(store);
        Assert.Throws<InvalidOperationException>(() => gate.EnsureAllowed(new Uri("https://huggingface.co/x")));
        gate.EnsureAllowed(new Uri("http://127.0.0.1:8080/v1"));
    }

    [Fact]
    public void LanForcesAuthAndWildcardBind()
    {
        var s = new AppSettings();
        Assert.False(s.AuthRequired); Assert.Equal("127.0.0.1", s.BindAddress);
        s.LanAccess = true;
        Assert.True(s.AuthRequired); Assert.Equal("0.0.0.0", s.BindAddress);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory().FullName);
        var a = new SettingsStore(paths); a.Current.Port = 9999; a.Current.Profile = PerformanceProfile.Maximum; a.Save();
        var b = new SettingsStore(paths); b.Load();
        Assert.Equal(9999, b.Current.Port); Assert.Equal(PerformanceProfile.Maximum, b.Current.Profile);
    }

    [Fact]
    public void CatalogEntriesAllResolveToGgufUrls()
    {
        Assert.NotEmpty(ModelCatalog.Entries);
        foreach (var e in ModelCatalog.Entries)
            Assert.EndsWith(".gguf", ModelDownloader.ResolveUrl(e.Source).AbsolutePath);
    }

    [Fact]
    public void InternalPortDefaultsToRandom() => Assert.Equal(0, new AppSettings().InternalPort);

    [Fact]
    public void HuggingFaceShorthandResolves()
    {
        Assert.Equal("https://huggingface.co/o/r/resolve/main/m-Q4_K_M.gguf", ModelDownloader.ResolveUrl("o/r/m-Q4_K_M.gguf").ToString());
        Assert.Contains("/resolve/", ModelDownloader.ResolveUrl("https://huggingface.co/o/r/blob/main/m.gguf").ToString());
        Assert.Throws<ArgumentException>(() => ModelDownloader.ResolveUrl("nonsense"));
    }

    [Fact]
    public void AnalyticsStoresMetadataOnly()
    {
        var db = Path.Combine(Directory.CreateTempSubdirectory().FullName, "t.db");
        using var store = new AnalyticsStore(db);
        var tracker = new SessionTracker(store, null);
        tracker.Begin("m", "Q4_K_M", 8192, "gpu", "b1");
        tracker.OnRequest(new RequestRecord(DateTime.UtcNow, "m", "/v1/chat/completions", 100, 200, 0.3, 1500, 45, 5, 200, 50, 40));
        tracker.OnRequest(new RequestRecord(DateTime.UtcNow, "m", "/v1/chat/completions", 50, 100, 0.5, 1600, 47, 3, 200));
        var snap = tracker.Snapshot();
        Assert.Equal(300, snap.GeneratedTokens);
        Assert.Equal(150, snap.PromptTokens);
        Assert.Equal(0.4, snap.MedianTtft!.Value, 3);
        tracker.End();

        var s = Assert.Single(store.RecentSessions());
        Assert.Equal(300, s.GeneratedTokens);
        Assert.Equal(2, store.ApiTotalsAllTime().Requests);
    }

    [Fact]
    public void BenchmarkHistoryAndComparison()
    {
        var db = Path.Combine(Directory.CreateTempSubdirectory().FullName, "b.db");
        using var store = new AnalyticsStore(db);
        BenchmarkResult Make(double gen, double w) => new(0, DateTime.UtcNow, "m", "Q5_K_M", 16384, "gpu", "b1", 3, 128, 1024,
            gen, gen, gen - 1, gen + 1, 1800, 0.8, new TelemetrySummary(10, 98, 11L << 30, w, 63, 74, 2250, 2150), gen / w, null);
        store.InsertBenchmark(Make(45.2, 310));
        store.InsertBenchmark(Make(48.1, 286));
        var list = store.Benchmarks("m");
        Assert.Equal(2, list.Count);
        Assert.Equal(48.1, list[0].GenAvg, 3); // newest first
        var c = BenchmarkRunner.Compare(list[0], list[1]);
        Assert.Equal(6.4, c.GenDeltaPct, 1);
        Assert.Equal(-7.7, c.PowerDeltaPct!.Value, 1);
    }

    [Fact]
    public void MedianHandlesEvenAndEmpty()
    {
        Assert.Null(Stats.Median([]));
        Assert.Equal(2.5, Stats.Median([1, 2, 3, 4]));
    }
}
