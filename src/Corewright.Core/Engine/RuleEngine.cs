using Corewright.Core.Rules;

namespace Corewright.Core.Engine;

/// <summary>Writes slot values to real processes. Every call must verify the process identity (pid + creation time).</summary>
public interface IActuator
{
    /// <summary>Reads the current value so it can be restored later.</summary>
    bool TryRead(in ProcessKey key, Slot slot, out long value, out string? error);

    bool TryWrite(in ProcessKey key, Slot slot, long value, out string? error);

    /// <summary>The process exited or is no longer managed: release handles held for it.</summary>
    void Forget(in ProcessKey key);
}

public enum EngineEventKind { Applied, Reverted, Failed, Drift, Info }

public readonly record struct EngineEvent(
    EngineEventKind Kind, int Pid, string Process, string? Rule, string? Slot, string? Value, string? Message);

public interface IEngineEventSink
{
    void Emit(in EngineEvent e);
}

public sealed record AppliedSlot(Slot Slot, long Value, string RuleId, string RuleName);

public sealed record ExplainEntry(
    string RuleId, string RuleName, int Priority, bool Matches, bool Active, string[] Won, string[] Lost, string? Unavailable = null);

/// <summary>
/// Evaluates compiled rules against processes and reconciles each slot: the highest-priority active rule
/// that sets a slot owns it; a slot nobody owns any more is restored to the value it had before the engine
/// first wrote it. Not thread-safe; the caller serializes access.
/// </summary>
public sealed class RuleEngine(IActuator actuator, IEngineEventSink sink)
{
    private const int MaxDriftReapply = 3;

    private readonly Dictionary<ProcessKey, ProcState> _states = [];
    private readonly List<ProcessKey> _gone = [];
    private CompiledRuleSet _set = CompiledRuleSet.Empty;
    private int _version;
    private long _tick;

    public bool Paused { get; private set; }

    /// <summary>Resolves "auto-…" affinity modes; without one they fall back to leaving affinity unchanged.</summary>
    public IAffinityPlacer? Placer { get; set; }
    public int Version => _version;
    public CompiledRuleSet Rules => _set;

    public void SetRules(CompiledRuleSet set)
    {
        _set = set;
        _version++;
    }

    private EvalContext? _ctx;

    public void Evaluate(IReadOnlyList<ProcessInfo> processes, EvalContext ctx)
    {
        _ctx = ctx;
        _tick++;
        if (!Paused)
        {
            bool haveRules = _set.Rules.Length > 0;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (p.IsProtected) continue;
                if (!_states.TryGetValue(p.Key, out var st))
                {
                    if (!haveRules) continue;
                    st = new ProcState(p.Key, p.Name);
                    _states.Add(p.Key, st);
                }
                st.Seen = _tick;
                EvaluateProcess(p, st, ctx);
            }
        }

        foreach (var (key, st) in _states)
            if (st.Seen != _tick) _gone.Add(key);
        foreach (var key in _gone)
        {
            actuator.Forget(key);
            _states.Remove(key);
        }
        _gone.Clear();
    }

    /// <summary>Reverts every slot the engine applied and stops applying until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        if (Paused) return;
        RevertAll();
        Paused = true;
        sink.Emit(new EngineEvent(EngineEventKind.Info, 0, "", null, null, null, "rules paused, all changes reverted"));
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        _version++;
        sink.Emit(new EngineEvent(EngineEventKind.Info, 0, "", null, null, null, "rules resumed"));
    }

    public void RevertAll()
    {
        foreach (var st in _states.Values)
        {
            for (int s = 0; s < Slots.Count; s++)
                if (st.IsApplied[s]) Revert(st, s);
            actuator.Forget(st.Key);
        }
        _states.Clear();
    }

    public List<AppliedSlot> GetApplied(ProcessKey key)
    {
        var list = new List<AppliedSlot>();
        if (!_states.TryGetValue(key, out var st)) return list;
        for (int s = 0; s < Slots.Count; s++)
        {
            var r = st.AppliedRule[s];
            if (st.IsApplied[s] && r is not null) list.Add(new AppliedSlot((Slot)s, st.Applied[s], r.Id, r.Name));
        }
        return list;
    }

    public bool HasApplied(ProcessKey key)
    {
        if (!_states.TryGetValue(key, out var st)) return false;
        for (int s = 0; s < Slots.Count; s++)
            if (st.IsApplied[s]) return true;
        return false;
    }

    /// <summary>Which rules match a process right now and which slots each one wins or loses.</summary>
    public List<ExplainEntry> Explain(ProcessInfo p, EvalContext ctx)
    {
        var rules = _set.Rules;
        _states.TryGetValue(p.Key, out var st);
        var owner = new CompiledRule?[Slots.Count];
        var result = new List<ExplainEntry>(rules.Length);

        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            bool unavailable = r.UsesNetwork && !p.NetAvailable;
            bool matches = !unavailable && r.Match(p, ctx);
            bool active = st is not null && st.Version == _version && i < st.Active.Length
                ? (r.SustainMs > 0 || r.ReleaseMs > 0 ? st.Active[i] : matches)
                : matches;
            if (unavailable) active = false;
            var won = new List<string>();
            var lost = new List<string>();
            for (int s = 0; s < Slots.Count; s++)
            {
                if (!r.Sets((Slot)s)) continue;
                if (!active) continue;
                if (owner[s] is null) { owner[s] = r; won.Add(Slots.Name((Slot)s)); }
                else lost.Add($"{Slots.Name((Slot)s)} (to {owner[s]!.Name})");
            }
            result.Add(new ExplainEntry(r.Id, r.Name, r.Priority, matches, active, [.. won], [.. lost], unavailable ? "network data unavailable; needs the installed service" : null));
        }
        return result;
    }

    private void EvaluateProcess(ProcessInfo p, ProcState st, EvalContext ctx)
    {
        var rules = _set.Rules;
        if (st.Version != _version) st.Reset(rules.Length, _version);

        Span<long> want = stackalloc long[Slots.Count];
        Span<int> wantRule = stackalloc int[Slots.Count];
        wantRule.Fill(-1);
        int claimed = 0;

        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            if (r.UsesNetwork && !p.NetAvailable) { st.ClearTiming(i); continue; }
            if ((r.SlotMask & ~claimed) == 0)
            {
                // Every slot of this rule is already owned by a higher rule: skip the evaluation entirely.
                st.ClearTiming(i);
                continue;
            }
            if (!IsActive(r, i, p, st, ctx)) continue;
            for (int s = 0; s < Slots.Count; s++)
            {
                int bit = 1 << s;
                if ((r.SlotMask & bit) == 0 || (claimed & bit) != 0) continue;
                claimed |= bit;
                want[s] = r.Values[s];
                wantRule[s] = i;
            }
        }

        for (int s = 0; s < Slots.Count; s++)
        {
            bool has = (claimed & (1 << s)) != 0;
            Reconcile(p, st, s, has, want[s], has ? rules[wantRule[s]] : null);
        }
    }

    private static bool IsActive(CompiledRule r, int i, ProcessInfo p, ProcState st, EvalContext ctx)
    {
        bool m;
        if (r.IsDynamic)
        {
            m = r.Match(p, ctx);
        }
        else
        {
            byte cached = st.StaticCache[i];
            if (cached == 0)
            {
                cached = r.Match(p, ctx) ? (byte)1 : (byte)2;
                st.StaticCache[i] = cached;
            }
            m = cached == 1;
        }

        if (r.SustainMs <= 0 && r.ReleaseMs <= 0) return m;

        long now = ctx.NowMs;
        bool active = st.Active[i];
        if (m)
        {
            st.FalseSince[i] = 0;
            if (!active)
            {
                if (st.TrueSince[i] == 0) st.TrueSince[i] = now;
                if (now - st.TrueSince[i] >= r.SustainMs) active = true;
            }
        }
        else
        {
            st.TrueSince[i] = 0;
            if (active)
            {
                if (st.FalseSince[i] == 0) st.FalseSince[i] = now;
                if (now - st.FalseSince[i] >= r.ReleaseMs) active = false;
            }
        }
        st.Active[i] = active;
        return active;
    }

    private void Reconcile(ProcessInfo p, ProcState st, int s, bool has, long value, CompiledRule? rule)
    {
        var slot = (Slot)s;
        if (has && slot == Slot.Affinity && AffinityModes.IsDynamic(value))
        {
            // The placer keeps a process where it is unless there is a strong reason to move it (cache locality).
            if (Placer is null) has = false;
            else value = Placer.Resolve(p, value, _ctx ?? new EvalContext());
            if (value == 0) has = false;
        }
        if (!has)
        {
            if (st.IsApplied[s]) Revert(st, s);
            return;
        }

        if (st.IsApplied[s] && st.Applied[s] == value)
        {
            st.AppliedRule[s] = rule;
            if (slot == Slot.CpuPriority && p.BasePriority != 0 &&
                p.BasePriority != Slots.BasePriorityOf((CpuPriorityLevel)value) && st.DriftCount < MaxDriftReapply)
            {
                st.DriftCount++;
                bool ok = actuator.TryWrite(p.Key, slot, value, out var err);
                sink.Emit(new EngineEvent(EngineEventKind.Drift, p.Key.Pid, p.Name, rule!.Name, Slots.Name(slot),
                    Slots.FormatValue(slot, value),
                    ok ? $"process changed its priority; re-applied ({st.DriftCount}/{MaxDriftReapply})" : err));
            }
            return;
        }

        if (st.Failed[s] && st.FailedValue[s] == value) return;

        if (!st.IsApplied[s])
        {
            if (!actuator.TryRead(p.Key, slot, out var original, out var readErr))
            {
                Fail(p, st, s, value, rule!, readErr);
                return;
            }
            st.Original[s] = original;
        }

        if (actuator.TryWrite(p.Key, slot, value, out var err2))
        {
            st.IsApplied[s] = true;
            st.Applied[s] = value;
            st.AppliedRule[s] = rule;
            st.Failed[s] = false;
            sink.Emit(new EngineEvent(EngineEventKind.Applied, p.Key.Pid, p.Name, rule!.Name, Slots.Name(slot),
                Slots.FormatValue(slot, value), null));
        }
        else
        {
            Fail(p, st, s, value, rule!, err2);
        }
    }

    private void Fail(ProcessInfo p, ProcState st, int s, long value, CompiledRule rule, string? error)
    {
        st.Failed[s] = true;
        st.FailedValue[s] = value;
        var slot = (Slot)s;
        // A setting that does not apply to this process (GPU priority without a GPU context) is not an error worth a log line.
        if (error is not null && error.StartsWith("not applicable", StringComparison.Ordinal)) return;
        sink.Emit(new EngineEvent(EngineEventKind.Failed, p.Key.Pid, p.Name, rule.Name, Slots.Name(slot),
            Slots.FormatValue(slot, value), error));
    }

    private void Revert(ProcState st, int s)
    {
        var slot = (Slot)s;
        bool ok = actuator.TryWrite(st.Key, slot, st.Original[s], out var err);
        sink.Emit(new EngineEvent(ok ? EngineEventKind.Reverted : EngineEventKind.Failed, st.Key.Pid, st.Name,
            st.AppliedRule[s]?.Name, Slots.Name(slot), Slots.FormatValue(slot, st.Original[s]),
            ok ? null : $"revert failed: {err}"));
        st.IsApplied[s] = false;
        st.AppliedRule[s] = null;
    }

    private sealed class ProcState(ProcessKey key, string name)
    {
        public readonly ProcessKey Key = key;
        public readonly string Name = name;
        public long Seen;
        public int Version = -1;
        public int DriftCount;

        // Per rule (resized when the rule set changes).
        public byte[] StaticCache = [];
        public long[] TrueSince = [];
        public long[] FalseSince = [];
        public bool[] Active = [];

        // Per slot.
        public readonly bool[] IsApplied = new bool[Slots.Count];
        public readonly long[] Applied = new long[Slots.Count];
        public readonly long[] Original = new long[Slots.Count];
        public readonly CompiledRule?[] AppliedRule = new CompiledRule?[Slots.Count];
        public readonly bool[] Failed = new bool[Slots.Count];
        public readonly long[] FailedValue = new long[Slots.Count];

        public void Reset(int ruleCount, int version)
        {
            Version = version;
            DriftCount = 0;
            if (StaticCache.Length != ruleCount)
            {
                StaticCache = new byte[ruleCount];
                TrueSince = new long[ruleCount];
                FalseSince = new long[ruleCount];
                Active = new bool[ruleCount];
            }
            else
            {
                Array.Clear(StaticCache);
                Array.Clear(TrueSince);
                Array.Clear(FalseSince);
                Array.Clear(Active);
            }
            Array.Clear(Failed);
        }

        public void ClearTiming(int i)
        {
            TrueSince[i] = 0;
            FalseSince[i] = 0;
            Active[i] = false;
        }
    }
}
