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
    /// <summary>Private memory in MB times seconds; divided by measured seconds gives the average.</summary>
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(UsageHour))]
internal sealed partial class UsageJson : JsonSerializerContext;

public sealed record UsageProgramDto(string Program, string Name, double CpuSeconds, double CpuPercent, double BusyPercent, double AverageCores,
    long IoBytes, long AverageMemory, long PeakMemory, double RunningSeconds);
public sealed record UsagePointDto(long Time, double Seconds, double CpuPercent, double? ProgramPercent);
public sealed record UsageStorageDto(long Bytes, long CapBytes, long? OldestTime, int Files);
public sealed record UsageDto(bool Enabled, long From, long To, double MeasuredSeconds, int LogicalProcessors, double AvailableCpuSeconds,
    double BusyCpuSeconds, double UnlistedCpuSeconds, string? Program, UsageProgramDto[] Programs, UsagePointDto[] Hours, UsageStorageDto Storage);

/// <summary>
/// Usage history (PM-45, owner 2026-11-05: "I then can see, okay, this antivirus is taking up half of my CPU for the
/// whole day"). Adds up, per program and hour, the exact CPU time, I/O and memory the sampler already reads each tick:
/// no extra system calls. Cost per tick: one pass over the process list with a cached accumulator per process (a string
/// lookup only when a process is new or the hour changes); no allocation except for a program's first appearance in an
/// hour. Completed hours are appended to one file per UTC day in <c>usage\</c>; the current hour is saved every five
/// minutes, so a crash loses at most that. The folder never grows past the configured cap: whole oldest days go first,
/// then the oldest hours of the remaining day.
/// Limits: a process that starts and exits between two samples is not seen; CPU time a process uses between its last
/// sample and its exit is lost; interrupt and DPC time belongs to no program (reported as unlisted).
/// </summary>
internal sealed class UsageHistory
{
    /// <summary>A program is listed on its own in an hour above any of these; the rest only counts in the totals.</summary>
    private const double MinCpuSeconds = 1;
    private const long MinIoBytes = 16L << 20;
    private const double MinAverageMb = 64;
    private const int MaxProgramsPerHour = 2000;

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
    private readonly Lock _io = new();
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

    /// <summary>The last queued file write. Writes run one after another, in the order they were queued (callers hold Monitor.Sync).</summary>
    public Task SaveCompletion { get; private set; } = Task.CompletedTask;

    private Task Enqueue(Action write)
    {
        SaveCompletion = SaveCompletion.ContinueWith(_ =>
        {
            lock (_io)
            {
                try { write(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }, TaskScheduler.Default);
        return SaveCompletion;
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
            if (p.UsageGeneration != _generation || p.UsageSlot is not Acc a || !ReferenceEquals(a.Key, key))
            {
                if (!_acc.TryGetValue(key, out a!))
                {
                    if (_acc.Count >= MaxProgramsPerHour) continue;
                    a = new Acc { Key = key, Name = _displayOf(key) ?? p.Name };
                    _acc.Add(key, a);
                }
                p.UsageSlot = a;
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

    /// <summary>The current hour so far (under Monitor.Sync); allocates, so only for API requests and saves.</summary>
    public UsageHour? Snapshot()
    {
        if (_hour == 0 || _wall <= 0) return null;
        var h = new UsageHour { T = _hour, W = Math.Round(_wall, 1), A = Math.Round(_available, 1), B = Math.Round(_busy, 1), Lp = _logical };
        foreach (var a in _acc.Values)
        {
            double cpu = a.CpuTicks / 1e7;
            if (a.Key.Length > 0 && (cpu >= MinCpuSeconds || a.Io >= MinIoBytes || a.MemoryMbSeconds / _wall >= MinAverageMb))
                h.P.Add(new UsageEntry { K = a.Key, N = a.Name, C = Math.Round(cpu, 2), Io = a.Io, M = Math.Round(a.MemoryMbSeconds), Pk = a.Peak, R = Math.Round(a.Running) });
            else { h.Oc += cpu; h.Oio += a.Io; }
        }
        h.Oc = Math.Round(h.Oc, 2);
        return h;
    }

    /// <summary>Appends the finished hour to its day file and starts a new one.</summary>
    private void Close()
    {
        var hour = Snapshot();
        _acc.Clear();
        _generation++;
        _wall = _available = _busy = 0;
        if (hour is null) return;
        Enqueue(() =>
        {
            Append(hour);
            File.Delete(CurrentPath);
            EnforceCap();
        });
    }

    /// <summary>Writes the current hour to current.json (every five minutes and at shutdown).</summary>
    public void SaveCurrent()
    {
        var hour = Snapshot();
        if (hour is null) return;
        Enqueue(() =>
        {
            Directory.CreateDirectory(_dir);
            string tmp = CurrentPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(hour, UsageJson.Default.UsageHour));
            File.Move(tmp, CurrentPath, overwrite: true);
        });
    }

    /// <summary>At start: the saved current hour continues if it is still this hour, otherwise it is filed as finished.</summary>
    private void Recover()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return;
            var hour = JsonSerializer.Deserialize(File.ReadAllText(CurrentPath), UsageJson.Default.UsageHour);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (hour is null || hour.W <= 0) return;
            if (hour.T == now - now % 3600)
            {
                _hour = hour.T;
                _wall = hour.W; _available = hour.A; _busy = hour.B; _logical = hour.Lp;
                foreach (var e in hour.P)
                    _acc[e.K] = new Acc { Key = e.K, Name = e.N, CpuTicks = (long)(e.C * 1e7), Io = e.Io, MemoryMbSeconds = e.M, Peak = e.Pk, Running = e.R };
                // Unlisted programs' totals are kept by an unnamed accumulator, so the hour's sums stay right.
                if (hour.Oc > 0 || hour.Oio > 0) _acc[""] = new Acc { Key = "", Name = "", CpuTicks = (long)(hour.Oc * 1e7), Io = hour.Oio };
            }
            else
            {
                Append(hour);
                File.Delete(CurrentPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private string DayPath(long unix) => Path.Combine(_dir, DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");

    private void Append(UsageHour hour)
    {
        Directory.CreateDirectory(_dir);
        using var fs = new FileStream(DayPath(hour.T), FileMode.Append, FileAccess.Write, FileShare.Read);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hour, UsageJson.Default.UsageHour) + "\n");
        fs.Write(line);
    }

    private FileInfo[] DayFiles()
    {
        var dir = new DirectoryInfo(_dir);
        if (!dir.Exists) return [];
        var files = dir.GetFiles("????-??-??.jsonl");
        Array.Sort(files, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return files;
    }

    /// <summary>Deletes the oldest days until the folder fits the cap; the last day loses its oldest hours instead.</summary>
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
            while (keep > 0 && size + Encoding.UTF8.GetByteCount(lines[keep - 1]) + 1 <= budget) { size += Encoding.UTF8.GetByteCount(lines[keep - 1]) + 1; keep--; }
            string tmp = last.FullName + ".tmp";
            File.WriteAllLines(tmp, lines[keep..]);
            File.Move(tmp, last.FullName, overwrite: true);
        }
    }

    public UsageStorageDto Storage()
    {
        lock (_io)
        {
            try
            {
                var files = DayFiles();
                long bytes = files.Sum(f => f.Length) + (File.Exists(CurrentPath) ? new FileInfo(CurrentPath).Length : 0);
                long? oldest = null;
                if (files.Length > 0)
                {
                    using var reader = new StreamReader(new FileStream(files[0].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    if (reader.ReadLine() is { } first && TryParse(first) is { } h) oldest = h.T * 1000;
                }
                return new UsageStorageDto(bytes, Interlocked.Read(ref _capBytes), oldest, files.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new UsageStorageDto(0, Interlocked.Read(ref _capBytes), null, 0); }
        }
    }

    private static UsageHour? TryParse(string line)
    {
        try { return line.Length == 0 ? null : JsonSerializer.Deserialize(line, UsageJson.Default.UsageHour); }
        catch (JsonException) { return null; }
    }

    /// <summary>Finished hours that start in [fromUnix, toUnix). Reads only the day files in range, outside Monitor.Sync.</summary>
    public List<UsageHour> Read(long fromUnix, long toUnix)
    {
        var result = new List<UsageHour>();
        lock (_io)
        {
            try
            {
                string first = DayPath(fromUnix)[^16..], last = DayPath(Math.Max(fromUnix, toUnix - 1))[^16..];
                foreach (var f in DayFiles())
                {
                    if (string.CompareOrdinal(f.Name, first) < 0 || string.CompareOrdinal(f.Name, last) > 0) continue;
                    using var reader = new StreamReader(new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    while (reader.ReadLine() is { } line)
                        if (TryParse(line) is { } h && h.T >= fromUnix && h.T < toUnix) result.Add(h);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Deletes all history (History → Clear). Called under Monitor.Sync; the returned task finishes the deletion.</summary>
    public Task Clear()
    {
        _acc.Clear();
        _generation++;
        _wall = _available = _busy = 0;
        return Enqueue(() =>
        {
            foreach (var f in DayFiles()) f.Delete();
            if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
        });
    }

    /// <summary>Adds up hours into the response: programs sorted by CPU time, one point per hour for the chart.</summary>
    public static UsageDto Aggregate(bool enabled, long fromMs, long toMs, List<UsageHour> hours, string? program, int limit, UsageStorageDto storage)
    {
        hours.Sort((a, b) => a.T.CompareTo(b.T));
        double wall = 0, available = 0, busy = 0, listed = 0;
        int logical = 0;
        var byKey = new Dictionary<string, (string Name, double Cpu, long Io, double Mem, long Peak, double Run)>(StringComparer.Ordinal);
        var points = new List<UsagePointDto>(hours.Count);
        foreach (var h in hours)
        {
            wall += h.W; available += h.A; busy += h.B;
            if (h.Lp > 0) logical = h.Lp;
            double? mine = program is null ? null : 0;
            foreach (var e in h.P)
            {
                if (e.K.Length == 0) continue;
                listed += e.C;
                byKey.TryGetValue(e.K, out var t);
                byKey[e.K] = (e.N.Length > 0 ? e.N : t.Name ?? e.K, t.Cpu + e.C, t.Io + e.Io, t.Mem + e.M, Math.Max(t.Peak, e.Pk), t.Run + e.R);
                if (program is not null && e.K == program && h.A > 0) mine = 100 * e.C / h.A;
            }
            points.Add(new UsagePointDto(h.T * 1000, h.W, h.A > 0 ? Math.Round(100 * h.B / h.A, 2) : 0, mine is { } m ? Math.Round(m, 3) : null));
        }
        var programs = byKey
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
        return new UsageDto(enabled, fromMs, toMs, Math.Round(wall), logical, Math.Round(available), Math.Round(busy),
            Math.Max(0, Math.Round(busy - listed)), program, programs, [.. points], storage);
    }
}
