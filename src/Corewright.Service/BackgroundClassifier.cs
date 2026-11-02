using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Autopilot;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service;

/// <summary>Long-term statistics per program (see <see cref="ProgramIdentity"/>), kept across restarts. All time
/// counters age with a half-life, so what a program did last week weighs less than what it does today.</summary>
public sealed class ProgramStats
{
    public string Display { get; set; } = "";
    public double ObservedSeconds { get; set; }
    public double ForegroundSeconds { get; set; }
    /// <summary>Sum of (cores in use × seconds); divided by ObservedSeconds gives the average load in cores.</summary>
    public double CoreSeconds { get; set; }
    /// <summary>Seconds spent in each load band (<see cref="BackgroundClassifier.Bands"/>), for the 95th percentile.</summary>
    public double[] Profile { get; set; } = new double[BackgroundClassifier.Bands.Length];
    public double PeakCores { get; set; }
    public bool Service { get; set; }
    /// <summary>Unix seconds when the program (or its app) was last in front; 0 = never.</summary>
    public long LastForeground { get; set; }
    public double RecentForegroundRemaining { get; set; } = 1800;
    public long LastSeen { get; set; }
    /// <summary>Child processes it started (aged like the time counters). Launchers are never placed.</summary>
    public double ChildStarts { get; set; }
}

public sealed record BackgroundRowDto(string Name, string Program, double ObservedMinutes, double ForegroundPercent, double AverageCores,
    double PeakCores, double P95Cores, bool Service, string Verdict, string? Affinity, string Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, ProgramStats>))]
internal sealed partial class StatsJson : JsonSerializerContext;

/// <summary>
/// Background placement, version 2 (owner, 2026-11-02: "is our background rule really that easy?? we need to make this
/// way better!"). Decides per program, not per exe name:
/// <list type="bullet">
/// <item>Identity: exe path, or for hosts what they run (python.exe (train.py), svchost.exe (wuauserv)).</item>
/// <item>In front means the whole app: the foreground process and everything with the same app root (Electron
/// helpers, browser tabs, a terminal's children). Anything in front in the last 30 minutes is interactive.</item>
/// <item>Statistics age (3-day half-life); a load profile gives the 95th percentile, so bursts count.</item>
/// <item>Services are judged like everything else, except latency-critical ones (audio, input, drivers, network core).</item>
/// <item>Launchers are never placed: programs that keep starting other programs (agent hosts, terminals, IDEs, build
/// drivers). Their work runs in the children, and Windows gives every child the parent's cores until the engine puts
/// them back on its next tick, so a short command would run its whole life confined (2026-11-02: agent shells ran on
/// 2 threads because their claude.exe host was placed).</item>
/// </list>
/// Cost: one dictionary update per process per tick; identity and app root once per process lifetime.
/// </summary>
internal sealed class BackgroundClassifier
{
    /// <summary>Upper edges of the load bands, in cores.</summary>
    internal static readonly double[] Bands = [0.05, 0.25, 0.5, 1, 2, 4, 8, double.MaxValue];
    private static readonly double[] BandMid = [0.02, 0.15, 0.4, 0.8, 1.5, 3, 6, 12];

    private const int MaxPrograms = 3000;
    private const double HalfLifeSeconds = 3 * 86400;
    private const long RecentForegroundSeconds = 30 * 60;
    /// <summary>Child starts per observed hour from which a program counts as a launcher.</summary>
    private const double LauncherStartsPerHour = 6;

    private readonly string _path;
    private readonly Dictionary<string, ProgramStats> _stats;
    private readonly Dictionary<int, ProcessInfo> _byPid = [];
    private readonly Dictionary<string, double> _coresThisTick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _serviceThisTick = new(StringComparer.Ordinal);
    private readonly HashSet<string> _frontThisTick = new(StringComparer.Ordinal);
    private readonly List<ProcessInfo> _new = [];
    private long _lastTickMs, _lastSaveMs, _lastClassifyMs, _lastDecayMs;
    private string _signature = "";
    private List<string>? _launcherSource;
    private HashSet<string> _launchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _wasPresent;
    private readonly HashSet<string> _verdictPrograms = new(StringComparer.Ordinal);

    public BackgroundClassifier(string dataDirectory)
    {
        // Version 2 keys by program identity; the version 1 file (keyed by exe name) stays untouched next to it.
        // v3: statistics learned without foreground data (tray missing) were wrong; v2 is kept next to it, unused.
        _path = Path.Combine(dataDirectory, "background-stats-v3.json");
        _stats = Load(_path);
    }

    public IReadOnlyList<BackgroundVerdict> Verdicts { get; private set; } = [];

    /// <summary>App root of the current foreground process (for the isForegroundApp rule field).</summary>
    public ProcessKey ForegroundRoot { get; private set; }

    /// <summary>Accumulates one tick. Returns true when the classification changed (the caller rebuilds the rules).</summary>
    public bool Observe(IReadOnlyList<ProcessInfo> processes, int foregroundPid, bool foregroundKnown, int logical, long nowMs,
        BackgroundSettings settings, GameModeSettings game, SystemTopology topo, List<string> launchers)
    {
        if (!ReferenceEquals(_launcherSource, launchers))
        {
            _launcherSource = launchers;
            _launchers = new(launchers, StringComparer.OrdinalIgnoreCase);
        }
        _byPid.Clear();
        foreach (var p in processes) _byPid[p.Key.Pid] = p;

        // Identity and app root: once per process lifetime, before the engine sees the process.
        foreach (var p in processes)
        {
            if (p.Program is not null) continue;
            var (key, display, critical) = ProgramIdentity.Identify(p);
            p.Program = key;
            p.LatencyCritical = critical;
            p.AppRoot = RootOf(p);
            if (_stats.TryGetValue(key, out var known)) known.Display = display;
            else if (_stats.Count < MaxPrograms && !p.IsProtected && p.Key.Pid is not (0 or 4)) _stats[key] = new ProgramStats { Display = display };
            _new.Add(p);
        }
        // Each new process counts as a start for its parent's program (after the loop: the parent may be new too).
        // Processes already running when the service starts are not starts; counting them would inflate every restart.
        if (_lastTickMs != 0)
            foreach (var p in _new)
                if (p.Parent is { } parent && _byPid.TryGetValue(parent.Pid, out var pp) && pp.Key == parent && pp.Program is { } pk &&
                    _stats.TryGetValue(pk, out var ps))
                {
                    if (foregroundKnown) ps.ChildStarts++;
                    pp.LastChildStartMs = nowMs;
                }
        _new.Clear();

        foreach (var p in processes) p.HasChildren = false;
        foreach (var p in processes)
            if (p.Parent is { } parent && _byPid.TryGetValue(parent.Pid, out var pp) && pp.Key == parent) pp.HasChildren = true;
        foreach (var p in processes)
        {
            bool conditional = p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("gradle.exe", StringComparison.OrdinalIgnoreCase);
            p.IsLauncher = (_launchers.Contains(p.Name) && (!conditional || p.HasChildren)) ||
                (p.LastChildStartMs != 0 && nowMs - p.LastChildStartMs < 600_000) ||
                (p.Program is { } key && _stats.TryGetValue(key, out var stats) && IsLauncher(stats));
        }

        ForegroundRoot = _byPid.TryGetValue(foregroundPid, out var fg) ? fg.AppRoot : default;

        double dt = _lastTickMs == 0 ? 0 : Math.Min(10, (nowMs - _lastTickMs) / 1000.0);
        _lastTickMs = nowMs;
        // Without foreground reports (the service learns them from the tray) every program would look "never in
        // front" and the user's own apps would be classified as background (2026-11-02). Learn nothing then.
        bool observing = _wasPresent && foregroundKnown;
        _wasPresent = foregroundKnown;
        if (dt <= 0 || !observing) return false;
        foreach (var stats in _stats.Values) stats.RecentForegroundRemaining = Math.Max(0, stats.RecentForegroundRemaining - dt);

        _coresThisTick.Clear();
        _serviceThisTick.Clear();
        _frontThisTick.Clear();
        foreach (var p in processes)
        {
            if (p.IsProtected || p.Key.Pid is 0 or 4 || p.Program is null) continue;
            // All instances of a program add up: CpuPercent is a share of the whole machine.
            _coresThisTick[p.Program] = _coresThisTick.GetValueOrDefault(p.Program) + p.CpuPercent * logical / 100.0;
            if (p.SessionId == 0) _serviceThisTick[p.Program] = true;
            if (p.Key.Pid == foregroundPid || (ForegroundRoot.Pid != 0 && p.AppRoot == ForegroundRoot)) _frontThisTick.Add(p.Program);
        }

        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var key in _frontThisTick)
            if (_verdictPrograms.Contains(key)) { _lastClassifyMs = 0; break; }
        foreach (var (key, cores) in _coresThisTick)
        {
            if (!_stats.TryGetValue(key, out var s)) continue;
            s.ObservedSeconds += dt;
            s.CoreSeconds += cores * dt;
            s.Profile[Band(cores)] += dt;
            if (cores > s.PeakCores) s.PeakCores = cores;
            if (_serviceThisTick.ContainsKey(key)) s.Service = true;
            if (_frontThisTick.Contains(key))
            {
                s.ForegroundSeconds += dt;
                s.LastForeground = unix;
                s.RecentForegroundRemaining = RecentForegroundSeconds;
            }
            s.LastSeen = unix;
        }

        if (nowMs - _lastSaveMs > 5 * 60_000)
        {
            _lastSaveMs = nowMs;
            Save();
        }

        if (nowMs - _lastClassifyMs < 60_000 && _lastClassifyMs != 0) return false;
        _lastClassifyMs = nowMs;
        Decay(nowMs);
        return Classify(settings, game, unix);
    }

    /// <summary>The topmost ancestor below the shell. Services stop at services.exe, so each service is its own app.</summary>
    private ProcessKey RootOf(ProcessInfo p)
    {
        var cur = p;
        for (int depth = 0; depth < 16; depth++)
        {
            if (cur.Parent is not { } parent || !_byPid.TryGetValue(parent.Pid, out var pp) || pp.Key != parent) break;
            if (ProgramIdentity.IsShell(pp.Name) || pp.IsProtected) break;
            cur = pp;
        }
        return cur.Key;
    }

    private static int Band(double cores)
    {
        for (int i = 0; i < Bands.Length; i++)
            if (cores <= Bands[i]) return i;
        return Bands.Length - 1;
    }

    /// <summary>Ages every counter once a minute (cheaper than per tick, same result).</summary>
    private void Decay(long nowMs)
    {
        if (_lastDecayMs == 0) { _lastDecayMs = nowMs; return; }
        double f = Math.Pow(0.5, (nowMs - _lastDecayMs) / 1000.0 / HalfLifeSeconds);
        _lastDecayMs = nowMs;
        foreach (var s in _stats.Values)
        {
            s.ObservedSeconds *= f;
            s.ForegroundSeconds *= f;
            s.CoreSeconds *= f;
            s.PeakCores *= f;
            s.ChildStarts *= f;
            for (int i = 0; i < s.Profile.Length; i++) s.Profile[i] *= f;
        }
    }

    internal static double P95(ProgramStats s)
    {
        double total = s.Profile.Sum();
        if (total <= 0) return 0;
        double acc = 0;
        for (int i = 0; i < s.Profile.Length; i++)
        {
            acc += s.Profile[i];
            if (acc >= 0.95 * total) return Math.Min(BandMid[i], Math.Max(s.PeakCores, BandMid[0]));
        }
        return s.PeakCores;
    }

    private bool Classify(BackgroundSettings settings, GameModeSettings game, long unix)
    {
        var excluded = Excluded(settings, game);
        var verdicts = new List<BackgroundVerdict>();
        foreach (var (key, s) in _stats)
        {
            var (kind, _) = Decide(key, s, settings, excluded, unix);
            if (kind is "single" or "multi") verdicts.Add(new BackgroundVerdict(key, s.Display, kind, s.Service, Describe(s)));
        }
        verdicts.Sort((a, b) => string.CompareOrdinal(a.Program, b.Program));
        string signature = string.Join('|', verdicts.Select(v => $"{v.Program}:{v.Kind}"));
        Verdicts = verdicts;
        _verdictPrograms.Clear();
        foreach (var v in verdicts) _verdictPrograms.Add(v.Program);
        if (signature == _signature) return false;
        _signature = signature;
        return true;
    }

    private static HashSet<string> Excluded(BackgroundSettings settings, GameModeSettings game) =>
        new(settings.Exclusions.Concat(game.KeepResponsive).Concat(game.Games), StringComparer.OrdinalIgnoreCase);

    /// <summary>The decision and its reason, in the words the Learned table shows.</summary>
    private static (string Kind, string Label) Decide(string key, ProgramStats s, BackgroundSettings settings, HashSet<string> excluded, long unix)
    {
        string exe = s.Display.Split(" (")[0];
        if (excluded.Contains(exe) || excluded.Contains(s.Display)) return ("excluded", "Excluded");
        if (ProgramIdentity.IsCritical(exe, s.Display)) return ("critical", "Left alone: latency critical");
        if (s.Service && !settings.IncludeServices) return ("service", "Service (services are off)");
        if (s.ObservedSeconds < settings.MinObservedMinutes * 60) return ("observing", "Observing");
        if (s.RecentForegroundRemaining > 0) return ("interactive", "Interactive: in front recently");
        if (100 * s.ForegroundSeconds / s.ObservedSeconds > settings.MaxForegroundPercent) return ("interactive", "Interactive");
        if (IsLauncher(s)) return ("launcher", "Left alone: starts other programs");
        double avg = s.CoreSeconds / s.ObservedSeconds;
        double p95 = P95(s);
        if (avg < 0.02 && p95 <= 0.25) return ("idle", "Idle: nothing to gain");
        return p95 <= settings.SingleCoreLoad ? ("single", "Background: placed") : ("multi", "Background: placed, lower priority");
    }

    private static bool IsLauncher(ProgramStats s) =>
        s.ChildStarts >= 3 && s.ChildStarts / Math.Max(1.0 / 60, s.ObservedSeconds / 3600) >= LauncherStartsPerHour;

    private static string Describe(ProgramStats s)
    {
        double hours = s.ObservedSeconds / 3600;
        double front = 100 * s.ForegroundSeconds / Math.Max(1, s.ObservedSeconds);
        double avg = s.CoreSeconds / Math.Max(1, s.ObservedSeconds);
        return $"{(s.Service ? "Service, " : "")}{hours:0.#} h observed, in front {front:0.##} % of the time, {avg:0.##} cores on average, " +
               $"bursts to {P95(s):0.#} (95th percentile), {s.ChildStarts / Math.Max(1.0 / 60, hours):0.#} child processes per hour";
    }

    public List<BackgroundRowDto> Rows(BackgroundSettings settings, GameModeSettings game)
    {
        var excluded = Excluded(settings, game);
        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return [.. _stats
            .Where(kv => kv.Value.ObservedSeconds > 60)
            .OrderByDescending(kv => kv.Value.CoreSeconds)
            .Take(300)
            .Select(kv =>
            {
                var s = kv.Value;
                var (kind, label) = Decide(kv.Key, s, settings, excluded, unix);
                return new BackgroundRowDto(s.Display, kv.Key, Math.Round(s.ObservedSeconds / 60, 1),
                    Math.Round(100 * s.ForegroundSeconds / Math.Max(1, s.ObservedSeconds), 2),
                    Math.Round(s.CoreSeconds / Math.Max(1, s.ObservedSeconds), 2), Math.Round(s.PeakCores, 2), Math.Round(P95(s), 2), s.Service,
                    label, kind is "single" or "multi" ? "auto (sticky, least-loaded cores)" : null, Describe(s));
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
            {
                foreach (var s in d.Values)
                    if (s.Profile.Length != Bands.Length) s.Profile = new double[Bands.Length];
                return new Dictionary<string, ProgramStats>(d, StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new Dictionary<string, ProgramStats>(StringComparer.Ordinal);
    }
}
