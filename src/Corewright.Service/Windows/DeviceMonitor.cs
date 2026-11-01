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

/// <summary>Owned by Monitor.Sync. No timers, idle I/O or idle per-tick allocations.
/// Inventory is refreshed at startup, then only on API demand, at most every three minutes.</summary>
internal sealed unsafe class DeviceMonitor : IDisposable
{
    internal sealed class State(DeviceDto info)
    {
        public DeviceDto Info = info;
        public string Path = "";
        public ulong Luid;
        public uint Adapter;
        public bool FanAvailable;
        public nint Disk = -1;
        public long LastRead, LastWrite, LastIdle, LastQuery, HealthDue;
        public bool Baseline;
        private long[]? _time;
        private double?[]? _primary, _secondary;
        private int _next, _count;
        public void Clear() { Baseline = false; _count = _next = 0; Info.Values.SampledAt = null; }
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
            if (Adapter != 0) GpuDeviceProbe.Close(Adapter);
            Adapter = 0;
        }
    }
    private List<State> _disks = [], _gpus = [];
    private readonly Dictionary<ulong, State> _network = [];
    private long _inventoryDue, _inventoryAt, _lastActive;
    private bool _sampling;
    public DeviceMonitor() => Refresh();
    public void Refresh()
    {
        long now = Environment.TickCount64;
        if (now < _inventoryDue) return;
        _disks = Reconcile(_disks, DiskDeviceProbe.Inventory());
        _gpus = Reconcile(_gpus, GpuDeviceProbe.Inventory());
        _inventoryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _inventoryDue = now + 180_000;
        NetworkInventoryDue = true;
    }
    private static List<State> Reconcile(List<State> old, List<State>? found)
    {
        if (found is null) return old; // A failed enumeration never means every device was removed.
        for (int i = 0; i < found.Count; i++)
        {
            var previous = old.Find(s => s.Info.Id == found[i].Info.Id && s.Info.Name == found[i].Info.Name);
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
        if (active && !_sampling)
            foreach (var s in _disks.Concat(_gpus).Concat(_network.Values)) s.Clear(); // only on activation
        _sampling = active;
        if (active)
            foreach (var s in _network.Values)
            { s.Info.Values.Read = s.Info.Values.Write = null; s.Info.Values.SampledAt = null; s.Info.Connected = false; }
    }
    public void Sample(GpuMonitor? gpu, long now)
    {
        if (!_sampling)
        {
            if (_lastActive != 0 && now - _lastActive > 60_000)
            {
                foreach (var s in _disks) s.Close();
                foreach (var s in _gpus) s.Close();
                _lastActive = 0;
            }
            return;
        }
        _lastActive = now;
        long time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var s in _disks) { DiskDeviceProbe.Sample(s, now, time); s.Record(time); }
        foreach (var s in _gpus) { GpuDeviceProbe.Sample(s, gpu, time); s.Record(time); }
        foreach (var s in _network.Values) { if (s.Info.Values.SampledAt is null) s.Baseline = false; s.Record(time); }
    }
    public DevicesDto Snapshot(string? history, bool includeHidden)
    {
        var ids = (history ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(_inventoryAt, _sampling, [.. _disks.Concat(_gpus).Concat(_network.Values)
            .Where(s => includeHidden || !s.Info.Hidden).Select(s => s.Snapshot(ids.Contains("*") || ids.Contains(s.Info.Id)))]);
    }
    public void Dispose() { foreach (var s in _disks) s.Close(); foreach (var s in _gpus) s.Close(); }
}
