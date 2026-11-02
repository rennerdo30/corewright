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
        ulong strongest = 0;
        if (ctx.ForegroundBusy || ctx.GamePid != 0)
        {
            var cores = topo.CoresByRank.Length > 0 ? topo.CoresByRank : topo.PhysicalCores;
            for (int i = 0, n = Math.Min(Math.Max(0, keepBestFree()), cores.Length); i < n; i++) strongest |= cores[i];
        }
        ulong allowed = topo.AllMask & ~strongest & ~groupCores();
        if (ctx.GamePid != 0) allowed &= ~gameMask;
        return allowed == 0 || allowed == topo.AllMask ? 0 : (long)allowed;
    }

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
                $"{p.Rule}: loose mask; game CCD, strongest busy cores and exclusive group cores excluded where applicable"));
        }
        return rows;
    }
}
