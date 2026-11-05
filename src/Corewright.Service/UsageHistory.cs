using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Engine;

namespace Corewright.Service;

/// <summary>One program in one hour. Short names keep the files small (one line per hour, a year fits in the cap).</summary>
public sealed class UsageEntry
{
    /// <summary>Program identity (exe path, or what a host runs), see <see cref="ProgramIdentity"/>.</summary>
    public string K { get; set; } = "";
    /// <summary>Display name.</summary>
    public string N { get; set; } = "";
    /// <summary>CPU seconds (all its processes, all cores).</summary>
    public double C { get; set; }
    /// <summary>Disk and other I/O bytes.</summary>
    public long Io { get; set; }
    /// <summary>Private memory in MB times seconds; divided by running seconds gives the average while running.</summary>
    public double M { get; set; }
    /// <summary>Peak private memory of all its processes together, bytes.</summary>
    public long Pk { get; set; }
    /// <summary>Seconds it was running.</summary>
    public double R { get; set; }
}

/// <summary>One hour of usage history.</summary>
public sealed class UsageHour
{
    /// <summary>Hour start, Unix seconds (UTC).</summary>
    public long T { get; set; }
    /// <summary>Seconds measured in this hour (service running, PC awake).</summary>
    public double W { get; set; }
    /// <summary>CPU seconds all logical processors had (W times the processor count).</summary>
    public double A { get; set; }
    /// <summary>CPU seconds the processors were busy.</summary>
    public double B { get; set; }
    public int Lp { get; set; }
    /// <summary>CPU seconds and I/O of programs too small to list one by one.</summary>
    public double Oc { get; set; }
    public long Oio { get; set; }
    public List<UsageEntry> P { get; set; } = [];
}

/// <summary>
/// current.json: the unfinished hour with every program (unfiltered, so a restart within the hour loses no attribution)
/// and finished hours whose append has not succeeded yet (so a full or unavailable disk loses nothing until it works).
/// </summary>
public sealed class UsageCheckpoint
{
    public UsageHour? Current { get; set; }
    public List<UsageHour> Pending { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(UsageHour))]
[JsonSerializable(typeof(UsageCheckpoint))]
internal sealed partial class UsageJson : JsonSerializerContext;

public sealed record UsageProgramDto(string Program, string Name, double CpuSeconds, double CpuPercent, double BusyPercent, double AverageCores,
    long IoBytes, long AverageMemory, long PeakMemory, double RunningSeconds);
public sealed record UsagePointDto(long Time, double Seconds, double CpuPercent, double? ProgramPercent);
public sealed record UsageStorageDto(long Bytes, long CapBytes, long? OldestTime, int Files, string? Problem);
public sealed record UsageDto(bool Enabled, long From, long To, double MeasuredSeconds, int LogicalProcessors, double AvailableCpuSeconds,
    double BusyCpuSeconds, double UnlistedCpuSeconds, string? Program, UsageProgramDto[] Programs, UsagePointDto[] Hours, UsageStorageDto Storage);

/// <summary>
/// Usage history (PM-45, owner 2026-11-05: "I then can see, okay, this antivirus is taking up half of my CPU for the
/// whole day"). Adds up, per program and hour, the exact CPU time, I/O and memory the sampler already reads each tick:
/// no extra system calls. Cost per tick: one pass over the process list with an accumulator cached per process (a
/// string lookup only when a process is new, changes identity or the hour changes); no allocation except for a
/// program's first appearance in an hour.
/// <para>Storage: finished hours are appended to one file per UTC day in <c>usage\</c>. The unfinished hour (all
/// programs) and finished hours not yet written are kept in <c>usage\current.json</c>, saved every five minutes, at
/// every hour change and at shutdown, so a crash loses at most five minutes. Appends are idempotent by hour (a record
/// already in the day file is not written again) and start on a fresh line after a torn tail. The folder never grows
/// past the configured cap: whole oldest days go first, then the oldest hours of the remaining day.</para>
/// <para>Limits: a process that starts and exits between two samples is not seen; CPU time a process uses between its
/// last sample and its exit is lost; interrupt and DPC time belongs to no program (reported as unlisted). A sample
/// interval that crosses an hour boundary (at most 2 s) counts in the later hour.</para>
/// </summary>
internal sealed class UsageHistory
{
    /// <summary>A program is listed on its own in a finished hour above any of these; the rest only counts in the totals.</summary>
    private const double MinCpuSeconds = 1;
    private const long MinIoBytes = 16L << 20;
    private const double MinAverageMb = 64;
    private const int MaxProgramsPerHour = 2000;
    /// <summary>Finished hours kept in memory while the disk refuses them (three days).</summary>
    private const int MaxPending = 72;

    private sealed class Acc
    {
        public string Key = "", Name = "";
        public long CpuTicks, Io, TickMemory, Peak;
        public double MemoryMbSeconds, Running;
        public int Stamp;
    }

    private readonly string _dir;
    private readonly Func<string, string?> _displayOf;
    private readonly Dictionary<string, Acc> _acc = new(StringComparer.Ordinal);
    private readonly List<Acc> _touched = [];
    /// <summary>Serializes file writes, cap enforcement and deletion. Reads do not take it (shared, delete-tolerant handles).</summary>
    private readonly Lock _io = new();
    /// <summary>Finished hours not yet appended; guarded by locking the list itself (tick thread adds, writer removes).</summary>
    private readonly List<UsageHour> _pending = [];
    private long _hour;
    private double _wall, _available, _busy;
    private int _logical, _generation = 1, _stamp;
    private long _lastSaveMs;
    private long _capBytes = 64L << 20;

    public UsageHistory(string dataDirectory, Func<string, string?> displayOf)
    {
        _dir = Path.Combine(dataDirectory, "usage");
        _displayOf = displayOf;
        Recover();
    }

    /// <summary>The last write problem (disk full, access denied), or null; shown on the History page.</summary>
    public string? Problem { get; private set; }

    /// <summary>The last queued file write. Writes run one after another, in the order they were queued (callers hold Monitor.Sync).</summary>
    public Task SaveCompletion { get; private set; } = Task.CompletedTask;

    private Task<bool> Enqueue(Action write)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveCompletion = SaveCompletion.ContinueWith(_ =>
        {
            lock (_io)
            {
                try { write(); Problem = null; done.SetResult(true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    Problem = e.Message;
                    done.SetResult(false);
                }
            }
        }, TaskScheduler.Default);
        return done.Task;
    }

    private string CurrentPath => Path.Combine(_dir, "current.json");

    public void SetCap(int megabytes)
    {
        long cap = Math.Max(1, megabytes) * (1L << 20);
        if (cap == Interlocked.Exchange(ref _capBytes, cap)) return;
        Enqueue(EnforceCap);
    }

    /// <summary>One tick. Called under Monitor.Sync after identities are known.</summary>
    public void Observe(IReadOnlyList<ProcessInfo> processes, double dt, long availableTicks, long busyTicks, int logical, long nowMs)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long hour = now - now % 3600;
        if (hour != _hour)
        {
            if (_hour != 0 && _wall > 0) Close();
            _hour = hour;
        }
        // A long gap is sleep or a stalled service, not measured time; its deltas would exceed the time counted.
        if (dt <= 0 || dt > 30 || availableTicks <= 0) return;
        _wall += dt;
        _available += availableTicks / 1e7;
        _busy += busyTicks / 1e7;
        _logical = logical;
        _stamp++;
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (p.Key.Pid == 0) continue; // Idle: free CPU time, not a program
            string key = p.Program ?? p.Path ?? p.Name;
            // The slot stays valid while this process keeps the same identity string and the hour has not changed.
            if (p.UsageGeneration != _generation || p.UsageSlot is not Acc a || !ReferenceEquals(p.UsageKey, key))
            {
                if (!_acc.TryGetValue(key, out a!))
                {
                    if (_acc.Count >= MaxProgramsPerHour) continue;
                    a = new Acc { Key = key, Name = _displayOf(key) ?? p.Name };
                    _acc.Add(key, a);
                }
                p.UsageSlot = a;
                p.UsageKey = key;
                p.UsageGeneration = _generation;
            }
            a.CpuTicks += p.CpuDelta;
            a.Io += p.IoDelta;
            if (a.Stamp != _stamp) { a.Stamp = _stamp; a.TickMemory = 0; _touched.Add(a); }
            a.TickMemory += p.PrivateBytes;
        }
        for (int i = 0; i < _touched.Count; i++)
        {
            var a = _touched[i];
            a.Running += dt;
            a.MemoryMbSeconds += a.TickMemory / 1048576.0 * dt;
            if (a.TickMemory > a.Peak) a.Peak = a.TickMemory;
        }
        _touched.Clear();
        if (nowMs - _lastSaveMs > 5 * 60_000)
        {
            _lastSaveMs = nowMs;
            SaveCurrent();
        }
    }

    /// <summary>The current hour so far (under Monitor.Sync). Allocates, so only for API requests and saves. Filtered as a
    /// finished hour would be, or with every program for the checkpoint.</summary>
    public UsageHour? Snapshot(bool all = false)
    {
        if (_hour == 0 || _wall <= 0) return null;
        var h = new UsageHour { T = _hour, W = Math.Round(_wall, 1), A = Math.Round(_available, 1), B = Math.Round(_busy, 1), Lp = _logical };
        foreach (var a in _acc.Values)
        {
            double cpu = a.CpuTicks / 1e7;
            if (a.Key.Length > 0 && (all || cpu >= MinCpuSeconds || a.Io >= MinIoBytes || a.MemoryMbSeconds / _wall >= MinAverageMb))
                h.P.Add(new UsageEntry { K = a.Key, N = a.Name, C = Math.Round(cpu, 3), Io = a.Io, M = Math.Round(a.MemoryMbSeconds), Pk = a.Peak, R = Math.Round(a.Running, 1) });
            else { h.Oc += cpu; h.Oio += a.Io; }
        }
        h.Oc = Math.Round(h.Oc, 3);
        return h;
    }

    /// <summary>Queues the finished hour for appending and starts a new one.</summary>
    private void Close()
    {
        var hour = Snapshot();
        _acc.Clear();
        _generation++;
        _wall = _available = _busy = 0;
        if (hour is not null)
            lock (_pending)
            {
                _pending.Add(hour);
                if (_pending.Count > MaxPending) _pending.RemoveAt(0);
            }
        SaveCurrent();
    }

    /// <summary>
    /// Writes current.json (unfinished hour and pending hours), then appends pending hours to their day files and enforces
    /// the cap. Every five minutes, at each hour change and at shutdown.
    /// </summary>
    public void SaveCurrent()
    {
        var current = Snapshot(all: true);
        Enqueue(() =>
        {
            Directory.CreateDirectory(_dir);
            List<UsageHour> pending;
            lock (_pending) pending = [.. _pending];
            WriteCheckpoint(new UsageCheckpoint { Current = current, Pending = pending });
            if (pending.Count == 0) { EnforceCap(); return; }
            foreach (var h in pending)
            {
                Append(h); // throws on a full or unavailable disk: the hour stays pending and in the checkpoint
                lock (_pending) _pending.Remove(h);
            }
            lock (_pending) pending = [.. _pending];
            WriteCheckpoint(new UsageCheckpoint { Current = current, Pending = pending });
            EnforceCap();
        });
    }

    private void WriteCheckpoint(UsageCheckpoint checkpoint)
    {
        string tmp = CurrentPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(checkpoint, UsageJson.Default.UsageCheckpoint));
        File.Move(tmp, CurrentPath, overwrite: true);
    }

    /// <summary>At start: the saved hour continues if it is still this hour, otherwise it is finished; pending hours are
    /// appended (idempotently) on the first save.</summary>
    private void Recover()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return;
            string text = File.ReadAllText(CurrentPath);
            UsageCheckpoint? checkpoint;
            try { checkpoint = JsonSerializer.Deserialize(text, UsageJson.Default.UsageCheckpoint); }
            catch (JsonException) { checkpoint = null; }
            // Version 1 (beta.12 development builds) stored the bare hour.
            if (checkpoint is null || (checkpoint.Current is null && checkpoint.Pending.Count == 0))
                checkpoint = JsonSerializer.Deserialize(text, UsageJson.Default.UsageHour) is { T: > 0 } bare ? new UsageCheckpoint { Current = bare } : null;
            if (checkpoint is null) return;
            _pending.AddRange(checkpoint.Pending);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (checkpoint.Current is { W: > 0 } hour)
            {
                if (hour.T == now - now % 3600)
                {
                    _hour = hour.T;
                    _wall = hour.W; _available = hour.A; _busy = hour.B; _logical = hour.Lp;
                    foreach (var e in hour.P)
                        _acc[e.K] = new Acc { Key = e.K, Name = e.N, CpuTicks = (long)(e.C * 1e7), Io = e.Io, MemoryMbSeconds = e.M, Peak = e.Pk, Running = e.R };
                    // Totals of programs not listed in the checkpoint stay in an unnamed accumulator, so the hour's sums are right.
                    if (hour.Oc > 0 || hour.Oio > 0) _acc[""] = new Acc { CpuTicks = (long)(hour.Oc * 1e7), Io = hour.Oio };
                }
                else _pending.Add(Finish(hour));
            }
            if (_pending.Count > 0) SaveCurrent();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Problem = e.Message; }
    }

    /// <summary>Applies the listing thresholds to an unfiltered checkpoint hour.</summary>
    private static UsageHour Finish(UsageHour h)
    {
        var listed = new List<UsageEntry>();
        foreach (var e in h.P)
            if (e.C >= MinCpuSeconds || e.Io >= MinIoBytes || (h.W > 0 && e.M / h.W >= MinAverageMb)) listed.Add(e);
            else { h.Oc += e.C; h.Oio += e.Io; }
        h.P = listed;
        h.Oc = Math.Round(h.Oc, 3);
        return h;
    }

    private string DayPath(long unix) => Path.Combine(_dir, DayName(unix));
    private static string DayName(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl";

    /// <summary>Appends one hour unless its day file already has it; starts on a fresh line after a torn tail.</summary>
    private void Append(UsageHour hour)
    {
        string path = DayPath(hour.T);
        if (File.Exists(path))
            foreach (var line in ReadLines(path))
                if (TryParse(line) is { } existing && existing.T == hour.T) return;
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        bool tornTail = false;
        if (fs.Length > 0)
        {
            fs.Seek(-1, SeekOrigin.End);
            tornTail = fs.ReadByte() != '\n';
        }
        fs.Seek(0, SeekOrigin.End);
        var text = (tornTail ? "\n" : "") + JsonSerializer.Serialize(hour, UsageJson.Default.UsageHour) + "\n";
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line) yield return line;
    }

    private FileInfo[] DayFiles()
    {
        var dir = new DirectoryInfo(_dir);
        if (!dir.Exists) return [];
        var files = dir.GetFiles("????-??-??.jsonl");
        Array.Sort(files, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return files;
    }

    /// <summary>Deletes the oldest days until the folder (day files and current.json) fits the cap; the last day loses
    /// its oldest hours instead. A checkpoint alone larger than the cap (a 1 MB cap and thousands of programs) is kept.</summary>
    private void EnforceCap()
    {
        long cap = Interlocked.Read(ref _capBytes);
        var files = DayFiles();
        long current = File.Exists(CurrentPath) ? new FileInfo(CurrentPath).Length : 0;
        long total = current + files.Sum(f => f.Length);
        int i = 0;
        for (; total > cap && i < files.Length - 1; i++)
        {
            total -= files[i].Length;
            files[i].Delete();
        }
        if (total > cap && i < files.Length)
        {
            var last = files[i];
            var lines = File.ReadAllLines(last.FullName);
            long budget = cap - current, size = 0;
            int keep = lines.Length;
            while (keep > 0)
            {
                long n = Encoding.UTF8.GetByteCount(lines[keep - 1]) + 1; // written with "\n" below
                if (size + n > budget) break;
                size += n;
                keep--;
            }
            string tmp = last.FullName + ".tmp";
            var sb = new StringBuilder((int)Math.Min(size, int.MaxValue));
            for (int j = keep; j < lines.Length; j++) sb.Append(lines[j]).Append('\n');
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, last.FullName, overwrite: true);
        }
    }

    public UsageStorageDto Storage()
    {
        try
        {
            var files = DayFiles();
            long bytes = files.Sum(f => f.Length) + (File.Exists(CurrentPath) ? new FileInfo(CurrentPath).Length : 0);
            long? oldest = null;
            if (files.Length > 0)
                foreach (var line in ReadLines(files[0].FullName))
                    if (TryParse(line) is { } h) { oldest = h.T * 1000; break; }
            return new UsageStorageDto(bytes, Interlocked.Read(ref _capBytes), oldest, files.Length, Problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new UsageStorageDto(0, Interlocked.Read(ref _capBytes), null, 0, Problem ?? e.Message); }
    }

    private static UsageHour? TryParse(string line)
    {
        try { return line.Length == 0 ? null : JsonSerializer.Deserialize(line, UsageJson.Default.UsageHour); }
        catch (JsonException) { return null; }
    }

    /// <summary>Deletes all history (History → Clear). Called under Monitor.Sync; the task is true once the files are gone.</summary>
    public Task<bool> Clear()
    {
        _acc.Clear();
        _generation++;
        _wall = _available = _busy = 0;
        lock (_pending) _pending.Clear();
        return Enqueue(() =>
        {
            foreach (var f in DayFiles()) f.Delete();
            if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
        });
    }

    /// <summary>
    /// Streams the day files in range and adds them up without keeping the hours: memory is bounded by the number of
    /// distinct programs and one chart point per hour, not by the size of the store. Runs on the request thread, outside
    /// Monitor.Sync and the write lock. <paramref name="current"/> and <paramref name="pending"/> are the hours not on
    /// disk yet.
    /// </summary>
    public UsageDto Query(bool enabled, long fromMs, long toMs, string? program, int limit, UsageHour? current)
    {
        long fromUnix = fromMs / 1000 - fromMs / 1000 % 3600, toUnix = (toMs + 999) / 1000;
        var agg = new Aggregator(program);
        var seen = new HashSet<long>();
        string first = DayName(fromUnix), last = DayName(Math.Max(fromUnix, toUnix - 1));
        foreach (var f in DayFiles())
        {
            if (string.CompareOrdinal(f.Name, first) < 0 || string.CompareOrdinal(f.Name, last) > 0) continue;
            try
            {
                foreach (var line in ReadLines(f.FullName))
                    if (TryParse(line) is { } h && h.T >= fromUnix && h.T < toUnix && seen.Add(h.T)) agg.Add(h);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // deleted by the cap meanwhile
        }
        List<UsageHour> pending;
        lock (_pending) pending = [.. _pending];
        foreach (var h in pending)
            if (h.T >= fromUnix && h.T < toUnix && seen.Add(h.T)) agg.Add(h);
        if (current is not null && current.T >= fromUnix && current.T < toUnix && seen.Add(current.T)) agg.Add(current);
        return agg.Result(enabled, fromMs, toMs, limit, Storage());
    }

    private sealed class Aggregator(string? program)
    {
        private double _wall, _available, _busy, _listed;
        private int _logical;
        private readonly Dictionary<string, (string Name, double Cpu, long Io, double Mem, long Peak, double Run)> _byKey = new(StringComparer.Ordinal);
        private readonly List<UsagePointDto> _points = [];

        public void Add(UsageHour h)
        {
            _wall += h.W; _available += h.A; _busy += h.B;
            if (h.Lp > 0) _logical = h.Lp;
            double? mine = program is null ? null : 0;
            foreach (var e in h.P)
            {
                if (e.K.Length == 0) continue;
                _listed += e.C;
                _byKey.TryGetValue(e.K, out var t);
                _byKey[e.K] = (e.N.Length > 0 ? e.N : t.Name ?? e.K, t.Cpu + e.C, t.Io + e.Io, t.Mem + e.M, Math.Max(t.Peak, e.Pk), t.Run + e.R);
                if (program is not null && e.K == program && h.A > 0) mine = 100 * e.C / h.A;
            }
            _points.Add(new UsagePointDto(h.T * 1000, h.W, h.A > 0 ? Math.Round(100 * h.B / h.A, 2) : 0, mine is { } m ? Math.Round(m, 3) : null));
        }

        public UsageDto Result(bool enabled, long fromMs, long toMs, int limit, UsageStorageDto storage)
        {
            _points.Sort((a, b) => a.Time.CompareTo(b.Time));
            double wall = _wall, available = _available, busy = _busy;
            var programs = _byKey
                .OrderByDescending(kv => kv.Value.Cpu)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(kv => new UsageProgramDto(kv.Key, kv.Value.Name, Math.Round(kv.Value.Cpu, 1),
                    available > 0 ? Math.Round(100 * kv.Value.Cpu / available, 3) : 0,
                    busy > 0 ? Math.Round(100 * kv.Value.Cpu / busy, 2) : 0,
                    wall > 0 ? Math.Round(kv.Value.Cpu / wall, 3) : 0,
                    kv.Value.Io,
                    kv.Value.Run > 0 ? (long)(kv.Value.Mem / kv.Value.Run * 1048576) : 0,
                    kv.Value.Peak, Math.Round(kv.Value.Run)))
                .ToArray();
            return new UsageDto(enabled, fromMs, toMs, Math.Round(wall), _logical, Math.Round(available), Math.Round(busy),
                Math.Max(0, Math.Round(busy - _listed)), program, programs, [.. _points], storage);
        }
    }
}
