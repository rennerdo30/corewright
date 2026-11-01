using Corewright.Core.Rules;

namespace Corewright.Core.Engine;

/// <summary>Writes slot values to real processes. Every call must verify the process identity (pid + creation time).</summary>
public interface IActuator
{
    /// <summary>Reads the current value so it can be restored later.</summary>
    bool TryRead(in ProcessKey key, Slot slot, out long value, out string? error);

    bool TryWrite(in ProcessKey key, Slot slot, long value, out string? error);

    /// <summary>A deferred write has not changed confirmed enforcement and must not be cached as a refusal.</summary>
    WriteResult Write(in ProcessKey key, Slot slot, long value, out string? error) =>
        new(TryWrite(key, slot, value, out error) ? WriteStatus.Succeeded : WriteStatus.Failed);

    /// <summary>The process exited or is no longer managed: release handles held for it.</summary>
    void Forget(in ProcessKey key);
}

public enum WriteStatus { Succeeded, Failed, Deferred }
public readonly record struct WriteResult(WriteStatus Status, long RetryAfterMs = 0);

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
public sealed class RuleEngine(IActuator actuator, IEngineEventSink sink, Func<long>? nowMs = null)
{
    private const int MaxDriftReapply = 3;
    private const int RestoreRetryMs = 60_000;
    private readonly Func<long> _nowMs = nowMs ?? (static () => Environment.TickCount64);

    private readonly Dictionary<ProcessKey, ProcState> _states = [];
    private readonly List<ProcessKey> _gone = [];
    /// <summary>Processes without engine state that were already checked for inherited values (paused / no rules).</summary>
    private readonly HashSet<ProcessKey> _inheritSeen = [];
    private readonly List<ProcessKey> _inheritGone = [];
    private CompiledRuleSet _set = CompiledRuleSet.Empty;
    private int _version;
    private long _tick;

    public bool Paused { get; private set; }

    /// <summary>
    /// A process group freezes Autopilot (PM-39, e.g. during a measurement): Autopilot makes no new changes and keeps
    /// the ones it made on other processes as they are; nothing is reverted mid-run.
    /// </summary>
    public bool FreezeAutopilot { get; set; }

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
        if (!Paused || _states.Count != 0)
        {
            bool haveRules = _set.Rules.Length > 0;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (p.IsProtected) continue;
                if (!_states.TryGetValue(p.Key, out var st))
                {
                    if (Paused || !haveRules)
                    {
                        // No state is created now, but a child of a process the engine still holds modified (a restore
                        // is pending) inherited that value: put it back, once per process.
                        if (p.Parent is { } pk && _states.ContainsKey(pk) && _inheritSeen.Add(p.Key)) UndoInherited(p, 0);
                        continue;
                    }
                    st = new ProcState(p.Key, p.Name);
                    _states.Add(p.Key, st);
                }
                st.Seen = _tick;
                if (Paused)
                {
                    for (int s = 0; s < Slots.Count; s++)
                        if (st.Owned[s]) Revert(st, s);
                    if (!HasOwned(st))
                    {
                        actuator.Forget(st.Key);
                        _states.Remove(st.Key);
                    }
                }
                else EvaluateProcess(p, st, ctx);
            }
        }

        if (_inheritSeen.Count > 0)
        {
            foreach (var key in _inheritSeen)
                if (!_states.ContainsKey(key) && !IsListed(processes, key)) _inheritGone.Add(key);
            foreach (var key in _inheritGone) _inheritSeen.Remove(key);
            _inheritGone.Clear();
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

    private static bool IsListed(IReadOnlyList<ProcessInfo> processes, ProcessKey key)
    {
        for (int i = 0; i < processes.Count; i++)
            if (processes[i].Key == key) return true;
        return false;
    }

    /// <summary>Reverts every slot the engine applied and stops applying until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        if (Paused) return;
        RevertAll();
        Paused = true;
        sink.Emit(new EngineEvent(EngineEventKind.Info, 0, "", null, null, null, "rules paused; releases requested"));
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        _version++;
        sink.Emit(new EngineEvent(EngineEventKind.Info, 0, "", null, null, null, "rules resumed"));
    }

    /// <summary>Retains failed restores for the sampler to retry. Shutdown reports the remaining ownership.</summary>
    public void RevertAll(bool stopping = false, Action<EngineEvent>? onUnrestored = null)
    {
        foreach (var st in _states.Values)
        {
            for (int s = 0; s < Slots.Count; s++)
                if (st.Owned[s]) Revert(st, s);
            if (!HasOwned(st))
            {
                actuator.Forget(st.Key);
                _gone.Add(st.Key);
            }
            else if (stopping)
            {
                // The slot's failure event was emitted once. This summary reports what survives shutdown.
                for (int s = 0; s < Slots.Count; s++)
                    if (st.Owned[s])
                    {
                        var e = new EngineEvent(EngineEventKind.Info, st.Key.Pid, st.Name,
                            st.AppliedRule[s]?.Name, Slots.Name((Slot)s), Slots.FormatValue((Slot)s, st.Original[s]),
                            "shutdown: original value could not be restored; ownership remains unresolved");
                        sink.Emit(e);
                        onUnrestored?.Invoke(e); // Report inline; the bounded Activity ring may overwrite older entries.
                    }
            }
        }
        foreach (var key in _gone) _states.Remove(key);
        _gone.Clear();
    }

    private static bool HasOwned(ProcState st)
    {
        for (int s = 0; s < Slots.Count; s++)
            if (st.Owned[s]) return true;
        return false;
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

    /// <summary>Slots whose last write to this process failed (with the value that was refused).</summary>
    public List<(Slot Slot, long Value)> GetFailed(ProcessKey key)
    {
        var list = new List<(Slot, long)>();
        if (!_states.TryGetValue(key, out var st)) return list;
        for (int s = 0; s < Slots.Count; s++)
            if (st.Failed[s]) list.Add(((Slot)s, st.FailedValue[s]));
        return list;
    }

    /// <summary>Slots whose write is postponed (network cleanup backoff): requested but not confirmed in Windows yet.</summary>
    public List<Slot> GetPending(ProcessKey key)
    {
        var list = new List<Slot>();
        if (!_states.TryGetValue(key, out var st)) return list;
        for (int s = 0; s < Slots.Count; s++)
            if ((st.Pending & (1 << s)) != 0 && !(st.IsApplied[s] && (st.RevertFailed & (1 << s)) != 0)) list.Add((Slot)s);
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
            // A grouped process belongs to the tool that registered it: only group-aware rules apply (PM-39).
            // While Autopilot is frozen, its rules make no new decisions.
            if ((p.GroupId is not null && !r.GroupAware) || (FreezeAutopilot && r.IsAutopilot))
            {
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

        if (!st.InheritChecked)
        {
            st.InheritChecked = true;
            UndoInherited(p, claimed);
        }

        for (int s = 0; s < Slots.Count; s++)
        {
            bool has = (claimed & (1 << s)) != 0;
            // Frozen: what Autopilot set on a process outside any group stays exactly as it is.
            if (!has && FreezeAutopilot && p.GroupId is null && st.IsApplied[s] && st.AppliedRule[s] is { IsAutopilot: true }) continue;
            Reconcile(p, st, s, has, want[s], has ? rules[wantRule[s]] : null);
        }
    }

    /// <summary>
    /// Windows gives a new process its parent's CPU cores, and an idle or below-normal priority class. When the engine
    /// changed the parent, a child started afterwards would keep the engine's value without the engine owning it, and
    /// nothing would ever put it back (2026-11-02: agent shells confined to two threads). So a child that no rule
    /// claims for that setting gets the parent's original value back, once, when it first appears.
    /// </summary>
    private void UndoInherited(ProcessInfo p, int claimed)
    {
        if (p.Parent is not { } parentKey || !_states.TryGetValue(parentKey, out var parent)) return;
        ReadOnlySpan<Slot> inherited = [Slot.Affinity, Slot.CpuPriority];
        foreach (var slot in inherited)
        {
            int s = (int)slot;
            if ((claimed & (1 << s)) != 0 || !parent.IsApplied[s]) continue;
            // The parent has been modified without interruption since ModifiedSince; a child started before that
            // got the parent's own value, not ours.
            if (p.Key.CreateTime <= parent.ModifiedSince[s]) continue;
            // The value the child could have inherited: the current one, or the previous one if the engine moved it
            // after the child started (the placer moves affinity).
            long inheritedValue = p.Key.CreateTime < parent.ChangedAt[s] ? parent.PreviousApplied[s] : parent.Applied[s];
            if (slot == Slot.CpuPriority && !Slots.IsInheritedPriority(inheritedValue)) continue;
            if (!actuator.TryRead(p.Key, slot, out long current, out _) || current != inheritedValue) continue;
            if (actuator.TryWrite(p.Key, slot, parent.Original[s], out _))
                sink.Emit(new EngineEvent(EngineEventKind.Reverted, p.Key.Pid, p.Name, parent.AppliedRule[s]?.Name, Slots.Name(slot),
                    Slots.FormatValue(slot, parent.Original[s]), $"inherited from {parent.Name}; put back"));
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
            if (st.Owned[s]) Revert(st, s);
            return;
        }

        if (st.IsApplied[s] && st.Applied[s] == value && (st.Pending & (1 << s)) == 0)
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
        if ((st.Pending & (1 << s)) != 0 && _nowMs() < st.ApplyRetry[s]) return;

        if (!st.Owned[s])
        {
            if (!actuator.TryRead(p.Key, slot, out var original, out var readErr))
            {
                Fail(p, st, s, value, rule!, readErr);
                return;
            }
            st.Original[s] = original;
        }

        var result = actuator.Write(p.Key, slot, value, out var err2);
        if (result.Status == WriteStatus.Deferred)
        {
            st.Owned[s] = true; // Preserve the first original, even before the first confirmed apply.
            st.Pending |= 1 << s;
            st.ApplyRetry[s] = result.RetryAfterMs;
        }
        else if (result.Status == WriteStatus.Succeeded)
        {
            long nowFt = DateTime.UtcNow.ToFileTimeUtc();
            // Inheritance bookkeeping: the modification interval starts when the slot first leaves its original and
            // only ends with a confirmed restore; a reapply of the same value after a failed restore keeps it.
            if (!st.IsApplied[s]) { st.ModifiedSince[s] = nowFt; st.PreviousApplied[s] = value; st.ChangedAt[s] = nowFt; }
            else if (st.Applied[s] != value) { st.PreviousApplied[s] = st.Applied[s]; st.ChangedAt[s] = nowFt; }
            st.Owned[s] = true;
            st.IsApplied[s] = true;
            st.Applied[s] = value;
            st.AppliedRule[s] = rule;
            st.Failed[s] = false;
            st.Pending &= ~(1 << s);
            // Reapply does not complete an unresolved restore. Keep its deadline and once-only report.
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
        long now = _nowMs();
        if (now < st.RestoreRetry[s]) return;
        var slot = (Slot)s;
        bool reported = (st.RevertFailed & (1 << s)) != 0;
        var result = actuator.Write(st.Key, slot, st.Original[s], out var err);
        bool ok = result.Status == WriteStatus.Succeeded;
        if (result.Status == WriteStatus.Deferred)
        {
            st.Pending |= 1 << s;
            st.ApplyRetry[s] = 0; // A rematch may replace the pending release, without performing backend I/O.
            st.RestoreRetry[s] = Math.Max(now + RestoreRetryMs, result.RetryAfterMs);
            return;
        }
        if (ok || !reported)
            sink.Emit(new EngineEvent(ok ? EngineEventKind.Reverted : EngineEventKind.Failed, st.Key.Pid, st.Name,
                st.AppliedRule[s]?.Name, Slots.Name(slot), Slots.FormatValue(slot, st.Original[s]),
                ok ? null : $"revert failed: {err}"));
        if (!ok)
        {
            st.RevertFailed |= 1 << s;
            st.Pending |= 1 << s;
            st.ApplyRetry[s] = 0;
            st.RestoreRetry[s] = now + RestoreRetryMs;
            return; // Every slot keeps ownership and its first original until confirmed restoration or exit.
        }
        st.RevertFailed &= ~(1 << s);
        st.IsApplied[s] = false;
        st.Owned[s] = false;
        st.Pending &= ~(1 << s);
        st.RestoreRetry[s] = 0;
        st.AppliedRule[s] = null;
    }

    private sealed class ProcState(ProcessKey key, string name)
    {
        public readonly ProcessKey Key = key;
        public readonly string Name = name;
        public long Seen;
        public int Version = -1;
        public int DriftCount;
        public int RevertFailed;
        public int Pending;

        // Per rule (resized when the rule set changes).
        public byte[] StaticCache = [];
        public long[] TrueSince = [];
        public long[] FalseSince = [];
        public bool[] Active = [];

        // Per slot.
        public readonly bool[] IsApplied = new bool[Slots.Count];
        public readonly bool[] Owned = new bool[Slots.Count];
        public readonly long[] ApplyRetry = new long[Slots.Count];
        public readonly long[] RestoreRetry = new long[Slots.Count];
        public readonly long[] Applied = new long[Slots.Count];
        public readonly long[] Original = new long[Slots.Count];
        /// <summary>FILETIME (UTC) when the slot left its original value; children created after it may have inherited ours.</summary>
        public readonly long[] ModifiedSince = new long[Slots.Count];
        /// <summary>FILETIME (UTC) of the last change of the applied value, and the value before it.</summary>
        public readonly long[] ChangedAt = new long[Slots.Count];
        public readonly long[] PreviousApplied = new long[Slots.Count];
        /// <summary>Inheritance from a changed parent was checked (once per process).</summary>
        public bool InheritChecked;
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
