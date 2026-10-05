using System.Net;
using System.Net.Http.Headers;
using RadeonLLM.Core;

namespace RadeonLLM.Models;

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
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadeonLLM/0.1");
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
        File.Move(part, final);
        return _library.RegisterManaged(final);
    }
}
