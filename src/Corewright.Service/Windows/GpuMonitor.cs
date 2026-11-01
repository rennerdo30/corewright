using System.Runtime.InteropServices;

namespace Corewright.Service.Windows;

public sealed record GpuAdapterDto(string Id, double Utilization, long DedicatedUsed);

/// <summary>
/// GPU utilization like Task Manager: the "GPU Engine" counters per (process, adapter, engine). An adapter's
/// utilization is its busiest engine (sum over processes); a process's is its busiest engine. Dedicated memory comes
/// from "GPU Adapter Memory". Sampled only while a client is watching, because the counter set is large.
/// </summary>
internal sealed unsafe partial class GpuMonitor : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_LARGE = 0x400, PDH_MORE_DATA = 0x800007D2;
    private nint _query, _engine, _memory;
    private bool _ready;
    private byte[] _buffer = new byte[256 * 1024];
    private readonly Dictionary<int, double> _engineSum = [];
    private readonly Dictionary<int, double> _byPid = [];

    public GpuAdapterDto[] Adapters { get; private set; } = [];
    public double Utilization { get; private set; }
    public long DedicatedUsed { get; private set; }
    public IReadOnlyDictionary<int, double> ByProcess => _byPid;
    public bool Available => _ready;

    public GpuMonitor()
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engine) != 0) return;
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _memory);
        _ready = PdhCollectQueryData(_query) == 0;
    }

    public void Sample()
    {
        if (!_ready || PdhCollectQueryData(_query) != 0) return;
        _engineSum.Clear();
        _byPid.Clear();
        var engines = Read(_engine, PDH_FMT_DOUBLE);
        foreach (var (name, value) in engines)
        {
            // pid_1234_luid_0x00000000_0x0000D1A5_phys_0_eng_3_engtype_3D
            var span = name.AsSpan();
            int luid = span.IndexOf("_luid_"), eng = span.IndexOf("_engtype_");
            if (luid < 0 || eng < 0) continue;
            int engKey = string.GetHashCode(span[luid..eng]);
            _engineSum[engKey] = _engineSum.GetValueOrDefault(engKey) + value;
            if (span.StartsWith("pid_") && int.TryParse(span[4..luid], out int pid))
                _byPid[pid] = Math.Max(_byPid.GetValueOrDefault(pid), value);
        }

        // An adapter's utilization is its busiest engine (summed over processes).
        var busiest = new Dictionary<string, double>();
        foreach (var (name, _) in engines)
        {
            var span = name.AsSpan();
            int luid = span.IndexOf("_luid_"), eng = span.IndexOf("_engtype_");
            if (luid < 0 || eng < 0) continue;
            string adapter = AdapterId(span[(luid + 1)..]);
            double sum = _engineSum.GetValueOrDefault(string.GetHashCode(span[luid..eng]));
            busiest[adapter] = Math.Max(busiest.GetValueOrDefault(adapter), Math.Min(100, sum));
        }

        var memory = new Dictionary<string, long>();
        if (_memory != 0)
            foreach (var (name, value) in Read(_memory, PDH_FMT_LARGE))
                memory[AdapterId(name.AsSpan())] = (long)value;

        Adapters = [.. busiest.Keys.Union(memory.Keys).Order()
            .Select(k => new GpuAdapterDto(k, Math.Round(busiest.GetValueOrDefault(k), 1), memory.GetValueOrDefault(k)))];
        Utilization = Adapters.Length == 0 ? 0 : Adapters.Max(a => a.Utilization);
        DedicatedUsed = Adapters.Sum(a => a.DedicatedUsed);
    }

    /// <summary>"luid_0x00000000_0x0000D1A5_phys_0..." → "0x0000D1A5:0".</summary>
    private static string AdapterId(ReadOnlySpan<char> s)
    {
        int luid = s.IndexOf("luid_");
        if (luid < 0) return s.ToString();
        s = s[(luid + 5)..];
        int phys = s.IndexOf("_phys_");
        var luidPart = phys < 0 ? s : s[..phys];
        int sep = luidPart.LastIndexOf('_');
        string low = (sep < 0 ? luidPart : luidPart[(sep + 1)..]).ToString();
        string physNo = "0";
        if (phys >= 0)
        {
            var rest = s[(phys + 6)..];
            int end = rest.IndexOf('_');
            physNo = (end < 0 ? rest : rest[..end]).ToString();
        }
        return $"{low}:{physNo}";
    }

    private List<(string Name, double Value)> Read(nint counter, uint format)
    {
        var list = new List<(string, double)>();
        uint size = (uint)_buffer.Length;
        uint status;
        uint count;
        fixed (byte* p = _buffer) status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, p);
        if (status == PDH_MORE_DATA)
        {
            _buffer = new byte[size + 4096];
            size = (uint)_buffer.Length;
            fixed (byte* p = _buffer) status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, p);
        }
        if (status != 0) return list;
        fixed (byte* p = _buffer)
        {
            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; { DWORD CStatus; (pad) union { double; LONGLONG } } } = 24 bytes
            for (uint i = 0; i < count; i++)
            {
                byte* item = p + i * 24;
                char* name = *(char**)item;
                uint cstatus = *(uint*)(item + 8);
                if (cstatus > 1 || name == null) continue;
                double v = format == PDH_FMT_LARGE ? *(long*)(item + 16) : *(double*)(item + 16);
                list.Add((new string(name), v));
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
        _ready = false;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
