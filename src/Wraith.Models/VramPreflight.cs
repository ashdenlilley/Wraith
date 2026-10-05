namespace Wraith.Models;

public enum PreflightStatus { Safe, VramLimited }

public sealed record PreflightResult(
    long WeightsBytes, long KvCacheBytes, long RuntimeBytes, long TotalBytes, long AvailableBytes,
    PreflightStatus Status, int ContextSize, int? RecommendedContext, int? RecommendedGpuLayers)
{
    public bool Safe => Status == PreflightStatus.Safe;
}

/// <summary>Estimates VRAM use before launching so we avoid configurations likely to exhaust memory.</summary>
public static class VramPreflight
{
    const long Overhead = 700L << 20;          // runtime context, driver
    const long BatchBuffersPerK = 96L << 20;   // compute buffers scale roughly with batch

    /// <param name="gpuLayerFraction">1.0 = everything on GPU.</param>
    /// <param name="kvBytesPerElement">2 = f16 KV cache, 1 = q8_0.</param>
    public static PreflightResult Estimate(
        ModelEntry m, int context, long availableBytes, double gpuLayerFraction = 1.0,
        int batch = 2048, double kvBytesPerElement = 2.0)
    {
        var weights = (long)(m.SizeBytes * gpuLayerFraction);
        var kv = (long)(KvBytesFull(m, context, kvBytesPerElement) * gpuLayerFraction);
        var runtime = Overhead + batch / 1024 * BatchBuffersPerK;
        var total = weights + kv + runtime;
        bool safe = total <= availableBytes;

        int? recCtx = null, recLayers = null;
        if (!safe)
        {
            // Progressive fallback per spec: reduce context first, then GPU layers.
            for (int c = context / 2; c >= 2048; c /= 2)
            {
                if (Estimate(m, c, long.MaxValue, gpuLayerFraction, batch, kvBytesPerElement).TotalBytes <= availableBytes)
                { recCtx = c; break; }
            }
            if (recCtx is null && m.BlockCount is { } layers && layers > 0)
            {
                int ctx = Math.Min(context, 4096);
                for (int l = layers; l >= 0; l--)
                {
                    var f = (double)l / layers;
                    if (Estimate(m, ctx, long.MaxValue, f, batch, kvBytesPerElement).TotalBytes <= availableBytes)
                    { recCtx = ctx; recLayers = l; break; }
                }
            }
        }
        return new(weights, kv, runtime, total, availableBytes, safe ? PreflightStatus.Safe : PreflightStatus.VramLimited,
            context, recCtx, recLayers);
    }

    static long KvBytesFull(ModelEntry m, int context, double bytesPerElement)
    {
        // 2 (K and V) * layers * ctx * kv_heads * head_dim * bytes
        if (m.BlockCount is not { } layers || layers <= 0)
            return (long)context * 128 * 1024; // unknown arch: assume ~128 KiB per token
        int heads = m.HeadCount ?? 0, kvHeads = m.HeadCountKv ?? heads;
        int keyLen = m.KeyLength ?? (heads > 0 && m.EmbeddingLength is { } e ? e / heads : 128);
        int valLen = m.ValueLength ?? keyLen;
        if (kvHeads <= 0) kvHeads = 8;
        return (long)((double)layers * context * kvHeads * (keyLen + valLen) * bytesPerElement);
    }
}
