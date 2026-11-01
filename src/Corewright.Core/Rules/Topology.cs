using System.Numerics;
using System.Text;

namespace Corewright.Core.Rules;

/// <summary>Logical processors of processor group 0, split into performance and efficiency cores.</summary>
public sealed class SystemTopology
{
    public SystemTopology(int logicalCount, ulong performanceMask, ulong efficiencyMask)
    {
        LogicalCount = Math.Clamp(logicalCount, 1, 64);
        AllMask = LogicalCount == 64 ? ulong.MaxValue : (1UL << LogicalCount) - 1;
        PerformanceMask = performanceMask & AllMask;
        EfficiencyMask = efficiencyMask & AllMask;
        if (PerformanceMask == 0) PerformanceMask = AllMask;
    }

    public int LogicalCount { get; }
    public ulong AllMask { get; }
    public ulong PerformanceMask { get; }
    public ulong EfficiencyMask { get; }
    public bool IsHybrid => EfficiencyMask != 0;

    public static SystemTopology Uniform(int logicalCount) => new(logicalCount, ulong.MaxValue, 0);
}

public static class AffinityParser
{
    /// <summary>Parses "all", "p-cores", "e-cores" or a list such as "0-7,16".</summary>
    public static bool TryParse(string? text, SystemTopology topo, out ulong mask, out string? error)
    {
        mask = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "affinity is empty"; return false; }

        switch (text.Trim().ToLowerInvariant())
        {
            case "all": mask = topo.AllMask; return true;
            case "p-cores" or "performance":
                mask = topo.PerformanceMask;
                return true;
            case "e-cores" or "efficiency":
                if (!topo.IsHybrid) { error = "this CPU has no efficiency cores"; return false; }
                mask = topo.EfficiencyMask;
                return true;
        }

        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            int dash = part.IndexOf('-');
            if (!int.TryParse(dash < 0 ? part : part[..dash], out int from) ||
                !int.TryParse(dash < 0 ? part : part[(dash + 1)..], out int to) || from < 0 || to < from)
            {
                error = $"invalid processor range '{part}'";
                return false;
            }
            if (to >= topo.LogicalCount)
            {
                error = $"processor {to} does not exist (0-{topo.LogicalCount - 1})";
                return false;
            }
            for (int i = from; i <= to; i++) mask |= 1UL << i;
        }

        if (mask == 0) { error = "affinity selects no processor"; return false; }
        return true;
    }

    public static string Format(ulong mask)
    {
        if (mask == 0) return "none";
        var sb = new StringBuilder();
        int i = 0;
        while (i < 64)
        {
            if ((mask & (1UL << i)) == 0) { i++; continue; }
            int start = i;
            while (i + 1 < 64 && (mask & (1UL << (i + 1))) != 0) i++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(start);
            if (i > start) sb.Append('-').Append(i);
            i++;
        }
        return sb.ToString();
    }

    public static int Count(ulong mask) => BitOperations.PopCount(mask);
}
