using Corewright.Core.Autopilot;
using Corewright.Core.Engine;
using Corewright.Core.Rules;
using Corewright.Service.Windows;

namespace Corewright.Service;

public sealed class ServiceOptions
{
    public int Port { get; set; } = 47830;
    public int ActiveIntervalMs { get; set; } = 1000;
    public int IdleIntervalMs { get; set; } = 2000;
    public string? DataDirectory { get; set; }
}

/// <summary>
/// The single loop of the service: sample, evaluate, record history, sleep. Every other component reads
/// state under <see cref="Sync"/>. The interval is short only while an API client is active.
/// </summary>
internal sealed class Monitor : BackgroundService
{
    public const int HistoryCapacity = 300;

    private readonly ProcessSampler _sampler = new();
    private readonly WindowsActuator _actuator = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ServiceOptions _options;
    private readonly RuleStore _store;
    private readonly ILogger<Monitor> _log;
    private readonly EvalContext _ctx = new();
    private readonly Dictionary<string, string> _facts = new(StringComparer.OrdinalIgnoreCase);
    private long _lastClientMs;
    private int _reportedForegroundPid;
    private long _reportedForegroundMs;

    // History ring.
    private readonly NetworkSampler _network = new();
    public DeviceMonitor Devices { get; } = new();
    private readonly CoreUsageSampler _cores = new();
    private ProcessNetworkMonitor? _processNetwork;
    private long _networkLastUse = long.MinValue / 2;
    private bool _networkFailed;
    private bool _networkDemand, _networkFieldsActive, _networkStopReported;
    private int _networkVersion = -1;
    private long _networkStopRetry;
    public string? NetworkError { get; private set; }
    public void TouchNetwork() => Interlocked.Exchange(ref _networkLastUse, Environment.TickCount64);

    private readonly long[] _hTime = new long[HistoryCapacity];
    private readonly float[] _hGpu = new float[HistoryCapacity];
    private readonly float[] _hNetRx = new float[HistoryCapacity];
    private readonly float[] _hNetTx = new float[HistoryCapacity];
    private readonly float[] _hCpu = new float[HistoryCapacity];
    private readonly float[] _hMem = new float[HistoryCapacity];
    private readonly float[] _hIo = new float[HistoryCapacity];
    private int _hCount, _hNext;

    public Monitor(ServiceOptions options, RuleStore store, SettingsStore settingsStore, EventBuffer events, ILogger<Monitor> log)
    {
        _options = options;
        _store = store;
        _log = log;
        Devices.Start(Sync, log);
        Events = events;
        Engine = new RuleEngine(_actuator, events);
        _actuator.Network.Start(events);
        // Events carry the rule name; the per-rule history is kept by id (names can repeat across a rename).
        events.RuleIdOf = name =>
        {
            foreach (var r in Engine.Rules.Rules)
                if (string.Equals(r.Source.Name, name, StringComparison.Ordinal)) return r.Id;
            return null;
        };
        _ctx.Facts = _facts;
        ProcessInfo.CommandLineReader = SystemProbe.ReadCommandLine;
        SystemProbe.EnablePrivileges();
        var basic = SystemProbe.ReadTopology();
        try { CpuLayout = CpuTopologyProbe.Read(); }
        catch (Exception e) { log.LogWarning("CPU topology probe failed: {Message}", e.Message); }
        Topology = CpuLayout is { } layout ? WithLayout(basic, layout) : basic;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        _ctx.AlwaysGames = AlwaysGamesOf(Settings);
        _placer = new AffinityPlacer(() => Topology,
            () => AutopilotRules.GameCcd(Settings.Autopilot.GameMode.Placement, Topology),
            () => Settings.Autopilot.Background.KeepBestCoresFree,
            () => _groups.ExclusiveMask);
        _byPid = pid => _sampler.TryGet(pid, out var p) ? p : null;
        Engine.Placer = _placer;
        RuleSet = store.Load();
        var errors = new List<RuleError>();
        Engine.SetRules(CompileAll(RuleSet, Settings, errors));
        foreach (var e in errors)
            log.LogWarning("rule {Rule}: {Message}", e.RuleName, e.Message);
    }

    public Lock Sync { get; } = new();

    private readonly SettingsStore _settingsStore;
    private readonly GroupStore _groups = new();
    private readonly Func<int, ProcessInfo?> _byPid;
    private volatile bool _stopping;
    public GroupStore Groups => _groups;
    private readonly GameDetector _game = new();
    private BackgroundClassifier? _background;
    private readonly AffinityPlacer _placer;
    public AffinityPlacer Placer => _placer;

    /// <summary>Long-term program statistics; created on the first tick (needs the data folder).</summary>
    public BackgroundClassifier? Background => _background;
    private bool _reportedFullscreen;

    public AppSettings Settings { get; private set; }
    public GameDetector Game => _game;

    /// <summary>User rules plus the built-in Autopilot rules (lower priority, so user rules win). Errors only for enabled rules.</summary>
    private CompiledRuleSet CompileAll(RuleSet user, AppSettings settings, List<RuleError> errors)
    {
        var all = new RuleSet { Version = user.Version, Rules = [.. user.Rules, .. AutopilotRules.Build(settings.Autopilot, Topology, _background?.Verdicts), .. _groups.Rules()] };
        var raw = new List<RuleError>();
        var compiled = RuleCompiler.Compile(all, Topology, raw);
        errors.AddRange(raw.Where(e => all.Rules.Any(r => r.Id == e.RuleId && r.Enabled)));
        return compiled;
    }

    /// <summary>Adds CCDs, V-Cache and the CPPC ranking to the topology used by affinity tokens (ccd0, best-cores:4, ...).</summary>
    private static SystemTopology WithLayout(SystemTopology basic, TopologyDto layout)
    {
        static ulong Mask(IEnumerable<int> lps) => lps.Where(i => i < 64).Aggregate(0UL, (m, i) => m | (1UL << i));
        var ccdMasks = layout.Ccds.Select(c => Mask(c.Cores.SelectMany(k => k.Threads))).ToArray();
        var cores = layout.Ccds.SelectMany(c => c.Cores).ToList();
        var ranked = layout.RankingAvailable ? cores.Where(k => k.Rank > 0).OrderBy(k => k.Rank).Select(k => Mask(k.Threads)).ToArray() : [];
        int freq = -1;
        if (layout.RankingAvailable)
        {
            var top = cores.OrderBy(k => k.Rank).First();
            freq = layout.Ccds.ToList().FindIndex(c => c.Cores.Contains(top));
        }
        return new SystemTopology(basic.LogicalCount, basic.PerformanceMask, basic.EfficiencyMask)
        {
            CcdMasks = ccdMasks,
            VCacheCcds = [.. layout.Ccds.Where(c => c.VCache).Select(c => c.Id)],
            FrequencyCcd = freq,
            CoresByRank = ranked,
            PhysicalCores = cores.Select(k => Mask(k.Threads)).ToArray(),
        };
    }
    public RuleEngine Engine { get; }
    public EventBuffer Events { get; }
    public SystemTopology Topology { get; }
    public TopologyDto? CpuLayout { get; }
    public ProcessDetailsCache Details { get; } = new();
    /// <summary>Recent API calls that changed something or were rejected (Integrations).</summary>
    public ApiLog ApiCalls { get; } = new();
    public NetworkSampler Network => _network;
    public NetworkPolicies NetworkPolicies => _actuator.Network;
    public CoreUsageSampler Cores => _cores;
    public GpuReadings Gpu => Devices.Gpu;
    public RuleSet RuleSet { get; private set; }
    public ProcessSampler Sampler => _sampler;
    public EvalContext Context => _ctx;
    public IReadOnlyDictionary<string, string> Facts => _facts;
    public bool IsWindowsService { get; init; }
    public string DataDirectory { get; init; } = "";
    public int IntervalMs => IsClientActive ? _options.ActiveIntervalMs : _options.IdleIntervalMs;

    private bool IsClientActive => Environment.TickCount64 - Interlocked.Read(ref _lastClientMs) < 5000;

    /// <summary>Called for every API request: keeps the fast interval while someone is watching.</summary>
    public void TouchClient()
    {
        bool wasIdle = !IsClientActive;
        Interlocked.Exchange(ref _lastClientMs, Environment.TickCount64);
        if (wasIdle) Wake();
    }

    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public List<RuleError> ReplaceRules(RuleSet set)
    {
        var errors = new List<RuleError>();
        RuleCompiler.Compile(set, Topology, errors);
        if (errors.Count > 0) return errors;
        var compiled = CompileAll(set, Settings, errors);
        if (errors.Count > 0) return errors;
        lock (Sync)
        {
            _store.Save(set);
            RuleSet = set;
            Engine.SetRules(compiled);
        }
        Events.Prune(id => id.StartsWith(AutopilotRules.IdPrefix, StringComparison.Ordinal) || set.Rules.Any(r => r.Id == id));
        Wake();
        return errors;
    }

    /// <summary>Marked games count for rules even when Autopilot or game mode is off; "Never games" always wins.</summary>
    private static HashSet<string> AlwaysGamesOf(AppSettings s)
    {
        var set = new HashSet<string>(s.Autopilot.GameMode.Games, StringComparer.OrdinalIgnoreCase);
        set.ExceptWith(s.Autopilot.GameMode.NotGames);
        return set;
    }

    public List<RuleError> ReplaceSettings(AppSettings settings)
    {
        var errors = new List<RuleError>();
        var compiled = CompileAll(RuleSet, settings, errors);
        if (errors.Count > 0) return errors;
        lock (Sync)
        {
            _settingsStore.Save(settings);
            Settings = settings;
            _ctx.AlwaysGames = AlwaysGamesOf(settings);
            Engine.SetRules(compiled);
        }
        Wake();
        return errors;
    }

    public void SetFact(string key, string? value)
    {
        lock (Sync)
        {
            if (value is null) _facts.Remove(key);
            else _facts[key] = value;
        }
        Wake();
    }

    public void ReportForeground(int pid, bool fullscreen)
    {
        lock (Sync)
        {
            _reportedForegroundPid = pid;
            _reportedFullscreen = fullscreen;
            _reportedForegroundMs = Environment.TickCount64;
        }
        Wake();
    }

    public (long[] Time, float[] Cpu, float[] Mem, float[] Io, float[] Gpu, float[] NetRx, float[] NetTx) History()
    {
        lock (Sync)
        {
            var t = new long[_hCount];
            var c = new float[_hCount];
            var m = new float[_hCount];
            var io = new float[_hCount];
            var gpu = new float[_hCount];
            var rx = new float[_hCount];
            var tx = new float[_hCount];
            int start = (_hNext - _hCount + HistoryCapacity) % HistoryCapacity;
            for (int i = 0; i < _hCount; i++)
            {
                int j = (start + i) % HistoryCapacity;
                t[i] = _hTime[j];
                c[i] = _hCpu[j];
                m[i] = _hMem[j];
                io[i] = _hIo[j];
                gpu[i] = _hGpu[j];
                rx[i] = _hNetRx[j];
                tx[i] = _hNetTx[j];
            }
            return (t, c, m, io, gpu, rx, tx);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Corewright monitor started ({Mode}, {Count} logical processors, hybrid: {Hybrid})",
            IsWindowsService ? "service" : "console", Topology.LogicalCount, Topology.IsHybrid);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tick();
                try { await _wake.WaitAsync(IntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _stopping = true;
            lock (Sync)
            {
                Engine.RevertAll(stopping: true, onUnrestored: e =>
                    _log.LogWarning("Unrestored process {Pid} ({Process}), {Slot}, original {Value}: {Message}",
                        e.Pid, e.Process, e.Slot, e.Value, e.Message));
                _background?.Save();
            }
            _actuator.Dispose();
            Devices.Dispose();
            _processNetwork?.Dispose();
            if ((_processNetwork is null || _processNetwork.Stopped) && _actuator.Network.SessionStopped)
                _actuator.Network.Ownership.Dispose();
            else _log.LogWarning("Network session cleanup failed; machine lease retained until process exit: {Error}", _processNetwork?.Error);
            _sampler.Dispose();
            _log.LogInformation("Corewright monitor stopped; cleanup failures, if any, are reported above");
        }
    }

    private void Tick()
    {
        lock (Sync)
        {
            _sampler.Sample();
            _ctx.NowMs = Environment.TickCount64;
            _actuator.Network.RetryPending(_ctx.NowMs);
            bool ruleDemand = !Engine.Paused && Engine.Rules.NeedsNetwork;
            bool needNetwork = ruleDemand || (IsClientActive && _ctx.NowMs - Interlocked.Read(ref _networkLastUse) < 5000);
            if ((needNetwork && !_networkDemand) || _networkVersion != Engine.Version) _networkFailed = false;
            _networkDemand = needNetwork; _networkVersion = Engine.Version;
            if (_processNetwork is { Available: false } failed)
            {
                if (!_networkFailed) NetworkFailure(failed.Error);
                if (_ctx.NowMs >= _networkStopRetry) StopProcessNetwork();
            }
            if (needNetwork && _processNetwork is null && !_networkFailed)
            {
                _processNetwork = new ProcessNetworkMonitor(_actuator.Network.Ownership);
                NetworkError = _processNetwork.Error;
                if (!_processNetwork.Available)
                {
                    NetworkFailure(NetworkError);
                    StopProcessNetwork();
                }
            }
            if (_processNetwork is { Available: true } && !ruleDemand && _ctx.NowMs - Interlocked.Read(ref _networkLastUse) > 60_000)
                StopProcessNetwork();
            if (_processNetwork is { Available: true } active)
            {
                active.Sample(_sampler.Processes);
                _networkFieldsActive = true;
            }
            else if (_networkFieldsActive)
            {
                for (int i = 0; i < _sampler.Processes.Count; i++)
                { var p = _sampler.Processes[i]; p.NetAvailable = false; p.NetSend = p.NetReceive = 0; }
                _networkFieldsActive = false;
            }

            // A service in session 0 cannot see the desktop; the tray reports the foreground process.
            // In console mode the service can read it itself unless the tray reported recently.
            bool fullscreen;
            if (!IsWindowsService && _ctx.NowMs - _reportedForegroundMs > 10_000)
                (_ctx.ForegroundPid, fullscreen) = SystemProbe.ForegroundInfo();
            else
                (_ctx.ForegroundPid, fullscreen) = (_reportedForegroundPid, _reportedFullscreen);

            // Autopilot game mode: only re-classifies when the foreground or its full-screen state changes.
            var ap = Settings.Autopilot;
            if (!_groups.AnyFreeze && _game.Update(_sampler, _ctx.ForegroundPid, fullscreen, ap.GameMode, ap.Enabled && ap.GameMode.Enabled, Events))
            {
                _ctx.GamePid = _game.Game?.Pid ?? 0;
                _ctx.GameName = _game.GameName;
                _facts["game.active"] = _game.Game is null ? "false" : "true";
                if (_game.GameName is { } gn) _facts["game.name"] = gn; else _facts.Remove("game.name");
            }

            // PM-12: long-term observation; a changed classification rebuilds the rule set (at most once a minute).
            _background ??= new BackgroundClassifier(DataDirectory);
            // As a service the foreground comes from the tray: known once it reported, unknown after it said goodbye (pid 0).
            bool foregroundKnown = !IsWindowsService || (_reportedForegroundMs != 0 && _reportedForegroundPid > 0);
            if (_background.Observe(_sampler.Processes, _ctx.ForegroundPid, foregroundKnown, Topology.LogicalCount, _ctx.NowMs,
                    Settings.Autopilot.Background, Settings.Autopilot.GameMode, Topology, ap.Launchers)
                && Settings.Autopilot.Enabled && Settings.Autopilot.Background.Enabled)
            {
                var errors = new List<RuleError>();
                var compiled = CompileAll(RuleSet, Settings, errors);
                if (errors.Count == 0) Engine.SetRules(compiled);
                else _log.LogWarning("background placement rules rejected: {Message}", errors[0].Message);
            }

            _ctx.ForegroundRoot = _background.ForegroundRoot;
            // PM-39: process groups. Expiry rebuilds the rules; membership is decided once per new process.
            if (_groups.Expire(_ctx.NowMs)) RebuildRules();
            _groups.Resolve(_sampler.Processes, _byPid);
            Engine.FreezeAutopilot = _groups.AnyFreeze;
            if (_groups.AnyFreeze) _facts["autopilot.frozen"] = "true";
            else _facts.Remove("autopilot.frozen");
            _ctx.ForegroundBusy = _sampler.TryGet(_ctx.ForegroundPid, out var front) && front.CpuPercent * Topology.LogicalCount / 100 >= 0.5;
            if (IsClientActive) _cores.Sample(); // Loose masks do not need per-thread load.
            Engine.Evaluate(_sampler.Processes, _ctx);

            // Cheap system counters always; the costly ones (GPU counters, per-thread load) only while someone watches.
            Devices.BeginSample(IsClientActive);
            _network.Sample(Devices, IsClientActive);
            if (!IsClientActive)
            {
                Details.DropIfIdle();
            }

            Devices.Sample(_ctx.NowMs);
            _hTime[_hNext] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _hCpu[_hNext] = (float)_sampler.SystemCpuPercent;
            _hMem[_hNext] = _sampler.MemoryTotal == 0 ? 0 : (float)(100.0 * _sampler.MemoryUsed / _sampler.MemoryTotal);
            _hIo[_hNext] = (float)_sampler.TotalIoBytesPerSec;
            _hGpu[_hNext] = (float)Gpu.Utilization;
            _hNetRx[_hNext] = (float)_network.ReceiveBytesPerSec;
            _hNetTx[_hNext] = (float)_network.SendBytesPerSec;
            _hNext = (_hNext + 1) % HistoryCapacity;
            if (_hCount < HistoryCapacity) _hCount++;
        }
    }

    private void NetworkFailure(string? error)
    {
        NetworkError = error ?? "network ETW unavailable";
        _networkFailed = true;
        Events.Emit(new(EngineEventKind.Failed, 0, "", null, "network", null, NetworkError));
    }

    private void StopProcessNetwork()
    {
        if (_processNetwork is null) return;
        _processNetwork.Dispose();
        if (_processNetwork.Stopped)
        { _processNetwork = null; _networkStopRetry = 0; _networkStopReported = false; return; }
        NetworkError = _processNetwork.Error;
        _networkFailed = true; // Do not report this stop failure again as a consumer failure next tick.
        if (!_networkStopReported) Events.Emit(new(EngineEventKind.Failed, 0, "", null, "network", null, NetworkError));
        _networkStopReported = true;
        _networkStopRetry = Environment.TickCount64 + 60_000;
    }

    private void RebuildRules()
    {
        var errors = new List<RuleError>();
        var compiled = CompileAll(RuleSet, Settings, errors);
        if (errors.Count == 0) Engine.SetRules(compiled);
        else _log.LogWarning("rules rejected after a process group change: {Message}", errors[0].Message);
    }

    // ---- Process groups (PM-39) ------------------------------------------------------------------------------

    /// <summary>
    /// Registers or renews a group and applies its settings before returning: one fresh sample and evaluation, so a
    /// tool that starts its root suspended can resume it with the right settings already in place.
    /// </summary>
    public (int Status, object Body) PutGroup(string id, GroupRequestDto request)
    {
        if (_stopping) return (503, new GroupErrorDto("stopping", "The Corewright service is stopping."));
        if (id.Length is 0 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            return (400, new GroupErrorDto("invalid", "id: 1 to 64 letters, digits, '-', '_' or '.'"));
        if (GroupStore.Validate(request) is { } invalid) return (400, new GroupErrorDto("invalid", invalid));
        lock (Sync)
        {
            if (Engine.Paused)
            {
                if (_groups.Remove(id)) RebuildRules();
                return (503, new GroupErrorDto("engine-paused", "Corewright's rules are paused; nothing is applied."));
            }
            Tick(); // fresh sample: a root started a moment ago (even suspended) is known now
            var roots = request.Roots!.Select(r => new ProcessKey(r.Pid, r.CreationTime)).ToArray();
            foreach (var r in roots)
                if (!_sampler.TryGet(r.Pid, out var live) || live.Key != r)
                    return (404, new GroupErrorDto("root-not-found", $"No running process with pid {r.Pid} and that creation time."));

            _groups.TryGet(id, out var existing);
            ulong exclusive = 0;
            if (GroupStore.ExclusiveCount(request.Actions?.Affinity) is { } n)
            {
                if (existing is not null && GroupStore.Bits(existing.Exclusive) == n * Math.Max(1, CoreWidth()))
                    exclusive = existing.Exclusive; // renewal keeps its cores
                else if (_groups.PickExclusive(n, Topology, AutopilotRules.GameCcd(Settings.Autopilot.GameMode.Placement, Topology), id, out var why) is { } mask)
                    exclusive = mask;
                else
                    return (409, new GroupErrorDto("placement-unavailable", why));
            }
            var group = new GroupStore.Group
            {
                Id = id,
                Owner = request.Owner!.Trim(),
                Label = request.Label?.Trim() is { Length: > 0 } label ? label : id,
                Roots = roots,
                IncludeDescendants = request.IncludeDescendants,
                Freeze = request.FreezeAutopilot,
                ExpiresMs = Environment.TickCount64 + (request.TtlSeconds ?? 90) * 1000L,
                Actions = request.Actions,
                Exclusive = exclusive,
            };

            // The group's settings are validated exactly like a rule.
            var probe = new GroupStore();
            probe.Put(group);
            var errors = new List<RuleError>();
            RuleCompiler.Compile(new RuleSet { Rules = [.. probe.Rules()] }, Topology, errors);
            if (errors.Count > 0) return (400, new GroupErrorDto("invalid", errors[0].Message));

            bool changed = existing is null || !SameShape(existing, group);
            if (changed)
            {
                _groups.Put(group);
                RebuildRules();
                Tick(); // apply now
            }
            else
            {
                existing!.ExpiresMs = group.ExpiresMs; // plain renewal: nothing to re-apply
                group = existing;
            }

            // A root Corewright cannot change (not elevated, protected) is refused: the tool keeps its own controls.
            foreach (var r in roots)
            {
                var pending = Engine.GetPending(r);
                if (pending.Count > 0)
                {
                    _groups.Remove(id);
                    RebuildRules();
                    Tick();
                    return (503, new GroupErrorDto("pending",
                        $"{string.Join(", ", pending.Select(Slots.Name))} on pid {r.Pid} is not in place yet (an earlier network change is still being cleaned up); try again in a minute."));
                }
                var failed = Engine.GetFailed(r);
                if (failed.Count > 0)
                {
                    _groups.Remove(id);
                    RebuildRules();
                    Tick();
                    return (503, new GroupErrorDto("cannot-control",
                        $"Corewright could not set {string.Join(", ", failed.Select(f => Slots.Name(f.Slot)))} on pid {r.Pid}."));
                }
            }
            return (200, GroupView(group));
        }
    }

    /// <summary>Whole cores have this many logical processors (SMT); 1 when unknown.</summary>
    private int CoreWidth() => Topology.CoresByRank.Length > 0 ? System.Numerics.BitOperations.PopCount(Topology.CoresByRank[0]) : 1;

    private static bool SameShape(GroupStore.Group a, GroupStore.Group b) =>
        a.Owner == b.Owner && a.Label == b.Label && a.IncludeDescendants == b.IncludeDescendants && a.Freeze == b.Freeze &&
        a.Exclusive == b.Exclusive && a.Roots.SequenceEqual(b.Roots) && SameActions(a.Actions, b.Actions);

    private static bool SameActions(RuleActions? a, RuleActions? b) =>
        (a is null && b is null) || (a is not null && b is not null &&
        a.CpuPriority == b.CpuPriority && a.IoPriority == b.IoPriority && a.GpuPriority == b.GpuPriority &&
        a.MemoryPriority == b.MemoryPriority && a.EfficiencyMode == b.EfficiencyMode && a.MemoryLimitMb == b.MemoryLimitMb &&
        a.WorkingSetLimitMb == b.WorkingSetLimitMb && a.Affinity == b.Affinity &&
        a.NetworkBlocked == b.NetworkBlocked && a.UploadLimitMbps == b.UploadLimitMbps && a.NetworkPriority == b.NetworkPriority);

    public (int Status, object Body) GetGroup(string id)
    {
        lock (Sync)
            return _groups.TryGet(id, out var g) ? (200, GroupView(g)) : (404, new GroupErrorDto("unknown-group", null));
    }

    public GroupDto[] ListGroups()
    {
        lock (Sync) return [.. _groups.All.Select(GroupView)];
    }

    public bool DeleteGroup(string id)
    {
        lock (Sync)
        {
            if (!_groups.Remove(id)) return false;
            RebuildRules();
            Tick(); // put back what the group's settings changed
            return true;
        }
    }

    private GroupDto GroupView(GroupStore.Group g)
    {
        var warnings = new List<GroupWarningDto>();
        int count = 0;
        foreach (var p in _sampler.Processes)
        {
            if (p.GroupId != g.Id) continue;
            count++;
            foreach (var (slot, _) in Engine.GetFailed(p.Key))
                warnings.Add(new GroupWarningDto(p.Key.Pid, p.Name, Slots.Name(slot), "write refused (access denied or protected)"));
        }
        string? cpus = null, cpu = null, io = null, mem = null;
        foreach (var a in Engine.GetApplied(g.Roots[0]))
        {
            string v = Slots.FormatValue(a.Slot, a.Value);
            switch (a.Slot)
            {
                case Slot.Affinity: cpus = v; break;
                case Slot.CpuPriority: cpu = v; break;
                case Slot.IoPriority: io = v; break;
                case Slot.MemoryPriority: mem = v; break;
            }
        }
        if (cpus is null && g.Exclusive != 0) cpus = AffinityParser.Format(g.Exclusive);
        int left = (int)Math.Max(0, (g.ExpiresMs - Environment.TickCount64) / 1000);
        return new GroupDto(g.Id, g.Owner, g.Label, Engine.Paused ? "engine-paused" : _stopping ? "stopping" : "applied", g.Freeze,
            new GroupAppliedDto(cpus, cpu, io, mem), count, left, [.. warnings]);
    }

    public void Pause()
    {
        lock (Sync) Engine.Pause();
    }

    public void Resume()
    {
        lock (Sync) Engine.Resume();
        Wake();
    }
}
