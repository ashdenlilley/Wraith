using System.Text;

namespace Wraith.Models;

public sealed record GgufMetadata(
    int Version, string? Architecture, string? Name, int? FileType, long? ContextLength,
    int? BlockCount, int? HeadCount, int? HeadCountKv, int? EmbeddingLength, int? KeyLength, int? ValueLength)
{
    public string? Quantization => GgufReader.QuantName(FileType);
}

/// <summary>Reads the GGUF header key/values without loading tensors.</summary>
public static class GgufReader
{
    static readonly Dictionary<int, string> Quants = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1",
        [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M", [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M",
        [16] = "Q5_K_S", [17] = "Q5_K_M", [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S",
        [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S", [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M",
        [28] = "IQ2_S", [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M", [32] = "BF16",
    };

    public static string? QuantName(int? t) => t is { } v && Quants.TryGetValue(v, out var n) ? n : null;

    public static bool HasMagic(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b.SequenceEqual("GGUF"u8);
        }
        catch { return false; }
    }

    public static GgufMetadata Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("GGUF"u8))
            throw new InvalidDataException("Not a GGUF file.");
        int version = r.ReadInt32();
        if (version is < 2 or > 3) throw new InvalidDataException($"Unsupported GGUF version {version}.");
        r.ReadUInt64(); // tensor count
        ulong kvCount = r.ReadUInt64();

        var kv = new Dictionary<string, object>();
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(r);
            var type = r.ReadInt32();
            var wanted = key.StartsWith("general.") || key.Contains(".context_length") || key.Contains(".block_count")
                || key.Contains(".attention.head_count") || key.Contains(".embedding_length")
                || key.Contains(".attention.key_length") || key.Contains(".attention.value_length");
            if (wanted && type != 9) kv[key] = ReadValue(r, type);
            else Skip(r, type);
        }

        string? arch = kv.GetValueOrDefault("general.architecture") as string;
        long? L(string suffix) => arch is not null && kv.TryGetValue($"{arch}.{suffix}", out var v) ? Convert.ToInt64(v) : null;
        int? I(string suffix) => L(suffix) is { } l ? (int)l : null;
        int? ft = kv.TryGetValue("general.file_type", out var f) ? Convert.ToInt32(f) : null;

        return new GgufMetadata(version, arch, kv.GetValueOrDefault("general.name") as string, ft,
            L("context_length"), I("block_count"), I("attention.head_count"),
            I("attention.head_count_kv") ?? I("attention.head_count"), I("embedding_length"),
            I("attention.key_length"), I("attention.value_length"));
    }

    static string ReadString(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > 1 << 24) throw new InvalidDataException("Corrupt GGUF string length.");
        return Encoding.UTF8.GetString(r.ReadBytes((int)len));
    }

    static object ReadValue(BinaryReader r, int type) => type switch
    {
        0 => (long)r.ReadByte(), 1 => (long)r.ReadSByte(), 2 => (long)r.ReadUInt16(), 3 => (long)r.ReadInt16(),
        4 => (long)r.ReadUInt32(), 5 => (long)r.ReadInt32(), 6 => (double)r.ReadSingle(), 7 => r.ReadByte() != 0,
        8 => ReadString(r), 10 => (long)r.ReadUInt64(), 11 => r.ReadInt64(), 12 => r.ReadDouble(),
        _ => throw new InvalidDataException($"Unknown GGUF value type {type}.")
    };

    static void Skip(BinaryReader r, int type)
    {
        switch (type)
        {
            case 0 or 1 or 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2 or 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4 or 5 or 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10 or 11 or 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8: r.BaseStream.Seek((long)r.ReadUInt64(), SeekOrigin.Current); break;
            case 9:
                var et = r.ReadInt32();
                var n = r.ReadUInt64();
                int fixedSize = et switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
                if (fixedSize > 0) r.BaseStream.Seek((long)n * fixedSize, SeekOrigin.Current);
                else for (ulong i = 0; i < n; i++) Skip(r, et);
                break;
            default: throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }
}
