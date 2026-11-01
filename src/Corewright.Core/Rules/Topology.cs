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

    /// <summary>Logical processors per CCD (L3 group), in CCD order.</summary>
    public ulong[] CcdMasks { get; init; } = [];

    /// <summary>CCD indexes with 3D V-Cache (empty when unknown; all of them on dual V-Cache parts).</summary>
    public int[] VCacheCcds { get; init; } = [];

    /// <summary>The CCD with the highest boosting core, or -1.</summary>
    public int FrequencyCcd { get; init; } = -1;

    /// <summary>Physical cores (their logical processors) ordered fastest first by CPPC; empty without a ranking.</summary>
    public ulong[] CoresByRank { get; init; } = [];

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
            case "auto-background" or "auto-game":
                if (topo.LogicalCount >= 64) { error = "dynamic placement supports up to 63 logical processors"; return false; }
                mask = (ulong)(text.Trim().Equals("auto-game", StringComparison.OrdinalIgnoreCase) ? AffinityModes.Game : AffinityModes.Background);
                return true;
            case "vcache-ccd" or "frequency-ccd" or "best-ccd":
            {
                int ccd = text.Trim().ToLowerInvariant() switch
                {
                    "vcache-ccd" => topo.VCacheCcds.Length == 1 ? topo.VCacheCcds[0] : -1,
                    _ => topo.FrequencyCcd,
                };
                if (ccd < 0 || ccd >= topo.CcdMasks.Length)
                {
                    error = text.Trim().Equals("vcache-ccd", StringComparison.OrdinalIgnoreCase)
                        ? "this CPU has no single V-Cache CCD (none, or every CCD has V-Cache)"
                        : "the CPU reports no core ranking";
                    return false;
                }
                mask = topo.CcdMasks[ccd] & topo.AllMask;
                return mask != 0;
            }
            case "e-cores" or "efficiency":
                if (!topo.IsHybrid) { error = "this CPU has no efficiency cores"; return false; }
                mask = topo.EfficiencyMask;
                return true;
        }

        var lower = text.Trim().ToLowerInvariant();
        if (lower.StartsWith("ccd") && int.TryParse(lower[3..], out int ccdIndex))
        {
            if (ccdIndex < 0 || ccdIndex >= topo.CcdMasks.Length) { error = $"CCD {ccdIndex} does not exist"; return false; }
            mask = topo.CcdMasks[ccdIndex] & topo.AllMask;
            return mask != 0;
        }
        if (lower.StartsWith("best-cores:") && int.TryParse(lower[11..], out int best))
        {
            if (topo.CoresByRank.Length == 0) { error = "the CPU reports no core ranking"; return false; }
            if (best < 1) { error = "best-cores needs at least 1"; return false; }
            foreach (var core in topo.CoresByRank.Take(best)) mask |= core;
            mask &= topo.AllMask;
            return mask != 0;
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
        if (AffinityModes.IsDynamic((long)mask)) return (long)mask == AffinityModes.Game ? "auto (game CCD)" : "auto (background)";
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

/// <summary>
/// Dynamic affinity: the rule says "auto-background" or "auto-game" and a placer picks the real cores when the
/// setting is applied. Encoded as masks with bit 63 set, which no real mask uses below 64 logical processors.
/// </summary>
public static class AffinityModes
{
    public const long Background = unchecked((long)0x8000_0000_0000_0001UL);
    public const long Game = unchecked((long)0x8000_0000_0000_0002UL);

    public static bool IsDynamic(long value) => value == Background || value == Game;
}

/// <summary>Resolves a dynamic affinity mode to a real mask for one process. Implemented by the host.</summary>
public interface IAffinityPlacer
{
    long Resolve(Engine.ProcessInfo process, long mode, Engine.EvalContext context);
}
