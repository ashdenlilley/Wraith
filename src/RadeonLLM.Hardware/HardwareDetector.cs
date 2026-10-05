using System.Runtime.InteropServices;
using Microsoft.Win32;
using RadeonLLM.Core;

namespace RadeonLLM.Hardware;

public static class HardwareDetector
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static SystemInfo Detect()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                if (!int.TryParse(name, out _)) continue;
                using var k = cls!.OpenSubKey(name);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase) && desc.Contains("Display")) continue;
                long vram = k!.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    int i => (uint)i,
                    _ => 0
                };
                gpus.Add(new GpuInfo(desc, Vendor(desc), vram, k.GetValue("DriverVersion") as string));
            }
        }
        catch { /* registry unreadable: report no GPUs */ }

        return new SystemInfo(gpus, CpuName(), Environment.ProcessorCount, TotalRam(), VulkanLoader());
    }

    static string Vendor(string desc) =>
        desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : desc.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Other";

    static string CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return ((k?.GetValue("ProcessorNameString") as string) ?? "Unknown CPU").Trim();
        }
        catch { return "Unknown CPU"; }
    }

    static bool VulkanLoader() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);

    public static (long total, double usedFraction) Ram()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? ((long)s.TotalPhys, s.MemoryLoad / 100.0) : (0, 0);
    }

    static long TotalRam() => Ram().total;
}
