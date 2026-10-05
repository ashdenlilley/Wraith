using Wraith.Core;

namespace Wraith.Runtime;

/// <summary>Chooses llama.cpp builds for the detected hardware, in fallback order.</summary>
public static class BackendSelector
{
    /// <summary>NVIDIA driver "32.0.15.6094" means 560.94: last digit of the 3rd segment + the 4th segment.</summary>
    public static double? NvidiaDriverVersion(string? windowsDriverVersion)
    {
        var p = windowsDriverVersion?.Split('.');
        if (p is not { Length: 4 } || !int.TryParse(p[2], out var a) || !int.TryParse(p[3], out var b)) return null;
        var digits = (a % 10).ToString() + b.ToString("D4");
        return double.TryParse(digits.Insert(digits.Length - 2, "."), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>CUDA toolkit generation the driver can run: 13.x needs driver 580+, 12.x needs 525+.</summary>
    public static string? CudaVersionFor(double? nvidiaDriver) =>
        nvidiaDriver is { } d ? d >= 580 ? "13.4" : d >= 525 ? "12.4" : null : null;

    /// <summary>Ordered candidates for Auto. Explicit choices still get a CPU safety net.</summary>
    public static IReadOnlyList<RuntimeBackend> Candidates(RuntimeBackend requested, SystemInfo sys)
    {
        if (requested != RuntimeBackend.Auto)
            return requested == RuntimeBackend.Cpu ? [RuntimeBackend.Cpu] : [requested];

        var gpu = sys.PrimaryGpu;
        var list = new List<RuntimeBackend>();
        if (gpu?.Vendor == "NVIDIA" && CudaVersionFor(NvidiaDriverVersion(gpu.DriverVersion)) is not null) list.Add(RuntimeBackend.Cuda);
        if (gpu is not null && sys.VulkanLoaderPresent) list.Add(RuntimeBackend.Vulkan);
        list.Add(RuntimeBackend.Cpu);
        return list;
    }

    public static string Label(RuntimeBackend b) => b switch
    {
        RuntimeBackend.Cuda => "CUDA", RuntimeBackend.Rocm => "ROCm (HIP)", RuntimeBackend.Cpu => "CPU", RuntimeBackend.Vulkan => "Vulkan", _ => "Auto"
    };

    /// <summary>Does this probe result show the backend can actually see a device?</summary>
    public static bool Works(RuntimeBackend b, IReadOnlyList<RuntimeDevice> devices) => b switch
    {
        RuntimeBackend.Cpu => true,
        RuntimeBackend.Vulkan => devices.Any(d => d.Kind == DeviceKind.Vulkan),
        RuntimeBackend.Cuda => devices.Any(d => d.Kind == DeviceKind.Cuda),
        RuntimeBackend.Rocm => devices.Any(d => d.Kind == DeviceKind.Rocm),
        _ => devices.Count > 0
    };
}
