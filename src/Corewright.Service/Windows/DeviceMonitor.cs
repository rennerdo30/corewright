namespace Corewright.Service.Windows;

public sealed record DeviceValues
{
    public double? Read { get; set; }
    public double? Write { get; set; }
    public double? Active { get; set; }
    public double? Load { get; set; }
    public long? DedicatedUsed { get; set; }
    public long? SharedUsed { get; set; }
    public long? SampledAt { get; set; }
}
public sealed record DeviceHealth
{
    public double? Temperature { get; set; }
    public double? FanRpm { get; set; }
    public double? PowerPercent { get; set; }
    public double? EngineClockMhz { get; set; }
    public int? WearPercent { get; set; }
    public int? SparePercent { get; set; }
    public int? CriticalWarning { get; set; }
    // NVMe counters are 128-bit; decimal strings preserve them through JSON/JavaScript.
    public string? MediaErrors { get; set; }
    public string? PowerOnHours { get; set; }
    public string? UnsafeShutdowns { get; set; }
    public string? DataUnitsRead { get; set; }
    public string? DataUnitsWritten { get; set; }
    public long? SampledAt { get; set; }
}
public sealed record DeviceHistory(long[] Time, double?[] Primary, double?[] Secondary);
public sealed record DeviceDto(string Id, string Kind, string Name, int? Index = null, string? Bus = null,
    long? Size = null, string[]? Volumes = null, long? DedicatedMemory = null, long? SharedMemory = null,
    string? Alias = null, string? Type = null, ulong? ReceiveLinkSpeed = null, ulong? SendLinkSpeed = null,
    bool? Connected = null, bool Hidden = false)
{
    public ulong? ReceiveLinkSpeed { get; set; } = ReceiveLinkSpeed;
    public ulong? SendLinkSpeed { get; set; } = SendLinkSpeed;
    public bool? Connected { get; set; } = Connected;
    public DeviceValues Values { get; init; } = new();
    public DeviceHealth Health { get; init; } = new();
    public DeviceHistory? History { get; init; }
}
public sealed record DevicesDto(long InventoryAt, bool Sampling, DeviceDto[] Devices);

/// <summary>Cached readings and network rows are owned by Monitor.Sync. Disk/GPU state and native
/// handles belong exclusively to DeviceProbeWorker; publication copies values without doing I/O.</summary>
internal sealed unsafe class DeviceMonitor : IDisposable
{
    internal sealed class State(DeviceDto info)
    {
        public DeviceDto Info = info;
        public State? Published;
        public bool IdentityInvalidated;
        public bool NeedsInventoryRefresh;
        public int ResetVersion;
        public string Path = "";
        public ulong Luid;
        public uint Adapter;
        public bool FanAvailable;
        public nint Disk = -1;
        public long LastRead, LastWrite, LastIdle, LastQuery, NextOpenAttemptMs, HealthDue;
        public bool Baseline;
        private long[]? _time;
        private double?[]? _primary, _secondary;
        private int _next, _count;
        public void Clear()
        {
            Baseline = false; _count = _next = 0; ResetVersion++;
            var v = Info.Values;
            v.Read = v.Write = v.Active = v.Load = null;
            v.DedicatedUsed = v.SharedUsed = null; v.SampledAt = null;
        }
        public void Invalidate(long now)
        {
            Close(); Clear(); IdentityInvalidated = NeedsInventoryRefresh = true;
            NextOpenAttemptMs = now + 60_000;
            var h = Info.Health;
            h.Temperature = h.FanRpm = h.PowerPercent = h.EngineClockMhz = null;
            h.WearPercent = h.SparePercent = h.CriticalWarning = null;
            h.MediaErrors = h.PowerOnHours = h.UnsafeShutdowns = h.DataUnitsRead = h.DataUnitsWritten = null;
            h.SampledAt = null;
        }
        public void Record(long now)
        {
            _time ??= new long[Monitor.HistoryCapacity];
            _primary ??= new double?[Monitor.HistoryCapacity];
            _secondary ??= new double?[Monitor.HistoryCapacity];
            _time[_next] = now;
            var v = Info.Values;
            _primary[_next] = Info.Kind == "gpu" ? v.Load : v.Read;
            _secondary[_next] = Info.Kind == "gpu" ? null : v.Write;
            _next = (_next + 1) % _time.Length;
            _count = Math.Min(_count + 1, _time.Length);
        }
        public DeviceDto Snapshot(bool history)
        {
            DeviceHistory? h = null;
            if (history)
            {
                var t = new long[_count]; var a = new double?[_count]; var b = new double?[_count];
                for (int i = 0; i < _count; i++)
                {
                    int j = (_next - _count + i + Monitor.HistoryCapacity) % Monitor.HistoryCapacity;
                    t[i] = _time![j]; a[i] = _primary![j]; b[i] = _secondary![j];
                }
                h = new(t, a, b);
            }
            return Info with { Values = Info.Values with { }, Health = Info.Health with { }, History = h };
        }
        public void Close()
        {
            if (Disk != -1) Native.CloseHandle(Disk);
            Disk = -1;
            Baseline = false;
            LastRead = LastWrite = LastIdle = LastQuery = 0;
            NextOpenAttemptMs = HealthDue = 0;
            if (Adapter != 0) GpuDeviceProbe.Close(Adapter);
            Adapter = 0;
        }
    }
    // Only the worker accesses these lists and their handles.
    private List<State> _disks = [], _gpus = [];
    // Only Monitor.Sync accesses the published lists and their histories.
    private List<State> _diskCache = [], _gpuCache = [];
    private readonly Dictionary<ulong, State> _network = [];
    private long _inventoryRetryMs, _inventoryAt, _lastActive;
    private int _inventoryGeneration = 1, _refreshedGeneration, _networkDirty = 1;
    private bool _sampling;
    private int _activation, _probeActivation;
    private DeviceProbeWorker? _worker;
    private DeviceNotifications? _notifications;
    public GpuReadings Gpu { get; } = new();
    public void Start(Lock sync, ILogger log)
    {
        _notifications = new(this, log); // Register before enumerating so no arrival is lost.
        _worker = new(this, sync, log);
    }
    internal void MarkInventoryDirty()
    {
        Interlocked.Increment(ref _inventoryGeneration);
        Interlocked.Exchange(ref _networkDirty, 1);
    }
    public void Refresh() => _worker?.Request(true);
    internal void RefreshProbes(Func<bool> clientActive)
    {
        long now = Environment.TickCount64;
        int generation = Volatile.Read(ref _inventoryGeneration);
        if (generation == _refreshedGeneration || now < _inventoryRetryMs) return;
        var disks = DiskDeviceProbe.Inventory(clientActive);
        if (!clientActive()) return;
        var gpus = GpuDeviceProbe.Inventory(clientActive);
        if (!clientActive()) return;
        _disks = Reconcile(_disks, disks);
        _gpus = Reconcile(_gpus, gpus);
        _inventoryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (disks is not null && gpus is not null) { _refreshedGeneration = generation; _inventoryRetryMs = 0; }
        else _inventoryRetryMs = now + 60_000; // Only failed enumerations retry, while demanded.
    }
    private static List<State> Reconcile(List<State> old, List<State>? found)
    {
        if (found is null) return old; // A failed enumeration never means every device was removed.
        for (int i = 0; i < found.Count; i++)
        {
            // A physical index and model may be reused immediately by a different device.
            var previous = old.Find(s => s.Info.Id == found[i].Info.Id
                && s.Info.Name == found[i].Info.Name && string.Equals(s.Path, found[i].Path, StringComparison.OrdinalIgnoreCase));
            if (previous is null) continue;
            previous.Info = found[i].Info with { Values = previous.Info.Values, Health = previous.Info.Health };
            found[i] = previous;
        }
        foreach (var s in old) if (!found.Contains(s)) s.Close();
        return found;
    }
    public bool NetworkInventoryDue { get; private set; } = true;
    private readonly HashSet<ulong> _seenNetwork = [];
    public void ObserveNetwork(byte* r, bool active, double elapsed)
    {
        ulong luid = *(ulong*)r;
        if (NetworkInventoryDue)
        {
            _seenNetwork.Add(luid);
            uint type = *(uint*)(r + 1128);
            bool hidden = type == 24 || (r[1152] & (2 | 128)) != 0; // FilterInterface / EndPointInterface
            string kind = (r[1152] & 1) == 0 ? "Virtual" : type == 71 ? "Wi-Fi" : type == 6 ? "Ethernet" : "Other";
            var info = new DeviceDto($"net:{luid:X16}", "network", new string((char*)(r + 542)), Alias: new string((char*)(r + 28)), Type: kind,
                Connected: *(int*)(r + 1156) == 1, ReceiveLinkSpeed: *(ulong*)(r + 1200), SendLinkSpeed: *(ulong*)(r + 1192), Hidden: hidden);
            if (_network.TryGetValue(luid, out var prev)) prev.Info = info with { Values = prev.Info.Values };
            else _network[luid] = new(info);
        }
        if (!_network.TryGetValue(luid, out var s) || !active) return;
        // Link state/speeds can change without a device arrival; keep these from the existing GetIfTable2 call.
        bool connected = *(int*)(r + 1156) == 1;
        ulong rxSpeed = *(ulong*)(r + 1200), txSpeed = *(ulong*)(r + 1192);
        s.Info.Connected = connected; s.Info.ReceiveLinkSpeed = rxSpeed; s.Info.SendLinkSpeed = txSpeed;
        long rx = *(long*)(r + 1208), tx = *(long*)(r + 1280);
        s.Info.Values.Read = s.Baseline && elapsed > 0 && rx >= s.LastRead ? (rx - s.LastRead) / elapsed : null;
        s.Info.Values.Write = s.Baseline && elapsed > 0 && tx >= s.LastWrite ? (tx - s.LastWrite) / elapsed : null;
        s.LastRead = rx; s.LastWrite = tx; s.Baseline = true;
        s.Info.Values.SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    public void EndNetworkInventory()
    {
        if (!NetworkInventoryDue) return;
        foreach (var key in _network.Keys.ToArray()) if (!_seenNetwork.Contains(key)) _network.Remove(key);
        _seenNetwork.Clear(); NetworkInventoryDue = false;
    }
    public void BeginSample(bool active)
    {
        if (active && Interlocked.Exchange(ref _networkDirty, 0) != 0) NetworkInventoryDue = true;
        if (active && !_sampling)
        {
            Interlocked.Increment(ref _activation);
            foreach (var s in _diskCache.Concat(_gpuCache).Concat(_network.Values)) s.Clear(); // only on activation
        }
        _sampling = active;
        if (active)
            foreach (var s in _network.Values)
            { s.Info.Values.Read = s.Info.Values.Write = null; s.Info.Values.SampledAt = null; s.Info.Connected = false; }
    }
    public void Sample(long now)
    {
        _worker?.Request(_sampling);
        if (!_sampling) return;
        long time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var s in _network.Values) { if (s.Info.Values.SampledAt is null) s.Baseline = false; s.Record(time); }
    }
    internal void SampleProbes(GpuMonitor? gpu, bool active, long now, Func<bool> clientActive)
    {
        if (!active)
        {
            if (_lastActive != 0 && now - _lastActive > 60_000)
            {
                foreach (var s in _disks) s.Close();
                foreach (var s in _gpus) s.Close();
                _lastActive = 0;
            }
            return;
        }
        int activation = Volatile.Read(ref _activation);
        if (_probeActivation != activation)
        {
            foreach (var s in _disks) s.Clear();
            foreach (var s in _gpus) s.Clear();
        }
        _probeActivation = activation;
        _lastActive = now;
        long time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var s in _disks)
        {
            if (!clientActive()) return;
            DiskDeviceProbe.Sample(s, now, time, clientActive);
            if (s.NeedsInventoryRefresh) { s.NeedsInventoryRefresh = false; MarkInventoryDirty(); }
        }
        foreach (var s in _gpus) { if (!clientActive()) return; GpuDeviceProbe.Sample(s, gpu, time); }
    }
    private long _publishedInventoryAt;
    internal void Publish(GpuMonitor? gpu, bool active)
    {
        if (_publishedInventoryAt != _inventoryAt)
        {
            _diskCache = Cache(_disks);
            _gpuCache = Cache(_gpus);
            _publishedInventoryAt = _inventoryAt;
            NetworkInventoryDue = true;
        }
        Copy(_disks, active); Copy(_gpus, active);
        Gpu.CopyFrom(gpu);
    }
    private static List<State> Cache(List<State> source)
    {
        var result = new List<State>(source.Count);
        foreach (var s in source)
        {
            s.Published ??= new(s.Info with { Values = new(), Health = new() });
            s.Published.Info = s.Info with { Values = s.Published.Info.Values, Health = s.Published.Info.Health };
            result.Add(s.Published);
        }
        return result;
    }
    private static void Copy(List<State> source, bool active)
    {
        foreach (var s in source)
        {
            if (s.Published is not { } p) continue;
            if (p.ResetVersion != s.ResetVersion) { p.Clear(); p.ResetVersion = s.ResetVersion; }
            var a = s.Info.Values; var b = p.Info.Values;
            b.Read = a.Read; b.Write = a.Write; b.Active = a.Active; b.Load = a.Load;
            b.DedicatedUsed = a.DedicatedUsed; b.SharedUsed = a.SharedUsed; b.SampledAt = a.SampledAt;
            var h = s.Info.Health; var j = p.Info.Health;
            j.Temperature = h.Temperature; j.FanRpm = h.FanRpm; j.PowerPercent = h.PowerPercent; j.EngineClockMhz = h.EngineClockMhz;
            j.WearPercent = h.WearPercent; j.SparePercent = h.SparePercent; j.CriticalWarning = h.CriticalWarning;
            j.MediaErrors = h.MediaErrors; j.PowerOnHours = h.PowerOnHours; j.UnsafeShutdowns = h.UnsafeShutdowns;
            j.DataUnitsRead = h.DataUnitsRead; j.DataUnitsWritten = h.DataUnitsWritten; j.SampledAt = h.SampledAt;
            if (active && a.SampledAt is { } time) p.Record(time);
        }
    }
    public DevicesDto Snapshot(string? history, bool includeHidden)
    {
        var ids = (history ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(_publishedInventoryAt, _sampling, [.. _diskCache.Concat(_gpuCache).Concat(_network.Values)
            .Where(s => includeHidden || !s.Info.Hidden).Select(s => s.Snapshot(ids.Contains("*") || ids.Contains(s.Info.Id)))]);
    }
    internal void CloseProbes() { foreach (var s in _disks) s.Close(); foreach (var s in _gpus) s.Close(); }
    public void Dispose() { _notifications?.Dispose(); _worker?.Dispose(); }
}
