using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadeonLLM.Core;

namespace RadeonLLM.Runtime;

public sealed record RuntimeRelease(string Version, string AssetName, string DownloadUrl, long Size, string? Sha256);

public sealed class RuntimeManifest
{
    public string Runtime { get; set; } = "llama.cpp";
    public string? Version { get; set; }
    public string Backend { get; set; } = "Vulkan";
    public string Architecture { get; set; } = "x64";
    public string? Sha256 { get; set; }
    public DateTime InstalledUtc { get; set; }
    /// <summary>Previously active version, kept on disk for rollback.</summary>
    public string? Previous { get; set; }
}

public sealed record RuntimeVerification(bool Ok, string Message, string? ReportedVersion);

/// <summary>Owns the llama.cpp installation: install, update, rollback, repair, remove, verify.</summary>
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
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadeonLLM/0.1");
    }

    string ManifestPath => Path.Combine(_paths.Runtime, "manifest.json");
    string VersionDir(string v) => Path.Combine(_paths.Runtime, v);

    public RuntimeManifest? Manifest
    {
        get
        {
            try { return File.Exists(ManifestPath) ? JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(ManifestPath), Json) : null; }
            catch { return null; }
        }
    }

    public string? Version => Manifest?.Version;

    /// <summary>Full path to the active llama-server.exe, or null if not installed.</summary>
    public string? ServerExe
    {
        get
        {
            var v = Version;
            return v is null ? null : FindServer(VersionDir(v));
        }
    }

    public bool IsInstalled => ServerExe is not null;

    public IReadOnlyList<string> InstalledVersions =>
        Directory.Exists(_paths.Runtime)
            ? Directory.GetDirectories(_paths.Runtime).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderDescending().ToList()
            : [];

    static string? FindServer(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    // ---- discovery ----

    /// <summary>
    /// Newest build with a Windows x64 Vulkan zip. The "latest" API endpoint points at a pinned non-build tag,
    /// and build releases are flagged prerelease, so scan the recent release list instead.
    /// </summary>
    public async Task<RuntimeRelease> GetLatestAsync(CancellationToken ct = default)
    {
        var url = ReleasesUrl + "?per_page=15";
        _gate.EnsureAllowed(new Uri(url));
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        foreach (var rel in doc.RootElement.EnumerateArray())
            if (!(rel.TryGetProperty("draft", out var d) && d.GetBoolean()) && ParseRelease(rel) is { } r) return r;
        throw new InvalidOperationException("No recent llama.cpp release has a Windows x64 Vulkan build.");
    }

    public async Task<RuntimeRelease> GetTaggedAsync(string tag, CancellationToken ct = default)
    {
        var url = $"{ReleasesUrl}/tags/{tag}";
        _gate.EnsureAllowed(new Uri(url));
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        return ParseRelease(doc.RootElement) ?? throw new InvalidOperationException($"Release {tag} has no Windows x64 Vulkan build.");
    }

    static RuntimeRelease? ParseRelease(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString()!;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString()!;
            if (!name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith("-bin-win-vulkan-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:"))
                sha = ds[7..];
            return new RuntimeRelease(tag, name, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// <summary>Returns the newer release if one exists and was not ignored; never installs automatically.</summary>
    public async Task<RuntimeRelease?> CheckForUpdateAsync(string? ignoredVersion, CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(ct);
        if (latest.Version == Version || latest.Version == ignoredVersion) return null;
        return latest;
    }

    // ---- install ----

    public async Task InstallAsync(RuntimeRelease rel, IProgress<(string stage, double fraction)>? progress = null, CancellationToken ct = default)
    {
        _gate.EnsureAllowed(new Uri(rel.DownloadUrl));
        Directory.CreateDirectory(_paths.Runtime);
        Directory.CreateDirectory(_paths.Cache);
        var zip = Path.Combine(_paths.Cache, rel.AssetName);
        _log.Info($"Downloading runtime {rel.Version} ({rel.AssetName})");

        using (var resp = await _http.GetAsync(rel.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? rel.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(("Downloading inference runtime...", (double)done / total * 0.8));
            }
        }

        progress?.Report(("Verifying...", 0.82));
        var hash = await Sha256Async(zip, ct);
        if (rel.Sha256 is not null && !hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidDataException("Runtime download failed integrity check (SHA-256 mismatch).");
        }

        progress?.Report(("Extracting...", 0.88));
        var dir = VersionDir(rel.Version);
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);
        if (FindServer(tmp) is null)
        {
            Directory.Delete(tmp, true);
            throw new InvalidDataException("Archive does not contain llama-server.exe.");
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        File.Delete(zip);

        var old = Manifest;
        Save(new RuntimeManifest
        {
            Version = rel.Version, Sha256 = hash, InstalledUtc = DateTime.UtcNow,
            Previous = old?.Version is { } ov && ov != rel.Version ? ov : old?.Previous
        });
        _log.Info($"Runtime {rel.Version} installed.");
        progress?.Report(("Runtime ready.", 1));
    }

    public bool CanRollback => Manifest?.Previous is { } p && FindServer(VersionDir(p)) is not null;

    public void Rollback()
    {
        var m = Manifest ?? throw new InvalidOperationException("No runtime installed.");
        if (m.Previous is null || FindServer(VersionDir(m.Previous)) is null)
            throw new InvalidOperationException("No previous runtime to roll back to.");
        Save(new RuntimeManifest { Version = m.Previous, InstalledUtc = DateTime.UtcNow, Previous = m.Version });
        _log.Info($"Rolled back to {m.Previous}.");
    }

    /// <summary>Reinstalls the pinned version from its release tag.</summary>
    public async Task RepairAsync(IProgress<(string, double)>? progress = null, CancellationToken ct = default)
    {
        var v = Version ?? throw new InvalidOperationException("No runtime installed.");
        await InstallAsync(await GetTaggedAsync(v, ct), progress, ct);
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

    /// <summary>Runs --list-devices: the definitive "does Vulkan work with this GPU" test.</summary>
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
