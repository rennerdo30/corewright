using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service;

public sealed record PlacementDto(int Pid, string Name, string Cores, int Ccd, int DemandThreads, double PlacedSecondsAgo, string Why);

/// <summary>Stateless loose masks: Windows chooses individual cores. No demand sizing or reservations.</summary>
internal sealed class AffinityPlacer(Func<SystemTopology> topology, Func<int> gameCcd,
    Func<int> keepBestFree, Func<ulong> groupCores) : IAffinityPlacer
{
    public long Resolve(ProcessInfo p, long mode, EvalContext ctx)
    {
        var topo = topology();
        int game = gameCcd();
        ulong gameMask = game >= 0 && game < topo.CcdMasks.Length ? topo.CcdMasks[game] & topo.AllMask : 0;
        if (mode == AffinityModes.Game) return (long)(gameMask & ~groupCores());
        if (mode == AffinityModes.NonGame)
        {
            ulong other = topo.AllMask & ~gameMask;
            return ctx.GamePid == 0 || gameMask == 0 || other == 0 || (other & groupCores()) != 0 ? 0 : (long)other;
        }
        ulong strongest = 0;
        if (ctx.ForegroundBusy || ctx.GamePid != 0)
        {
            var cores = topo.CoresByRank.Length > 0 ? topo.CoresByRank : topo.PhysicalCores;
            if (keepBestFree() > 0 && cores.Length == 0) return 0;
            for (int i = 0, n = Math.Min(Math.Max(0, keepBestFree()), cores.Length); i < n; i++) strongest |= cores[i];
        }
        ulong allowed = topo.AllMask & ~strongest & ~groupCores();
        if (ctx.GamePid != 0) allowed &= ~gameMask;
        return allowed == 0 || allowed == topo.AllMask ? 0 : (long)allowed;
    }

    /// <summary>Why a process runs where it runs, in the words the Live placements table shows.</summary>
    private static string Reason(string rule, int onlyCcd) => rule switch
    {
        _ when rule.StartsWith("Autopilot: builds", StringComparison.Ordinal) => "A build while you play: runs on the other CCD so the game keeps its cores.",
        _ when rule.Contains("while gaming", StringComparison.Ordinal) => "Busy while you play: moved to the other CCD so the game keeps its cores.",
        _ when rule.Contains("game", StringComparison.OrdinalIgnoreCase) => onlyCcd >= 0 ? $"The game runs on CCD {onlyCcd}." : "The game's cores.",
        _ => "Background work: every core except your strongest few while you are busy, and not the game's CCD while you play.",
    };

    /// <summary>Confirmed engine-owned Autopilot affinity, never a cached preference or failed write.</summary>
    public List<PlacementDto> Snapshot(long nowMs, RuleEngine engine)
    {
        var rows = new List<PlacementDto>();
        var topo = topology();
        foreach (var p in engine.GetAutopilotAffinity())
        {
            int ccd = -1;
            for (int i = 0; i < topo.CcdMasks.Length; i++)
                if (((ulong)p.Mask & ~topo.CcdMasks[i]) == 0) { ccd = i; break; }
            rows.Add(new(p.Key.Pid, p.Name, AffinityParser.Format((ulong)p.Mask), ccd, 0,
                Math.Max(0, (DateTime.UtcNow.ToFileTimeUtc() - p.Since) / 10_000_000.0),
                Reason(p.Rule, ccd >= 0 && topo.CcdMasks.Length > 1 ? ccd : -1)));
        }
        return rows;
    }
}
