using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RadeonLLM.Core;

namespace RadeonLLM.Models;

public sealed class ModelEntry
{
    /// <summary>Stable API identifier, e.g. qwen2.5-coder-14b-q5km.</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Absolute path to the GGUF file.</summary>
    public string Path { get; set; } = "";
    /// <summary>True when the file lives in the managed models folder (deleted on Remove).</summary>
    public bool Managed { get; set; }
    public string? Quantization { get; set; }
    public long SizeBytes { get; set; }
    public string? Architecture { get; set; }
    public long? ContextLength { get; set; }
    public int? BlockCount { get; set; }
    public int? HeadCount { get; set; }
    public int? HeadCountKv { get; set; }
    public int? EmbeddingLength { get; set; }
    public int? KeyLength { get; set; }
    public int? ValueLength { get; set; }
    public string? Sha256 { get; set; }
    public DateTime AddedUtc { get; set; }
}

public sealed record ModelVerification(bool Ok, string Message);

public sealed partial class ModelLibrary
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly AppPaths _paths;
    readonly object _lock = new();
    List<ModelEntry> _models = new();

    public event Action? Changed;

    public ModelLibrary(AppPaths paths)
    {
        _paths = paths;
        Load();
    }

    public IReadOnlyList<ModelEntry> Models { get { lock (_lock) return _models.ToList(); } }
    public ModelEntry? Find(string? id) => id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    void Load()
    {
        try
        {
            if (File.Exists(_paths.ModelsFile))
                _models = JsonSerializer.Deserialize<List<ModelEntry>>(File.ReadAllText(_paths.ModelsFile), Json) ?? new();
        }
        catch { _models = new(); }
    }

    void Save()
    {
        Directory.CreateDirectory(_paths.Config);
        File.WriteAllText(_paths.ModelsFile, JsonSerializer.Serialize(_models, Json));
        Changed?.Invoke();
    }

    /// <summary>Adds a GGUF. copy=true copies it into the managed folder; false references it in place.</summary>
    public async Task<ModelEntry> ImportAsync(string source, bool copy, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Model file not found.", source);
        var meta = GgufReader.Read(source); // throws on non-GGUF

        var path = source; var managed = false;
        if (copy)
        {
            Directory.CreateDirectory(_paths.Models);
            var dest = System.IO.Path.Combine(_paths.Models, System.IO.Path.GetFileName(source));
            if (!string.Equals(System.IO.Path.GetFullPath(dest), System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) throw new IOException("A model file with that name already exists.");
                await CopyAsync(source, dest, progress, ct);
            }
            path = dest; managed = true;
        }
        return Register(path, managed, meta);
    }

    /// <summary>Registers a GGUF already in the managed folder (used after download).</summary>
    public ModelEntry RegisterManaged(string path) => Register(path, true, GgufReader.Read(path));

    ModelEntry Register(string path, bool managed, GgufMetadata meta)
    {
        var fi = new FileInfo(path);
        var quant = meta.Quantization ?? QuantFromName(fi.Name);
        var display = !string.IsNullOrWhiteSpace(meta.Name) ? meta.Name! : System.IO.Path.GetFileNameWithoutExtension(fi.Name);
        var entry = new ModelEntry
        {
            DisplayName = display, Path = path, Managed = managed, Quantization = quant, SizeBytes = fi.Length,
            Architecture = meta.Architecture, ContextLength = meta.ContextLength, BlockCount = meta.BlockCount,
            HeadCount = meta.HeadCount, HeadCountKv = meta.HeadCountKv, EmbeddingLength = meta.EmbeddingLength,
            KeyLength = meta.KeyLength, ValueLength = meta.ValueLength, AddedUtc = DateTime.UtcNow
        };
        lock (_lock)
        {
            var existing = _models.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            entry.Id = UniqueId(MakeId(display, quant, fi.Name));
            _models.Add(entry);
            Save();
        }
        return entry;
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var m = _models.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            _models.Remove(m);
            if (m.Managed && File.Exists(m.Path)) File.Delete(m.Path);
            Save();
        }
    }

    /// <summary>Renames the display name only; the API id stays stable so clients keep working.</summary>
    public void Rename(string id, string displayName)
    {
        lock (_lock)
        {
            var m = _models.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            m.DisplayName = displayName.Trim();
            Save();
        }
    }

    public async Task<ModelVerification> VerifyAsync(string id, CancellationToken ct = default)
    {
        var m = Find(id);
        if (m is null) return new(false, "Model not found.");
        if (!File.Exists(m.Path)) return new(false, "File is missing.");
        var fi = new FileInfo(m.Path);
        if (fi.Length != m.SizeBytes) return new(false, $"Size changed ({Fmt.Bytes(m.SizeBytes)} to {Fmt.Bytes(fi.Length)}); file may be incomplete.");
        try { GgufReader.Read(m.Path); } catch (Exception ex) { return new(false, "Invalid GGUF header: " + ex.Message); }
        await using var fs = File.OpenRead(m.Path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        lock (_lock)
        {
            if (m.Sha256 is null) { m.Sha256 = hash; Save(); }
            else if (m.Sha256 != hash) return new(false, "SHA-256 differs from the value recorded at first verification.");
        }
        return new(true, "Model verified.");
    }

    static async Task CopyAsync(string src, string dst, IProgress<double>? progress, CancellationToken ct)
    {
        await using var i = File.OpenRead(src);
        await using var o = File.Create(dst);
        var buf = new byte[1 << 20]; long done = 0; int n;
        while ((n = await i.ReadAsync(buf, ct)) > 0)
        {
            await o.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            progress?.Report((double)done / i.Length);
        }
    }

    string UniqueId(string id)
    {
        var candidate = id; int n = 2;
        while (_models.Any(m => m.Id == candidate)) candidate = $"{id}-{n++}";
        return candidate;
    }

    [GeneratedRegex(@"(?i)(?<![a-z0-9])((?:IQ|Q)\d(?:_[A-Z0-9]+)*|F16|F32|BF16)(?![a-z0-9])")]
    private static partial Regex QuantRegex();

    public static string? QuantFromName(string fileName) =>
        QuantRegex().Match(fileName) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

    /// <summary>qwen2.5-coder-14b-q5km style slug.</summary>
    public static string MakeId(string display, string? quant, string fileName)
    {
        var baseName = string.IsNullOrWhiteSpace(display) ? System.IO.Path.GetFileNameWithoutExtension(fileName) : display;
        baseName = QuantRegex().Replace(baseName, "");
        var slug = Regex.Replace(baseName.ToLowerInvariant(), @"[^a-z0-9.]+", "-").Trim('-');
        if (slug.Length == 0) slug = "model";
        var q = quant?.Replace("_", "").ToLowerInvariant();
        return string.IsNullOrEmpty(q) ? slug : $"{slug}-{q}";
    }
}
