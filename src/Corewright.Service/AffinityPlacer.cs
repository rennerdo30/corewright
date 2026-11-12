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
        if (AffinityModes.IsIsolate(mode)) return 0; // Isolation is resolved by the engine through PickIsolated.
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

    /// <summary>
    /// PM-49 "isolate:N": N threads of their own, chosen like a careful human would on this CPU:
    /// <list type="bullet">
    /// <item>whole physical cores, strongest first (CPPC, the Ryzen Master ranking), so no other process shares a core
    /// through SMT; an odd N leaves the last sibling idle but reserved;</item>
    /// <item>never physical core 0, where Windows runs most interrupts, DPCs and the shell;</item>
    /// <item>P-cores only on hybrid CPUs;</item>
    /// <item>inside one CCD (one L3) when they fit, so the program's threads share their cache: the game's CCD for a game,
    /// otherwise the other CCDs first, so a game started later keeps its CCD;</item>
    /// <item>outside other isolations and group cores, and always leaving at least two cores for everything else.</item>
    /// </list>
    /// </summary>
    public ulong PickIsolated(ProcessInfo owner, int threads, ulong taken, EvalContext ctx, out ulong reserve, out string? why)
    {
        reserve = 0;
        why = null;
        var topo = topology();
        if (threads < 1) { why = "isolate needs at least 1 thread"; return 0; }

        // Every physical core once, strongest first; cores missing from the ranking follow in their physical order.
        var cores = new List<ulong>(topo.PhysicalCores.Length);
        foreach (var c in topo.CoresByRank) if ((c & topo.AllMask) == c && c != 0) cores.Add(c);
        foreach (var c in topo.PhysicalCores) if ((c & topo.AllMask) == c && c != 0 && !cores.Contains(c)) cores.Add(c);
        // Without physical-core boundaries an SMT sibling could stay shared: refuse rather than claim isolation.
        if (cores.Count == 0) { why = "the CPU layout (physical cores) is unknown, so threads cannot be isolated safely"; return 0; }

        ulong core0 = 0;
        foreach (var c in cores) if ((c & 1) != 0) { core0 = c; break; }
        ulong blocked = taken | groupCores() | core0;
        var free = new List<ulong>(cores.Count);
        foreach (var c in cores)
            if ((c & blocked) == 0 && (!topo.IsHybrid || (c & topo.EfficiencyMask) == 0)) free.Add(c);

        // CCD order: the game's CCD for the game itself; everyone else prefers the other CCDs.
        int game = gameCcd();
        ulong gameMask = topo.CcdMasks.Length > 1 && game >= 0 && game < topo.CcdMasks.Length ? topo.CcdMasks[game] : 0;
        bool isGame = owner.IsGame || (ctx.GamePid != 0 && owner.Key.Pid == ctx.GamePid);
        var pools = new List<ulong>();
        if (topo.CcdMasks.Length > 1)
        {
            if (isGame && gameMask != 0) pools.Add(gameMask);
            // Other CCDs in the order of their strongest free core.
            foreach (var c in free)
                foreach (var ccd in topo.CcdMasks)
                    if ((c & ~ccd) == 0 && !pools.Contains(ccd) && (isGame || ccd != gameMask)) pools.Add(ccd);
            if (!isGame && gameMask != 0) pools.Add(gameMask);
        }

        var picked = new List<ulong>();
        bool Fill(ulong pool)
        {
            picked.Clear();
            int have = 0;
            foreach (var c in free)
            {
                if (have >= threads) break;
                if (pool != 0 && (c & ~pool) != 0) continue;
                picked.Add(c);
                have += AffinityParser.Count(c);
            }
            return have >= threads;
        }

        bool fits = false;
        foreach (var pool in pools) if (fits = Fill(pool)) break;
        if (!fits)
        {
            // Spanning CCDs costs cache sharing but still isolates; same CCD preference order, strongest first.
            fits = Fill(0);
        }
        int freeThreads = 0;
        foreach (var c in free) freeThreads += AffinityParser.Count(c);
        if (!fits)
        {
            why = topo.IsHybrid
                ? $"isolate:{threads} needs {threads} P-core threads; {freeThreads} are free (core 0, other isolations and group cores excluded)"
                : $"isolate:{threads} needs {threads} threads; {freeThreads} are free (core 0, other isolations and group cores excluded)";
            return 0;
        }

        ulong mask = 0;
        int left = threads;
        foreach (var c in picked)
        {
            reserve |= c;
            for (ulong rest = c; rest != 0 && left > 0; rest &= rest - 1, left--) mask |= rest & (~rest + 1);
        }

        // Everything else needs room: at least two physical cores (core 0 included) stay outside all isolations.
        int remainingCores = 0;
        ulong held = taken | groupCores() | reserve;
        foreach (var c in cores) if ((c & held) == 0) remainingCores++;
        if (remainingCores < 2)
        {
            why = $"isolate:{threads} would leave fewer than two cores for every other process";
            reserve = 0;
            return 0;
        }
        return mask;
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
