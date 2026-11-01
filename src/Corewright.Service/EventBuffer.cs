using Corewright.Core.Engine;

namespace Corewright.Service;

public sealed record EventDto(long Seq, long Time, string Kind, int Pid, string Process, string? Rule, string? Slot, string? Value, string? Message);

/// <summary>Fixed-size ring of engine events; clients poll with the last sequence number they saw.</summary>
internal sealed class EventBuffer : IEngineEventSink
{
    private const int Capacity = 1000;
    private readonly EventDto?[] _ring = new EventDto?[Capacity];
    private readonly Lock _lock = new();
    private long _seq;

    public void Emit(in EngineEvent e)
    {
        lock (_lock)
        {
            _seq++;
            _ring[_seq % Capacity] = new EventDto(_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                e.Kind.ToString().ToLowerInvariant(), e.Pid, e.Process, e.Rule, e.Slot, e.Value, e.Message);
        }
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
