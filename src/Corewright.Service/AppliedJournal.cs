using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Engine;
using Corewright.Core.Rules;
using Corewright.Service.Windows;

namespace Corewright.Service;

internal sealed record JournalEntry(int Pid, long CreationTime, Slot Slot, long Original, long Applied, RuleOrigin Origin, bool Restored = false);
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;

/// <summary>Bounded write-ahead log. Flush the recovery record before modifying a process; never per tick.</summary>
internal sealed class AppliedJournal : IAppliedJournal
{
    private const int MaxActive = 8192, CompactAfter = 16384;
    private const long MaxBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly ILogger _log;
    private readonly Dictionary<(ProcessKey Key, Slot Slot), JournalEntry> _active = [];
    private readonly List<(ProcessKey Key, Slot Slot)> _gone = [];
    private int _records;
    private bool _blocked;

    public AppliedJournal(string dataDirectory, IActuator actuator, IEngineEventSink sink, ILogger log)
    {
        _path = Path.Combine(dataDirectory, "applied-journal.jsonl"); _log = log;
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > MaxBytes) throw new IOException("recovery journal exceeds its safety bound");
                foreach (string line in File.ReadLines(_path))
                {
                    _records++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry);
                    if (entry is null) continue;
                    var id = (new ProcessKey(entry.Pid, entry.CreationTime), entry.Slot);
                    if (entry.Restored) _active.Remove(id); else _active[id] = entry;
                    if (_active.Count > MaxActive) throw new IOException("too many outstanding recovery entries");
                }
            }
            foreach (var (id, entry) in _active)
            {
                if (!actuator.TryRead(id.Key, id.Slot, out long current, out _)) continue;
                if (current != entry.Applied) { _gone.Add(id); continue; }
                bool ok = actuator.TryWrite(id.Key, id.Slot, entry.Original, out var error);
                sink.Emit(new(ok ? EngineEventKind.Reverted : EngineEventKind.Failed, entry.Pid, "", "Startup recovery", Slots.Name(entry.Slot),
                    Slots.FormatValue(entry.Slot, entry.Original), ok ? "restored original from applied-changes journal" : error));
                if (ok) _gone.Add(id);
            }
            foreach (var id in _gone) _active.Remove(id);
            _gone.Clear();
            Compact();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { _blocked = true; log.LogWarning("Applied journal preserved; new changes blocked: {Message}", e.Message); }
    }

    public long OriginalOf(ProcessKey key, Slot slot, long current) =>
        _active.TryGetValue((key, slot), out var entry) && current == entry.Applied ? entry.Original : current;

    public bool BeforeApply(ProcessKey key, Slot slot, long original, long applied, RuleOrigin origin, out string? error)
    {
        error = null;
        // Networking already has a machine-wide recovery journal and its own ownership protocol.
        if (NetworkPolicies.IsSlot(slot)) return true;
        var id = (key, slot);
        if (_blocked) { error = "recovery journal unavailable; change declined"; return false; }
        if (!_active.ContainsKey(id) && _active.Count >= MaxActive) { error = "recovery journal full; change declined"; return false; }
        var entry = new JournalEntry(key.Pid, key.CreateTime, slot, original, applied, origin);
        if (_active.TryGetValue(id, out var old) && old == entry) return true;
        try { Append(entry); _active[id] = entry; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return false; }
    }

    public void Reverted(ProcessKey key, Slot slot)
    {
        if (!_active.TryGetValue((key, slot), out var entry)) return;
        try { Append(entry with { Restored = true }); _active.Remove((key, slot)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _log.LogWarning("Recovery completion not saved: {Message}", e.Message); }
    }

    public void Forget(ProcessKey key)
    {
        for (int s = 0; s < Slots.Count; s++) Reverted(key, (Slot)s);
    }

    private void Append(JournalEntry entry)
    {
        if (_records >= CompactAfter || (File.Exists(_path) && new FileInfo(_path).Length >= MaxBytes - 1024)) Compact();
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, entry, JournalJson.Default.JournalEntry);
        stream.WriteByte((byte)'\n'); stream.Flush(flushToDisk: true); _records++;
    }

    private void Compact()
    {
        string tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var entry in _active.Values)
            { JsonSerializer.Serialize(stream, entry, JournalJson.Default.JournalEntry); stream.WriteByte((byte)'\n'); }
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true); _records = _active.Count;
    }
}
