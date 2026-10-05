using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wraith.Core;

namespace Wraith.Runtime;

public sealed record RuntimeAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>One installable llama.cpp build. Extras are unpacked into the same folder (e.g. the CUDA runtime DLLs).</summary>
public sealed record RuntimeRelease(
    string Version, RuntimeBackend Backend, RuntimeAsset Main, IReadOnlyList<RuntimeAsset> Extras)
{
    public string AssetName => Main.Name;
    public long TotalSize => Main.Size + Extras.Sum(e => e.Size);
}

public sealed class RuntimeManifest
{
    public string Runtime { get; set; } = "llama.cpp";
    public string? Version { get; set; }
    public string Backend { get; set; } = "Vulkan";
    public string Architecture { get; set; } = "x64";
    public string? Sha256 { get; set; }
    public DateTime InstalledUtc { get; set; }
    /// <summary>Folder name ("b11400-vulkan") of the previously active build, kept on disk for rollback.</summary>
    public string? Previous { get; set; }

    public RuntimeBackend BackendKind => Enum.TryParse<RuntimeBackend>(Backend, true, out var b) ? b : RuntimeBackend.Vulkan;
}

public sealed record RuntimeVerification(bool Ok, string Message, string? ReportedVersion);

/// <summary>Owns the llama.cpp installation: install, update, switch backend, rollback, repair, remove, verify.</summary>
public sealed class RuntimeManager
{
    const string ReleasesUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    readonly AppPaths _paths;
    readonly LogChannel _log;
    readonly NetworkGate _gate;
    readonly HttpClient _http;

    public RuntimeManager(AppPaths paths, LogChannel log, NetworkGate gate, HttpClient? http = null)
    {
        _paths = paths; _log = log; _gate = gate;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wraith/0.1");
    }

    string ManifestPath => Path.Combine(_paths.Runtime, "manifest.json");

    public static string DirName(string version, RuntimeBackend backend) => $"{version}-{backend.ToString().ToLowerInvariant()}";

    string BuildDir(string version, RuntimeBackend backend)
    {
        var dir = Path.Combine(_paths.Runtime, DirName(version, backend));
        // v0.1 installs used a bare version folder (always Vulkan).
        var legacy = Path.Combine(_paths.Runtime, version);
        return !Directory.Exists(dir) && backend == RuntimeBackend.Vulkan && Directory.Exists(legacy) ? legacy : dir;
    }

    public RuntimeManifest? Manifest
    {
        get
        {
            try { return File.Exists(ManifestPath) ? JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(ManifestPath), Json) : null; }
            catch { return null; }
        }
    }

    public string? Version => Manifest?.Version;
    public RuntimeBackend? ActiveBackend => Manifest is { Version: not null } m ? m.BackendKind : null;

    /// <summary>Full path to the active llama-server.exe, or null if not installed.</summary>
    public string? ServerExe => Manifest is { Version: { } v } m ? FindServer(BuildDir(v, m.BackendKind)) : null;

    public bool IsInstalled => ServerExe is not null;

    /// <summary>Versions on disk for a backend, newest first.</summary>
    public IReadOnlyList<string> InstalledBuilds(RuntimeBackend backend) =>
        !Directory.Exists(_paths.Runtime) ? [] :
        Directory.GetDirectories(_paths.Runtime).Select(d => ParseDirName(Path.GetFileName(d)))
            .Where(x => x is { } t && t.backend == backend && FindServer(BuildDir(t.version, t.backend)) is not null)
            .Select(x => x!.Value.version).OrderDescending().ToList();

    public bool HasBuild(string version, RuntimeBackend backend) => FindServer(BuildDir(version, backend)) is not null;

    /// <summary>Make an already-downloaded build the active one. Returns false if it is not on disk.</summary>
    public bool Activate(string version, RuntimeBackend backend)
    {
        if (!HasBuild(version, backend)) return false;
        var old = Manifest;
        Save(new RuntimeManifest
        {
            Version = version, Backend = backend.ToString(), InstalledUtc = DateTime.UtcNow,
            Previous = old?.Version is { } ov ? DirName(ov, old.BackendKind) : old?.Previous
        });
        return true;
    }

    static string? FindServer(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    // ---- discovery ----

    /// <summary>
    /// Newest build with an asset for the backend. The "latest" API endpoint points at a pinned non-build tag,
    /// and build releases are flagged prerelease, so scan the recent release list instead.
    /// </summary>
    public async Task<RuntimeRelease> GetLatestAsync(RuntimeBackend backend = RuntimeBackend.Vulkan, string? cudaVersion = null, CancellationToken ct = default)
    {
        var url = ReleasesUrl + "?per_page=15";
        _gate.EnsureAllowed(new Uri(url));
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        foreach (var rel in doc.RootElement.EnumerateArray())
            if (!(rel.TryGetProperty("draft", out var d) && d.GetBoolean()) && ParseRelease(rel, backend, cudaVersion) is { } r) return r;
        throw new InvalidOperationException($"No recent llama.cpp release has a Windows x64 {BackendSelector.Label(backend)} build.");
    }

    public async Task<RuntimeRelease> GetTaggedAsync(string tag, RuntimeBackend backend, string? cudaVersion = null, CancellationToken ct = default)
    {
        var url = $"{ReleasesUrl}/tags/{tag}";
        _gate.EnsureAllowed(new Uri(url));
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        return ParseRelease(doc.RootElement, backend, cudaVersion)
            ?? throw new InvalidOperationException($"Release {tag} has no Windows x64 {BackendSelector.Label(backend)} build.");
    }

    public static RuntimeRelease? ParseRelease(JsonElement root, RuntimeBackend backend, string? cudaVersion = null)
    {
        var tag = root.GetProperty("tag_name").GetString()!;
        var assets = root.GetProperty("assets").EnumerateArray().Select(a =>
        {
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:")) sha = ds[7..];
            return new RuntimeAsset(a.GetProperty("name").GetString()!, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
        }).ToList();

        RuntimeAsset? Find(Func<string, bool> pred) => assets.FirstOrDefault(a => pred(a.Name));
        bool Llama(string n, string suffix) =>
            n.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        switch (backend)
        {
            case RuntimeBackend.Vulkan:
            case RuntimeBackend.Auto:
                return Find(n => Llama(n, "-bin-win-vulkan-x64.zip")) is { } v ? new(tag, RuntimeBackend.Vulkan, v, []) : null;
            case RuntimeBackend.Cpu:
                return Find(n => Llama(n, "-bin-win-cpu-x64.zip")) is { } c ? new(tag, RuntimeBackend.Cpu, c, []) : null;
            case RuntimeBackend.Rocm:
                return Find(n => n.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(n, @"-bin-win-rocm-[\d.]+-x64\.zip$")) is { } r
                    ? new(tag, RuntimeBackend.Rocm, r, []) : null;
            case RuntimeBackend.Cuda:
                // Newest CUDA the driver supports first; older CUDA runs on newer drivers, never the reverse.
                var cap = double.TryParse(cudaVersion, System.Globalization.CultureInfo.InvariantCulture, out var cv) ? cv : double.MaxValue;
                foreach (var ver in new[] { "13.4", "12.4" }.Where(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture) <= cap))
                {
                    var main = Find(n => Llama(n, $"-bin-win-cuda-{ver}-x64.zip"));
                    var rt = Find(n => n.StartsWith("cudart-", StringComparison.OrdinalIgnoreCase) && n.EndsWith($"-cuda-{ver}-x64.zip", StringComparison.OrdinalIgnoreCase));
                    if (main is not null && rt is not null) return new(tag, RuntimeBackend.Cuda, main, [rt]);
                }
                return null;
        }
        return null;
    }

    /// <summary>Returns the newer release if one exists and was not ignored; never installs automatically.</summary>
    public async Task<RuntimeRelease?> CheckForUpdateAsync(string? ignoredVersion, CancellationToken ct = default)
    {
        var m = Manifest;
        var backend = m?.BackendKind ?? RuntimeBackend.Vulkan;
        var cuda = backend == RuntimeBackend.Cuda ? ExistingCudaVersion() : null;
        var latest = await GetLatestAsync(backend, cuda, ct);
        if (latest.Version == m?.Version || latest.Version == ignoredVersion) return null;
        return latest;
    }

    string? ExistingCudaVersion()
    {
        var exeDir = Path.GetDirectoryName(ServerExe);
        if (exeDir is null) return null;
        var ver = Directory.EnumerateFiles(exeDir, "cudart64_*.dll").Select(Path.GetFileName).FirstOrDefault();
        return ver is null ? null : ver.Contains("_13") ? "13.4" : "12.4";
    }

    // ---- install ----

    public async Task InstallAsync(RuntimeRelease rel, IProgress<(string stage, double fraction)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.Runtime);
        Directory.CreateDirectory(_paths.Cache);
        var all = new[] { rel.Main }.Concat(rel.Extras).ToList();
        foreach (var a in all) _gate.EnsureAllowed(new Uri(a.Url));

        var dir = Path.Combine(_paths.Runtime, DirName(rel.Version, rel.Backend));
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);

        long total = all.Sum(a => a.Size), done = 0;
        string? mainHash = null;
        foreach (var asset in all)
        {
            var zip = Path.Combine(_paths.Cache, asset.Name);
            _log.Info($"Downloading runtime asset {asset.Name} ({rel.Version}, {rel.Backend})");
            using (var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(("Downloading inference runtime...", (double)done / total * 0.8));
                }
            }

            progress?.Report(("Verifying...", 0.82));
            var hash = await Sha256Async(zip, ct);
            if (asset.Sha256 is not null && !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new InvalidDataException($"{asset.Name} failed integrity check (SHA-256 mismatch).");
            }
            if (asset == rel.Main) mainHash = hash;

            progress?.Report(("Extracting...", 0.88));
            ZipFile.ExtractToDirectory(zip, tmp, overwriteFiles: true);
            File.Delete(zip);
        }

        if (FindServer(tmp) is null)
        {
            Directory.Delete(tmp, true);
            throw new InvalidDataException("Archive does not contain llama-server.exe.");
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);

        var old = Manifest;
        var oldDir = old?.Version is { } ov ? DirName(ov, old.BackendKind) : old?.Previous;
        Save(new RuntimeManifest
        {
            Version = rel.Version, Backend = rel.Backend.ToString(), Sha256 = mainHash, InstalledUtc = DateTime.UtcNow,
            Previous = oldDir == DirName(rel.Version, rel.Backend) ? old?.Previous : oldDir
        });
        _log.Info($"Runtime {rel.Version} ({rel.Backend}) installed.");
        progress?.Report(("Runtime ready.", 1));
    }

    static (string version, RuntimeBackend backend)? ParseDirName(string? dir)
    {
        if (dir is null) return null;
        var i = dir.LastIndexOf('-');
        if (i < 0) return (dir, RuntimeBackend.Vulkan);
        return Enum.TryParse<RuntimeBackend>(dir[(i + 1)..], true, out var b) ? (dir[..i], b) : (dir, RuntimeBackend.Vulkan);
    }

    public bool CanRollback => ParseDirName(Manifest?.Previous) is { } p && HasBuild(p.version, p.backend);

    public void Rollback()
    {
        var m = Manifest ?? throw new InvalidOperationException("No runtime installed.");
        if (ParseDirName(m.Previous) is not { } p || !HasBuild(p.version, p.backend))
            throw new InvalidOperationException("No previous runtime to roll back to.");
        Save(new RuntimeManifest
        {
            Version = p.version, Backend = p.backend.ToString(), InstalledUtc = DateTime.UtcNow,
            Previous = m.Version is { } v ? DirName(v, m.BackendKind) : null
        });
        _log.Info($"Rolled back to {p.version} ({p.backend}).");
    }

    /// <summary>Reinstalls the pinned version and backend from its release tag.</summary>
    public async Task RepairAsync(IProgress<(string, double)>? progress = null, CancellationToken ct = default)
    {
        var m = Manifest;
        var v = m?.Version ?? throw new InvalidOperationException("No runtime installed.");
        await InstallAsync(await GetTaggedAsync(v, m!.BackendKind, ExistingCudaVersion(), ct), progress, ct);
    }

    public void Remove()
    {
        if (Directory.Exists(_paths.Runtime)) Directory.Delete(_paths.Runtime, true);
        Directory.CreateDirectory(_paths.Runtime);
        _log.Info("Runtime removed.");
    }

    // ---- verify / probe ----

    public async Task<RuntimeVerification> VerifyAsync(CancellationToken ct = default)
    {
        var exe = ServerExe;
        if (exe is null) return new(false, "Runtime not installed.", null);
        try
        {
            var (code, output) = await RunAsync(exe, "--version", TimeSpan.FromSeconds(30), ct);
            var ver = output.Split('\n').FirstOrDefault(l => l.Contains("version", StringComparison.OrdinalIgnoreCase))?.Trim();
            return code == 0 || ver is not null
                ? new(true, "Runtime verified.", ver)
                : new(false, $"llama-server exited with code {code}.", null);
        }
        catch (Exception ex) { return new(false, ex.Message, null); }
    }

    /// <summary>Runs --list-devices: the definitive "does this backend work with this GPU" test.</summary>
    public async Task<IReadOnlyList<RuntimeDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var exe = ServerExe ?? throw new InvalidOperationException("Runtime not installed.");
        var (_, output) = await RunAsync(exe, "--list-devices", TimeSpan.FromSeconds(60), ct);
        return DeviceParser.Parse(output);
    }

    static async Task<(int code, string output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var o = p.StandardOutput.ReadToEndAsync(cts.Token);
        var e = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o + "\n" + await e);
    }

    // ---- helpers ----

    void Save(RuntimeManifest m)
    {
        Directory.CreateDirectory(_paths.Runtime);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m, Json));
    }

    static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var fs = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }
}
