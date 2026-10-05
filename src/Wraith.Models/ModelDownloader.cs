using System.Net;
using System.Net.Http.Headers;
using Wraith.Core;

namespace Wraith.Models;

/// <summary>Resumable GGUF downloader. Accepts a full URL or a Hugging Face "owner/repo/file.gguf" shorthand.</summary>
public sealed class ModelDownloader
{
    readonly AppPaths _paths;
    readonly NetworkGate _gate;
    readonly ModelLibrary _library;
    readonly LogChannel _log;
    readonly HttpClient _http;

    public ModelDownloader(AppPaths paths, NetworkGate gate, ModelLibrary library, LogChannel log, HttpClient? http = null)
    {
        _paths = paths; _gate = gate; _library = library; _log = log;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wraith/0.1");
    }

    public static Uri ResolveUrl(string input)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https"))
            return u.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Contains("/blob/")
                ? new Uri(input.Replace("/blob/", "/resolve/")) : u;
        var parts = input.Split('/', 3);
        if (parts.Length == 3 && parts[2].EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://huggingface.co/{parts[0]}/{parts[1]}/resolve/main/{parts[2]}");
        throw new ArgumentException("Enter a URL or owner/repo/file.gguf.");
    }

    /// <summary>Hugging Face exposes the LFS sha256 in X-Linked-ETag on the first (pre-redirect) response.</summary>
    public async Task<string?> FetchExpectedSha256Async(Uri url, CancellationToken ct)
    {
        if (!url.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("Wraith/0.1");
            using var resp = await noRedirect.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            if (resp.Headers.TryGetValues("X-Linked-ETag", out var v))
            {
                var etag = v.First().Trim('"', ' ');
                if (etag.Length == 64 && etag.All(Uri.IsHexDigit)) return etag.ToLowerInvariant();
            }
        }
        catch (HttpRequestException) { }
        return null;
    }

    public async Task<ModelEntry> DownloadAsync(string input, IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        var url = ResolveUrl(input);
        _gate.EnsureAllowed(url);
        var name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("URL must point to a .gguf file.");
        Directory.CreateDirectory(_paths.Models);
        var final = Path.Combine(_paths.Models, name);
        var part = final + ".part";
        if (File.Exists(final)) throw new IOException("A model file with that name already exists.");

        var expectedSha = await FetchExpectedSha256Async(url, ct);
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); existing = 0; }
        else resp.EnsureSuccessStatusCode();

        bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long total = (resp.Content.Headers.ContentLength ?? 0) + existing;
        _log.Info($"Downloading {name} ({Fmt.Bytes(total)}){(resumed ? $", resuming at {Fmt.Bytes(existing)}" : "")}");

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[1 << 20]; long done = existing; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report((done, total));
            }
        }
        if (!GgufReader.HasMagic(part)) { File.Delete(part); throw new InvalidDataException("Downloaded file is not a GGUF."); }
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (expectedSha is not null && hash != expectedSha)
        {
            File.Delete(part);
            throw new InvalidDataException("Download failed integrity check (SHA-256 does not match the published value). Try again.");
        }
        _log.Info(expectedSha is null ? $"{name}: no published checksum to compare; recorded sha256 {hash[..12]}..." : $"{name}: sha256 verified.");
        File.Move(part, final);
        return _library.RegisterManaged(final, hash);
    }
}
