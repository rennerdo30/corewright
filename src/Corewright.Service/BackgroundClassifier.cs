using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Autopilot;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service;

/// <summary>Long-term statistics per program (exe name), kept across restarts in background-stats.json.</summary>
public sealed class ProgramStats
{
    public double ObservedSeconds { get; set; }
    public double ForegroundSeconds { get; set; }
    /// <summary>Sum of (cores in use × seconds); divided by ObservedSeconds gives the average load in cores.</summary>
    public double CoreSeconds { get; set; }
    public double PeakCores { get; set; }
    public bool Service { get; set; }
    public long LastSeen { get; set; }
}

public sealed record BackgroundRowDto(string Name, double ObservedMinutes, double ForegroundPercent, double AverageCores, double PeakCores,
    bool Service, string Verdict, string? Affinity, string Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, ProgramStats>))]
internal sealed partial class StatsJson : JsonSerializerContext;

/// <summary>
/// Observes every program over hours (one dictionary update per process per tick, no system calls) and classifies
/// clear background programs: never interactive (foreground share below the limit), not a service, observed long
/// enough. Single-threaded ones get one physical core, filled from the last core backwards and never one of the
/// strongest cores; multi-threaded ones get lower priority and the cores outside the strongest ones.
/// </summary>
internal sealed class BackgroundClassifier
{
    private const int MaxPrograms = 2000;
    private readonly string _path;
    private readonly Dictionary<string, ProgramStats> _stats;
    private readonly HashSet<string> _seenThisTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _coresThisTick = new(StringComparer.OrdinalIgnoreCase);
    private long _lastTickMs, _lastSaveMs, _lastClassifyMs;
    private string _signature = "";

    public BackgroundClassifier(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "background-stats.json");
        _stats = Load(_path);
    }

    public IReadOnlyList<BackgroundVerdict> Verdicts { get; private set; } = [];

    /// <summary>Accumulates one tick. Returns true when the classification changed (the caller rebuilds the rules).</summary>
    public bool Observe(IReadOnlyList<ProcessInfo> processes, int foregroundPid, int logical, long nowMs,
        BackgroundSettings settings, GameModeSettings game, SystemTopology topo)
    {
        double dt = _lastTickMs == 0 ? 0 : Math.Min(10, (nowMs - _lastTickMs) / 1000.0);
        _lastTickMs = nowMs;
        if (dt <= 0) return false;

        _seenThisTick.Clear();
        _coresThisTick.Clear();
        string? foregroundName = null;
        foreach (var p in processes)
        {
            if (p.IsProtected || p.Key.Pid is 0 or 4) continue;
            if (p.Key.Pid == foregroundPid) foregroundName = p.Name;
            // Sum all instances of a program: CpuPercent is a share of the whole machine.
            _coresThisTick[p.Name] = _coresThisTick.GetValueOrDefault(p.Name) + p.CpuPercent * logical / 100.0;
            if (!_stats.TryGetValue(p.Name, out var s))
            {
                if (_stats.Count >= MaxPrograms) continue;
                s = new ProgramStats();
                _stats[p.Name] = s;
            }
            if (p.SessionId == 0) s.Service = true;
        }

        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (name, cores) in _coresThisTick)
        {
            if (!_stats.TryGetValue(name, out var s)) continue;
            s.ObservedSeconds += dt;
            s.CoreSeconds += cores * dt;
            if (cores > s.PeakCores) s.PeakCores = cores;
            if (string.Equals(name, foregroundName, StringComparison.OrdinalIgnoreCase)) s.ForegroundSeconds += dt;
            s.LastSeen = unix;
        }

        if (nowMs - _lastSaveMs > 5 * 60_000)
        {
            _lastSaveMs = nowMs;
            Save();
        }

        if (nowMs - _lastClassifyMs < 60_000 && _lastClassifyMs != 0) return false;
        _lastClassifyMs = nowMs;
        return Classify(settings, game, topo);
    }

    private bool Classify(BackgroundSettings settings, GameModeSettings game, SystemTopology topo)
    {
        var verdicts = new List<BackgroundVerdict>();
        var backCores = BackCores(topo, settings.KeepBestCoresFree);
        var load = new double[backCores.Count];
        ulong allowed = backCores.Aggregate(0UL, (m, c) => m | c);
        var excluded = new HashSet<string>(settings.Exclusions.Concat(game.KeepResponsive).Concat(game.Games), StringComparer.OrdinalIgnoreCase);

        // Busiest first, so the heaviest single-threaded programs get the very last cores.
        var candidates = _stats
            .Select(kv => (Name: kv.Key, Stats: kv.Value, Kind: Verdict(kv.Key, kv.Value, settings, excluded)))
            .Where(x => x.Kind is not null)
            .OrderByDescending(x => x.Stats.CoreSeconds / Math.Max(1, x.Stats.ObservedSeconds))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, s, kind) in candidates)
        {
            double avg = s.CoreSeconds / Math.Max(1, s.ObservedSeconds);
            string? affinity = null;
            if (kind == "single" && backCores.Count > 0)
            {
                // Fill from the back: the first core (counting from the last one) that still has room for this program,
                // otherwise the least loaded back core. Never wraps onto the strongest cores.
                int slot = -1;
                for (int i = 0; i < backCores.Count; i++)
                    if (load[i] + avg <= 1.0) { slot = i; break; }
                if (slot < 0) slot = Array.IndexOf(load, load.Min());
                load[slot] += avg;
                affinity = AffinityParser.Format(backCores[slot]);
            }
            else if (kind == "multi" && allowed != 0 && allowed != topo.AllMask)
            {
                affinity = AffinityParser.Format(allowed);
            }
            verdicts.Add(new BackgroundVerdict(name, kind!, affinity, Describe(s)));
        }

        string signature = string.Join('|', verdicts.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Name}:{v.Kind}:{v.Affinity}"));
        Verdicts = verdicts;
        if (signature == _signature) return false;
        _signature = signature;
        return true;
    }

    private static string? Verdict(string name, ProgramStats s, BackgroundSettings settings, HashSet<string> excluded)
    {
        if (excluded.Contains(name) || s.Service) return null;
        if (s.ObservedSeconds < settings.MinObservedMinutes * 60) return null;
        if (100 * s.ForegroundSeconds / s.ObservedSeconds > settings.MaxForegroundPercent) return null;
        double avg = s.CoreSeconds / s.ObservedSeconds;
        if (avg < 0.01) return null; // idle programs: nothing to gain
        return avg <= settings.SingleCoreLoad ? "single" : "multi";
    }

    private static string Describe(ProgramStats s) =>
        $"{s.ObservedSeconds / 3600:0.#} h observed, {100 * s.ForegroundSeconds / Math.Max(1, s.ObservedSeconds):0.##} % foreground, " +
        $"{s.CoreSeconds / Math.Max(1, s.ObservedSeconds):0.##} cores on average";

    /// <summary>Physical cores (as LP masks) from the back of the CPU, skipping the strongest ones.</summary>
    private static List<ulong> BackCores(SystemTopology topo, int keepFree)
    {
        var protectedMask = topo.CoresByRank.Take(Math.Max(0, keepFree)).Aggregate(0UL, (m, c) => m | c);
        var cores = topo.CoresByRank.Length > 0
            ? topo.CoresByRank.ToList()
            : Enumerable.Range(0, topo.LogicalCount).Select(i => 1UL << i).ToList();
        return cores.Where(c => (c & protectedMask) == 0)
            .OrderByDescending(c => 63 - System.Numerics.BitOperations.LeadingZeroCount(c)) // highest LP first: fill from the back
            .ToList();
    }

    public List<BackgroundRowDto> Rows(BackgroundSettings settings, GameModeSettings game)
    {
        var excluded = new HashSet<string>(settings.Exclusions.Concat(game.KeepResponsive).Concat(game.Games), StringComparer.OrdinalIgnoreCase);
        var verdicts = Verdicts.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        return [.. _stats
            .Where(kv => kv.Value.ObservedSeconds > 60)
            .OrderByDescending(kv => kv.Value.CoreSeconds)
            .Take(300)
            .Select(kv =>
            {
                var s = kv.Value;
                verdicts.TryGetValue(kv.Key, out var v);
                string verdict = v?.Kind switch
                {
                    "single" => "Background, single core",
                    "multi" => "Background, lower priority",
                    _ => excluded.Contains(kv.Key) ? "Excluded" : s.Service ? "Service (left alone)" :
                        s.ObservedSeconds < settings.MinObservedMinutes * 60 ? "Observing" :
                        100 * s.ForegroundSeconds / s.ObservedSeconds > settings.MaxForegroundPercent ? "Interactive" : "Idle",
                };
                return new BackgroundRowDto(kv.Key, Math.Round(s.ObservedSeconds / 60, 1),
                    Math.Round(100 * s.ForegroundSeconds / Math.Max(1, s.ObservedSeconds), 2),
                    Math.Round(s.CoreSeconds / Math.Max(1, s.ObservedSeconds), 2), Math.Round(s.PeakCores, 2), s.Service, verdict, v is null ? null : "auto (sticky, least-loaded cores)", Describe(s));
            })];
    }

    public void Save()
    {
        try
        {
            // Forget programs not seen for 30 days so the file stays small.
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400;
            foreach (var k in _stats.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList()) _stats.Remove(k);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_stats, StatsJson.Default.DictionaryStringProgramStats));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, ProgramStats> Load(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize(File.ReadAllText(path), StatsJson.Default.DictionaryStringProgramStats) is { } d)
                return new Dictionary<string, ProgramStats>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new Dictionary<string, ProgramStats>(StringComparer.OrdinalIgnoreCase);
    }
}
