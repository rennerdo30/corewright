using System.Globalization;
using System.Runtime.InteropServices;
namespace Corewright.Service.Windows;

public sealed class GpuAdapterDto(string id)
{
    public string Id { get; } = id;
    public double Utilization { get; internal set; }
    public long DedicatedUsed { get; internal set; }
    public long? SharedUsed { get; internal set; }
    public bool LoadAvailable { get; internal set; }
    public bool MemoryAvailable { get; internal set; }
}

/// <summary>Task Manager's busiest engine per adapter, summed across processes. Buffers and numeric keys
/// are reused. Full LUIDs prevent collisions. Owned by Monitor's existing client-active lifecycle.</summary>
internal sealed unsafe partial class GpuMonitor : IDisposable
{
    private nint _query, _engine, _memory, _shared;
    private byte[] _buffer = new byte[256 * 1024];
    private readonly Dictionary<(ulong Luid, uint Physical, int Engine), double> _engines = [];
    private readonly Dictionary<(ulong Luid, uint Physical), GpuAdapterDto> _adapters = [];
    private readonly Dictionary<int, double> _byPid = [];
    public GpuAdapterDto[] Adapters { get; private set; } = [];
    public double Utilization { get; private set; }
    public long DedicatedUsed { get; private set; }
    public IReadOnlyDictionary<int, double> ByProcess => _byPid;
    public bool Available { get; private set; }
    public GpuMonitor()
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engine) != 0) return;
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _memory);
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Shared Usage", 0, out _shared);
        Available = PdhCollectQueryData(_query) == 0;
    }
    internal static string Id(ulong luid, uint physical = 0) => $"gpu:{luid:X16}:{physical}";
    public void Sample()
    {
        foreach (var a in Adapters) { a.LoadAvailable = a.MemoryAvailable = false; a.SharedUsed = null; a.Utilization = 0; a.DedicatedUsed = 0; }
        Utilization = 0; DedicatedUsed = 0; _engines.Clear(); _byPid.Clear();
        if (!Available || PdhCollectQueryData(_query) != 0) return;
        Read(_engine, 0x200, 0); Read(_memory, 0x400, 1); Read(_shared, 0x400, 2);
        foreach (var (key, value) in _engines)
        {
            var a = Adapter(key.Luid, key.Physical);
            a.Utilization = Math.Max(a.Utilization, Math.Clamp(value, 0, 100)); a.LoadAvailable = true;
        }
        foreach (var a in Adapters) { Utilization = Math.Max(Utilization, a.Utilization); DedicatedUsed += a.DedicatedUsed; }
    }
    private GpuAdapterDto Adapter(ulong luid, uint physical)
    {
        if (_adapters.TryGetValue((luid, physical), out var a)) return a;
        a = new(Id(luid, physical)); _adapters[(luid, physical)] = a;
        Adapters = [.. _adapters.Values]; // Only when a new adapter is observed.
        return a;
    }
    private void Read(nint counter, uint format, int kind)
    {
        if (counter == 0) return;
        uint size = (uint)_buffer.Length, count; uint status;
        fixed (byte* p = _buffer) status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, p);
        if (status == 0x800007D2 && size < 16 * 1024 * 1024)
        {
            _buffer = new byte[size + 4096]; size = (uint)_buffer.Length;
            fixed (byte* p = _buffer) status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, p);
        }
        if (status != 0) return;
        fixed (byte* p = _buffer)
        {
            for (uint i = 0; i < count; i++)
            {
                byte* item = p + i * 24; char* name = *(char**)item;
                if (*(uint*)(item + 8) > 1 || name == null) continue;
                int length = 0; while (length < 1024 && name[length] != 0) length++;
                var text = new ReadOnlySpan<char>(name, length);
                int at = text.IndexOf("luid_"), phys = text.IndexOf("_phys_");
                if (at < 0 || phys < at + 5) continue;
                var luidText = text[(at + 5)..phys]; int sep = luidText.IndexOf('_');
                if (sep < 2 || sep + 3 > luidText.Length || !uint.TryParse(luidText[2..sep], NumberStyles.HexNumber, null, out uint hi)
                    || !uint.TryParse(luidText[(sep + 3)..], NumberStyles.HexNumber, null, out uint lo)) continue;
                var tail = text[(phys + 6)..]; int end = tail.IndexOf('_');
                if (!uint.TryParse(end < 0 ? tail : tail[..end], out uint physical)) continue;
                ulong luid = (ulong)hi << 32 | lo;
                var a = Adapter(luid, physical);
                if (kind != 0)
                {
                    long value = Math.Max(0, *(long*)(item + 16));
                    if (kind == 1) { a.DedicatedUsed = value; a.MemoryAvailable = true; } else a.SharedUsed = value;
                    continue;
                }
                double load = *(double*)(item + 16);
                if (!double.IsFinite(load) || load < 0) continue;
                int engAt = text.IndexOf("_eng_"); if (engAt < 0) continue;
                var engine = text[(engAt + 5)..]; end = engine.IndexOf('_');
                if (!int.TryParse(end < 0 ? engine : engine[..end], out int eng)) continue;
                var key = (luid, physical, eng); _engines[key] = _engines.GetValueOrDefault(key) + load;
                int pidEnd = text.IndexOf("_luid_");
                if (text.StartsWith("pid_") && pidEnd > 4 && int.TryParse(text[4..pidEnd], out int pid))
                    _byPid[pid] = Math.Max(_byPid.GetValueOrDefault(pid), load);
            }
        }
    }
    public void Dispose() { if (_query != 0) PdhCloseQuery(_query); _query = 0; Available = false; }
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhOpenQueryW(string? source, nint data, out nint query);
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhAddEnglishCounterW(nint query, string path, nint data, out nint counter);
    [LibraryImport("pdh.dll")] private static partial uint PdhCollectQueryData(nint query);
    [LibraryImport("pdh.dll")] private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, byte* items);
    [LibraryImport("pdh.dll")] private static partial uint PdhCloseQuery(nint query);
}
