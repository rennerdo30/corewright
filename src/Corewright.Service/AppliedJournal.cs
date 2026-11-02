using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Engine;
using Corewright.Core.Rules;
using Corewright.Service.Windows;

namespace Corewright.Service;

internal sealed record JournalEntry(int Pid, long CreationTime, Slot Slot, long Original, long Applied, RuleOrigin Origin, bool Restored = false, long? PreviousApplied = null);
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;

/// <summary>
/// Bounded write-ahead log of what the engine changed, so a crashed service can put processes back at its next start.
/// <list type="bullet">
/// <item>Written before a process is modified, never per tick. Records go to Windows with a plain write, not a forced
/// disk flush: the log only matters when the service dies while the processes live on, and data already handed to
/// Windows survives a crashed writer. A power loss also ends every process it would describe. So no disk I/O waits
/// under <c>Monitor.Sync</c> (review 2026-11-02, M3).</item>
/// <item>A damaged line (crash during an append) ends parsing; the valid records before it are recovered, the damaged
/// file is kept as <c>applied-journal.damaged-*.jsonl</c>, and a fresh log continues. One bad line must never block
/// every later rule (review H1). Size limits stop new entries, never recovery.</item>
/// </list>
/// </summary>
internal sealed class AppliedJournal : IAppliedJournal, IDisposable
{
    private const int MaxActive = 8192, CompactAfter = 16384;
    private const long MaxBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly ILogger _log;
    private readonly Dictionary<(ProcessKey Key, Slot Slot), JournalEntry> _active = [];
    private readonly List<(ProcessKey Key, Slot Slot)> _gone = [];
    private FileStream? _stream;
    private int _records;
    private bool _blocked;

    /// <summary>Why new changes are declined, or null. Shown in status.</summary>
    public string? Problem { get; private set; }

    public AppliedJournal(string dataDirectory, IActuator actuator, IEngineEventSink sink, ILogger log)
    {
        _path = Path.Combine(dataDirectory, "applied-journal.jsonl"); _log = log;
        bool damaged = false;
        try
        {
            if (File.Exists(_path))
            {
                foreach (string line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JournalEntry? entry;
                    try { entry = JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry); }
                    catch (JsonException) { damaged = true; break; }
                    if (entry is null) continue;
                    var id = (new ProcessKey(entry.Pid, entry.CreationTime), entry.Slot);
                    if (entry.Restored) _active.Remove(id); else _active[id] = entry;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Block($"the applied-changes journal could not be read ({e.Message})");
            return;
        }

        foreach (var (id, entry) in _active) Recover(id, entry, actuator, sink);
        foreach (var id in _gone) _active.Remove(id);
        _gone.Clear();

        try
        {
            if (damaged)
            {
                string kept = Path.Combine(dataDirectory, $"applied-journal.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
                File.Copy(_path, kept, overwrite: false);
                log.LogWarning("Applied journal had a damaged line; valid records recovered, damaged file kept as {Path}", kept);
            }
            Compact();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Block($"the applied-changes journal could not be written ({e.Message})");
        }
    }

    private void Recover((ProcessKey Key, Slot Slot) id, JournalEntry entry, IActuator actuator, IEngineEventSink sink)
    {
        if (!actuator.TryRead(id.Key, id.Slot, out long current, out _))
        {
            nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, id.Key.Pid);
            if (h == 0)
            { if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 87) _gone.Add(id); }
            else
            {
                try { if (Native.GetProcessTimes(h, out long created, out _, out _, out _) && created != id.Key.CreateTime) _gone.Add(id); }
                finally { Native.CloseHandle(h); }
            }
            return;
        }
        if (current != entry.Applied && current != entry.PreviousApplied)
        {
            // A memory cap whose job can no longer be found reads as "no cap" although the process may still be capped
            // (review L3): say so instead of silently dropping it.
            if (id.Slot == Slot.MemoryLimit && entry.Applied > 0 && current == 0)
                sink.Emit(new(EngineEventKind.Failed, entry.Pid, "", "Startup recovery", Slots.Name(entry.Slot),
                    Slots.FormatValue(entry.Slot, entry.Original), "memory cap could not be verified after a restart; restart the program to be sure it has none"));
            _gone.Add(id);
            return;
        }
        bool ok = actuator.TryWrite(id.Key, id.Slot, entry.Original, out var error);
        sink.Emit(new(ok ? EngineEventKind.Reverted : EngineEventKind.Failed, entry.Pid, "", "Startup recovery", Slots.Name(entry.Slot),
            Slots.FormatValue(entry.Slot, entry.Original), ok ? "restored original from applied-changes journal" : error));
        if (ok) _gone.Add(id);
    }

    public long OriginalOf(ProcessKey key, Slot slot, long current) =>
        _active.TryGetValue((key, slot), out var entry) && (current == entry.Applied || current == entry.PreviousApplied) ? entry.Original : current;

    public bool BeforeApply(ProcessKey key, Slot slot, long original, long applied, long? previousApplied, RuleOrigin origin, out string? error)
    {
        error = null;
        // Networking already has a machine-wide recovery journal and its own ownership protocol.
        if (NetworkPolicies.IsSlot(slot)) return true;
        var id = (key, slot);
        // Writing a value the process already had changes nothing a crash could leave behind: no record needed.
        if (original == applied && previousApplied is null && !_active.ContainsKey(id)) return true;
        if (_blocked) { error = Problem ?? "recovery journal unavailable; change declined"; return false; }
        if (!_active.ContainsKey(id) && _active.Count >= MaxActive) { error = "recovery journal full; change declined"; return false; }
        var entry = new JournalEntry(key.Pid, key.CreateTime, slot, original, applied, origin, PreviousApplied: previousApplied);
        if (_active.TryGetValue(id, out var old) && old == entry) return true;
        try { Append(entry); _active[id] = entry; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return false; }
    }

    public void Reverted(ProcessKey key, Slot slot)
    {
        if (!_active.TryGetValue((key, slot), out var entry)) return;
        _active.Remove((key, slot));
        if (_blocked) return;
        try { Append(entry with { Restored = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _log.LogWarning("Recovery completion not saved: {Message}", e.Message); }
    }

    public void Forget(ProcessKey key)
    {
        for (int s = 0; s < Slots.Count; s++) Reverted(key, (Slot)s);
    }

    private void Append(JournalEntry entry)
    {
        if (_records >= CompactAfter || (_stream is not null && _stream.Length >= MaxBytes - 1024)) Compact();
        _stream ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
        JsonSerializer.Serialize(_stream, entry, JournalJson.Default.JournalEntry);
        _stream.WriteByte((byte)'\n');
        _stream.Flush(); // to Windows, not to the disk (see the class comment)
        _records++;
    }

    /// <summary>Rewrites the log with only the outstanding entries (at start and when it has grown).</summary>
    private void Compact()
    {
        _stream?.Dispose();
        _stream = null;
        string tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var entry in _active.Values)
            { JsonSerializer.Serialize(stream, entry, JournalJson.Default.JournalEntry); stream.WriteByte((byte)'\n'); }
            stream.Flush(flushToDisk: true); // rare; the rename must not expose an empty file after a power loss
        }
        File.Move(tmp, _path, overwrite: true);
        _records = _active.Count;
    }

    private void Block(string reason)
    {
        _blocked = true;
        Problem = reason + "; Corewright does not change processes until it can record what it changes";
        _log.LogWarning("Applied journal: {Problem}", Problem);
    }

    public void Dispose() => _stream?.Dispose();
}
