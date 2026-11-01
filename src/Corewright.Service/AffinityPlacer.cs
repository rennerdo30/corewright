using System.Numerics;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service;

public sealed record PlacementDto(int Pid, string Name, string Cores, int Ccd, int DemandThreads, double PlacedSecondsAgo, string Why);

/// <summary>
/// Picks real cores for "auto-background" and "auto-game" (PM-31), weighing the whole picture (owner, 2026-11-02):
/// CPU architecture (CCDs, physical cores and SMT threads, 3D V-Cache, P/E cores, CPPC strength), live load per
/// thread, cores already reserved by other placed processes, and the process's own demand.
///
/// Cache-friendly: a process is placed once, on whole physical cores of the CCD where its demand is cheapest, and
/// stays there. It grows within its CCD when its demand grows (its threads and L3 stay warm). It moves only when it
/// must leave (a game started on its CCD) or its cores stay saturated for a minute while another CCD idles, at most
/// every two minutes.
/// </summary>
internal sealed class AffinityPlacer(Func<SystemTopology> topology, Func<float[]> threadLoad, Func<int> gameCcd, Func<int> keepBestFree, Func<ulong> groupCores)
    : IAffinityPlacer
{
    private const long GrowEveryMs = 30_000;
    private const long MoveEveryMs = 120_000;
    private const long SaturatedForMs = 60_000;

    // Cost weights, in "percent load" units, so they compare with live load directly.
    private const double StrengthPenalty = 30;   // strongest allowed core costs +30, weakest +0
    private const double EfficiencyBonus = 20;   // E-cores are preferred for background work
    private const double VCachePenalty = 15;     // on single V-Cache parts, keep the V-Cache CCD for games
    private const double ReservationWeight = 50; // per reserved thread of another placed process

    private sealed class Placement
    {
        public required string Name;
        public ulong Mask;
        public int Ccd;
        public int Demand;
        public long PlacedMs;
        public long ChangedMs;
        public long SaturatedSinceMs;
        public string Why = "";
    }

    private readonly Dictionary<ProcessKey, Placement> _placed = [];

    public long Resolve(ProcessInfo p, long mode, EvalContext ctx)
    {
        var topo = topology();
        if (topo.CcdMasks.Length == 0) return 0;
        int game = gameCcd();
        long now = ctx.NowMs;

        if (mode == AffinityModes.Game)
            return game >= 0 && game < topo.CcdMasks.Length ? (long)(topo.CcdMasks[game] & topo.AllMask) : 0;

        // Hard limits: never the strongest cores, never the game's CCD while a game runs.
        ulong strongest = topo.CoresByRank.Take(Math.Max(0, keepBestFree())).Aggregate(0UL, (m, c) => m | c);
        ulong gameMask = ctx.GamePid != 0 && game >= 0 && topo.CcdMasks.Length > 1 ? topo.CcdMasks[game] : 0;
        // Cores a process group holds exclusively (exclusive:N) are never used for Autopilot's placements.
        ulong allowed = topo.AllMask & ~strongest & ~gameMask & ~groupCores();
        if (allowed == 0) allowed = topo.AllMask & ~gameMask;
        if (allowed == 0) return 0;

        var load = threadLoad();
        int demand = Math.Max(2, (int)Math.Ceiling(p.CpuPercent * topo.LogicalCount / 100.0 * 1.5));
        var reserved = Reservations(except: p.Key);

        if (_placed.TryGetValue(p.Key, out var pl) && (pl.Mask & ~allowed) == 0)
        {
            double busy = Average(load, pl.Mask);
            if (busy > 95) { if (pl.SaturatedSinceMs == 0) pl.SaturatedSinceMs = now; }
            else pl.SaturatedSinceMs = 0;

            // Grow inside the same CCD only: existing threads keep their cores, the L3 stays warm.
            if (BitOperations.PopCount(pl.Mask) < demand && now - pl.ChangedMs >= GrowEveryMs)
            {
                ulong grown = Pick(pl.Mask, topo.CcdMasks[pl.Ccd] & allowed, demand, topo, load, reserved);
                if (grown != pl.Mask)
                {
                    pl.Mask = grown;
                    pl.Demand = demand;
                    pl.ChangedMs = now;
                    pl.Why = $"Grew to {BitOperations.PopCount(grown)} threads on CCD {pl.Ccd} because it needed more";
                }
            }

            bool saturated = pl.SaturatedSinceMs != 0 && now - pl.SaturatedSinceMs >= SaturatedForMs;
            if (!saturated || now - pl.ChangedMs < MoveEveryMs) return (long)pl.Mask;
            int other = CheapestCcd(topo, allowed, demand, load, reserved, exclude: pl.Ccd, out _);
            if (other < 0 || Average(load, topo.CcdMasks[other] & allowed) >= 40) return (long)pl.Mask;
        }

        bool moved = _placed.ContainsKey(p.Key);
        int ccd = CheapestCcd(topo, allowed, demand, load, reserved, exclude: -1, out _);
        if (ccd < 0) return (long)allowed;
        ulong mask = Pick(0, topo.CcdMasks[ccd] & allowed, demand, topo, load, reserved);
        _placed[p.Key] = new Placement
        {
            Name = p.Name,
            Mask = mask,
            Ccd = ccd,
            Demand = demand,
            PlacedMs = moved ? _placed[p.Key].PlacedMs : now,
            ChangedMs = now,
            Why = (moved ? "Moved" : "Placed") + $" on CCD {ccd}, the least busy place for {demand} threads" +
                  (gameMask != 0 ? ", away from the game" : "") + (strongest != 0 ? ", off your strongest cores" : "") + ".",
        };
        return (long)mask;
    }

    /// <summary>Forget exited processes (called once per tick with the live set).</summary>
    public void Prune(Func<ProcessKey, bool> alive)
    {
        if (_placed.Count == 0) return;
        foreach (var k in _placed.Keys.Where(k => !alive(k)).ToList()) _placed.Remove(k);
    }

    /// <summary>Current placements, for the Settings page: which process runs where and why.</summary>
    public List<PlacementDto> Snapshot(long nowMs) =>
        [.. _placed.OrderBy(kv => kv.Value.Ccd).ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PlacementDto(kv.Key.Pid, kv.Value.Name, AffinityParser.Format(kv.Value.Mask), kv.Value.Ccd,
                kv.Value.Demand, Math.Round((nowMs - kv.Value.PlacedMs) / 1000.0), kv.Value.Why))];

    /// <summary>Threads promised to other placed processes, spread over their masks (they may be idle right now).</summary>
    private double[] Reservations(ProcessKey except)
    {
        var r = new double[64];
        foreach (var (key, pl) in _placed)
        {
            if (key == except) continue;
            int n = BitOperations.PopCount(pl.Mask);
            if (n == 0) continue;
            double share = (double)pl.Demand / n;
            for (int i = 0; i < 64; i++)
                if ((pl.Mask & (1UL << i)) != 0) r[i] += share;
        }
        return r;
    }

    /// <summary>Cost of one physical core for background work: lower is better.</summary>
    private static double CoreCost(ulong core, SystemTopology topo, float[] load, double[] reserved)
    {
        double cost = Average(load, core);
        int threads = BitOperations.PopCount(core);
        double res = 0;
        for (int i = 0; i < 64; i++)
            if ((core & (1UL << i)) != 0) res += reserved[i];
        cost += ReservationWeight * res / Math.Max(1, threads);

        int rank = Array.IndexOf(topo.CoresByRank, core);
        if (rank >= 0 && topo.CoresByRank.Length > 1)
            cost += StrengthPenalty * (1.0 - (double)rank / (topo.CoresByRank.Length - 1));
        if ((core & topo.EfficiencyMask) != 0) cost -= EfficiencyBonus;
        if (topo.VCacheCcds.Length == 1 && topo.CcdMasks.Length > 1 && (core & topo.CcdMasks[topo.VCacheCcds[0]]) != 0) cost += VCachePenalty;
        return cost;
    }

    private static IEnumerable<ulong> Cores(SystemTopology topo) =>
        topo.CoresByRank.Length > 0 ? topo.CoresByRank : Enumerable.Range(0, topo.LogicalCount).Select(i => 1UL << i);

    /// <summary>Adds the cheapest whole physical cores of <paramref name="pool"/> until <paramref name="demand"/> threads.</summary>
    private static ulong Pick(ulong mask, ulong pool, int demand, SystemTopology topo, float[] load, double[] reserved)
    {
        var candidates = Cores(topo)
            .Select(c => c & pool)
            .Where(c => c != 0 && (c & mask) == 0)
            .OrderBy(c => CoreCost(c, topo, load, reserved))
            .ThenByDescending(c => 63 - BitOperations.LeadingZeroCount(c)); // ties: from the back
        foreach (var core in candidates)
        {
            if (BitOperations.PopCount(mask) >= demand) break;
            mask |= core;
        }
        return mask == 0 ? pool : mask;
    }

    /// <summary>The CCD where the cheapest cores for this demand cost least in total.</summary>
    private static int CheapestCcd(SystemTopology topo, ulong allowed, int demand, float[] load, double[] reserved, int exclude, out double bestCost)
    {
        int best = -1;
        bestCost = double.MaxValue;
        for (int i = 0; i < topo.CcdMasks.Length; i++)
        {
            ulong pool = topo.CcdMasks[i] & allowed;
            if (i == exclude || pool == 0) continue;
            var costs = Cores(topo).Select(c => c & pool).Where(c => c != 0).Select(c => (Core: c, Cost: CoreCost(c, topo, load, reserved)))
                .OrderBy(x => x.Cost).ToList();
            double total = 0;
            int threads = 0;
            foreach (var (core, c) in costs)
            {
                if (threads >= demand) break;
                total += c;
                threads += BitOperations.PopCount(core);
            }
            // A CCD that cannot hold the whole demand pays for the shortfall.
            if (threads < demand) total += 100 * (demand - threads);
            if (total < bestCost) { bestCost = total; best = i; }
        }
        return best;
    }

    private static double Average(float[] load, ulong mask)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < 64 && i < load.Length; i++)
            if ((mask & (1UL << i)) != 0) { sum += load[i]; n++; }
        return n == 0 ? 0 : sum / n;
    }
}
