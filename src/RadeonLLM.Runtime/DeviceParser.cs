using System.Text.RegularExpressions;

namespace RadeonLLM.Runtime;

public sealed record RuntimeDevice(string Id, string Name, long TotalMiB, long FreeMiB)
{
    public bool IsVulkan => Id.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parses lines such as "  Vulkan0: AMD Radeon RX 6900 XT (16368 MiB, 16000 MiB free)".</summary>
public static partial class DeviceParser
{
    [GeneratedRegex(@"^\s*(?<id>[A-Za-z]+\d+):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?\)\s*$", RegexOptions.Multiline)]
    private static partial Regex Line();

    public static IReadOnlyList<RuntimeDevice> Parse(string output) =>
        Line().Matches(output).Select(m => new RuntimeDevice(
            m.Groups["id"].Value, m.Groups["name"].Value,
            long.Parse(m.Groups["total"].Value),
            m.Groups["free"].Success ? long.Parse(m.Groups["free"].Value) : 0)).ToList();
}
