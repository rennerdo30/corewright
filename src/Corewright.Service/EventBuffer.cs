using Corewright.Core.Engine;

namespace Corewright.Service;

public sealed record EventDto(long Seq, long Time, string Kind, int Pid, string Process, string? Rule, string? Slot, string? Value, string? Message);

/// <summary>One time a rule changed a process: what it set there at that moment (owner, 2026-11-02: "show what processes
/// were affected by the rule the last 10 times or so").</summary>
public sealed record RuleHitDto(long Time, int Pid, string Process, string Kind, Dictionary<string, string> Set, string? Message);

/// <summary>Fixed-size ring of engine events; clients poll with the last sequence number they saw.</summary>
internal sealed class EventBuffer : IEngineEventSink
{
    private const int Capacity = 1000;
    private const int HitsPerRule = 10;
    private readonly EventDto?[] _ring = new EventDto?[Capacity];
    private readonly Lock _lock = new();
    private long _seq;

    // The last processes each rule changed, by rule id. Small and bounded: 10 entries per rule; rules that no longer
    // exist are dropped when the rule set changes (Prune). Separate from the ring so busy rules cannot push others out.
    private readonly Dictionary<string, List<RuleHitDto>> _hits = new(StringComparer.Ordinal);

    /// <summary>Rule name → id for the current rule set (set by the monitor). Events carry the name only.</summary>
    public Func<string, string?>? RuleIdOf { get; set; }

    public void Emit(in EngineEvent e)
    {
        lock (_lock)
        {
            _seq++;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string kind = e.Kind.ToString().ToLowerInvariant();
            _ring[_seq % Capacity] = new EventDto(_seq, now, kind, e.Pid, e.Process, e.Rule, e.Slot, e.Value, e.Message);
            if (e.Kind is EngineEventKind.Applied or EngineEventKind.Failed && e.Rule is { } name && e.Slot is { } slot &&
                RuleIdOf?.Invoke(name) is { } id)
                RecordHit(id, now, kind, e.Pid, e.Process, slot, e.Value ?? "", e.Message);
        }
    }

    /// <summary>Changes of one rule on one process within a second are one entry ("set CPU priority and cores").</summary>
    private void RecordHit(string ruleId, long now, string kind, int pid, string process, string slot, string value, string? message)
    {
        if (!_hits.TryGetValue(ruleId, out var list)) _hits[ruleId] = list = new List<RuleHitDto>(HitsPerRule);
        if (list.Count > 0 && list[^1] is var last && last.Pid == pid && last.Kind == kind && now - last.Time < 1000)
        {
            last.Set[slot] = value;
            return;
        }
        if (list.Count == HitsPerRule) list.RemoveAt(0);
        list.Add(new RuleHitDto(now, pid, process, kind, new Dictionary<string, string> { [slot] = value }, message));
    }

    /// <summary>Newest first.</summary>
    public RuleHitDto[] Hits(string ruleId)
    {
        lock (_lock)
        {
            if (!_hits.TryGetValue(ruleId, out var list)) return [];
            var copy = new RuleHitDto[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var h = list[list.Count - 1 - i];
                copy[i] = h with { Set = new Dictionary<string, string>(h.Set) };
            }
            return copy;
        }
    }

    /// <summary>Forgets rules that are gone (called when the rule set is replaced).</summary>
    public void Prune(Func<string, bool> exists)
    {
        lock (_lock)
            foreach (var id in _hits.Keys.Where(k => !exists(k)).ToList()) _hits.Remove(id);
    }

    public (long Last, EventDto[] Events) Since(long after, int max = 500)
    {
        lock (_lock)
        {
            long from = Math.Max(after + 1, Math.Max(1, _seq - Capacity + 1));
            from = Math.Max(from, _seq - max + 1);
            var list = new List<EventDto>((int)Math.Max(0, _seq - from + 1));
            for (long s = from; s <= _seq; s++)
                if (_ring[s % Capacity] is { } e) list.Add(e);
            return (_seq, [.. list]);
        }
    }
}
