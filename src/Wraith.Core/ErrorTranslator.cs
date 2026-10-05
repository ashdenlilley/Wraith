namespace Wraith.Core;

public sealed record FriendlyError(string Title, string[] Causes, string[] Actions, string Raw);

/// <summary>Maps raw runtime errors to human-readable guidance (spec section 34).</summary>
public static class ErrorTranslator
{
    public static bool IsGpuError(string raw) =>
        raw.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("vk::Queue::submit", StringComparison.OrdinalIgnoreCase);

    public static bool IsOutOfMemory(string raw) =>
        raw.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase);

    public static FriendlyError Translate(string raw)
    {
        if (IsOutOfMemory(raw))
            return new("The model did not fit in GPU memory.",
                ["Insufficient VRAM", "Context size too large"],
                ["Reduce Context", "Reduce GPU Offload", "Open Diagnostics"], raw);
        if (IsGpuError(raw))
            return new("GPU inference stopped unexpectedly.",
                ["GPU driver reset", "Unstable GPU overclock", "Insufficient VRAM", "Vulkan runtime error"],
                ["Retry", "Reduce Context", "Reduce GPU Offload", "Open Diagnostics"], raw);
        if (raw.Contains("failed to load model", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase))
            return new("The model could not be loaded.",
                ["Corrupt or incomplete GGUF file", "Model architecture newer than the installed runtime"],
                ["Verify Model", "Update Runtime", "Open Diagnostics"], raw);
        if (raw.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("couldn't bind", StringComparison.OrdinalIgnoreCase))
            return new("The server port is already in use.",
                ["Another program is using the port"], ["Change Port in Settings"], raw);
        return new("The inference server stopped unexpectedly.",
            ["Unknown runtime error"], ["Retry", "Open Diagnostics"], raw);
    }
}
