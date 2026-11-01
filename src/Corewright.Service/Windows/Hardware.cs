using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Corewright.Service.Windows;

public sealed record CoreDto(int Id, int[] Threads, int? Performance, int Rank, int CcdRank, bool Tied, int EfficiencyClass);

public sealed record CcdDto(int Id, long L3Bytes, bool VCache, CoreDto[] Cores);

public sealed record TopologyDto(string Name, int Logical, int Physical, bool Hybrid, bool RankingAvailable, CcdDto[] Ccds);

/// <summary>
/// CPU layout read once at start: CCDs (L3 groups) → physical cores → logical processors, plus the CPPC
/// "preferred core" ranking that Ryzen Master and Intel Turbo Boost Max show (Kernel-Processor-Power event 55).
/// </summary>
internal static unsafe partial class CpuTopologyProbe
{
    public static TopologyDto Read()
    {
        var cores = new List<(ulong Mask, byte Eff)>();
        var l3 = new List<(ulong Mask, long Size)>();

        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        var buf = new byte[len];
        fixed (byte* p = buf)
        {
            if (len > 0 && GetLogicalProcessorInformationEx(0xFFFF, p, ref len))
            {
                for (uint off = 0; off < len;)
                {
                    int rel = *(int*)(p + off);
                    uint size = *(uint*)(p + off + 4);
                    byte* r = p + off + 8;
                    if (rel == 0) // RelationProcessorCore
                    {
                        ushort groups = *(ushort*)(r + 22);
                        if (groups > 0 && *(ushort*)(r + 24 + 8) == 0) cores.Add((*(ulong*)(r + 24), r[1]));
                    }
                    else if (rel == 2 && r[0] == 3) // RelationCache, level 3
                    {
                        ushort groups = *(ushort*)(r + 30);
                        if (groups > 0 && *(ushort*)(r + 32 + 8) == 0) l3.Add((*(ulong*)(r + 32), *(uint*)(r + 4)));
                    }
                    if (size == 0) break;
                    off += size;
                }
            }
        }

        var perf = ReadCppcRanking();
        if (l3.Count == 0) l3.Add((cores.Aggregate(0UL, (m, c) => m | c.Mask), 0));

        // Physical cores in order of their first logical processor.
        var ordered = cores.OrderBy(c => System.Numerics.BitOperations.TrailingZeroCount(c.Mask)).ToList();
        var corePerf = ordered.Select(c => Threads(c.Mask).Select(t => perf.GetValueOrDefault(t)).DefaultIfEmpty(0).Max()).ToArray();
        bool ranked = corePerf.Any(v => v > 0) && corePerf.Distinct().Count() > 1;
        var overall = corePerf.Select((v, i) => (v, i)).OrderByDescending(x => x.v).Select((x, rank) => (x.i, rank)).ToDictionary(x => x.i, x => x.rank + 1);

        long maxL3 = l3.Max(c => c.Size);
        bool mixedL3 = l3.Select(c => c.Size).Distinct().Count() > 1;
        var ccds = new List<CcdDto>();
        int coreId = 0;
        var coreIds = ordered.Select(_ => coreId++).ToArray();
        foreach (var (cache, ccdIndex) in l3.OrderBy(c => System.Numerics.BitOperations.TrailingZeroCount(c.Mask)).Select((c, i) => (c, i)))
        {
            var members = ordered.Select((c, i) => (c, i)).Where(x => (x.c.Mask & cache.Mask) != 0).ToList();
            var values = members.Select(x => corePerf[x.i]).Distinct().OrderByDescending(v => v).ToList();
            var list = members.Select(x =>
            {
                int v = corePerf[x.i];
                int ccdRank = ranked && v > 0 ? values.IndexOf(v) + 1 : 0;
                bool tied = ranked && members.Count(m => corePerf[m.i] == v) > 1 && ccdRank <= 2;
                return new CoreDto(coreIds[x.i], Threads(x.c.Mask), v > 0 ? v : null, ranked ? overall[x.i] : 0, ccdRank, tied, x.c.Eff);
            }).ToArray();
            // V-Cache: the larger L3 on mixed parts; on parts where every CCD has 3D V-Cache (e.g. X3D2) all are marked.
            bool vcache = cache.Size >= 64L * 1024 * 1024 && (!mixedL3 || cache.Size == maxL3);
            ccds.Add(new CcdDto(ccdIndex, cache.Size, vcache, list));
        }

        return new CcdTopology(ccds, ranked).ToDto(CpuName(), Environment.ProcessorCount);
    }

    private sealed record CcdTopology(List<CcdDto> Ccds, bool Ranked)
    {
        public TopologyDto ToDto(string name, int _)
        {
            int logical = Ccds.Sum(c => c.Cores.Sum(k => k.Threads.Length));
            int physical = Ccds.Sum(c => c.Cores.Length);
            bool hybrid = Ccds.SelectMany(c => c.Cores).Select(k => k.EfficiencyClass).Distinct().Count() > 1;
            return new TopologyDto(name, logical, physical, hybrid, Ranked, [.. Ccds]);
        }
    }

    private static int[] Threads(ulong mask)
    {
        var list = new List<int>();
        for (int i = 0; i < 64; i++)
            if ((mask & (1UL << i)) != 0) list.Add(i);
        return [.. list];
    }

    private static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return "CPU"; }
    }

    // ---- CPPC ranking from Kernel-Processor-Power event 55 (written at boot for every logical processor) ----

    [GeneratedRegex(@"<Data Name='Number'>(\d+)</Data>.*?<Data Name='MaximumPerformancePercent'>(\d+)</Data>", RegexOptions.Singleline)]
    private static partial Regex PerfPattern();

    private static Dictionary<int, int> ReadCppcRanking()
    {
        var result = new Dictionary<int, int>();
        nint query = EvtQuery(0, "System",
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=55)]]", 0x1 | 0x200);
        if (query == 0) return result;
        try
        {
            var handles = new nint[64];
            char* text = stackalloc char[8192];
            while (result.Count < 256)
            {
                uint got;
                fixed (nint* h = handles)
                    if (!EvtNext(query, (uint)handles.Length, h, 1000, 0, out got) || got == 0) break;
                for (int i = 0; i < got; i++)
                {
                    try
                    {
                        if (EvtRender(0, handles[i], 1 /* EvtRenderEventXml */, 8192 * 2, text, out uint used, out _))
                        {
                            var m = PerfPattern().Match(new string(text, 0, Math.Max(0, (int)used / 2 - 1)));
                            // Newest first: keep the first value per processor (the most recent boot).
                            if (m.Success) result.TryAdd(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                        }
                    }
                    finally
                    {
                        EvtClose(handles[i]);
                    }
                }
                if (got < handles.Length) break;
            }
        }
        finally
        {
            EvtClose(query);
        }
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, uint count, nint* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferSize, char* buffer, out uint used, out uint properties);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}

/// <summary>Per logical processor busy % from one SystemProcessorPerformanceInformation call (group 0).</summary>
internal sealed unsafe class CoreUsageSampler
{
    private const int Stride = 48; // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    private long[] _idle = [], _total = [];

    public float[] Usage { get; private set; } = [];

    public void Sample()
    {
        int count = Math.Min(64, (int)Native.GetActiveProcessorCount(0));
        byte* buf = stackalloc byte[64 * Stride];
        if (Native.NtQuerySystemInformation(8, buf, (uint)(count * Stride), out _) < 0) return;
        if (_idle.Length != count) { _idle = new long[count]; _total = new long[count]; Usage = new float[count]; }
        for (int i = 0; i < count; i++)
        {
            long idle = *(long*)(buf + i * Stride), kernel = *(long*)(buf + i * Stride + 8), user = *(long*)(buf + i * Stride + 16);
            long total = kernel + user; // kernel time includes idle time
            long dTotal = total - _total[i], dIdle = idle - _idle[i];
            if (_total[i] != 0 && dTotal > 0) Usage[i] = (float)Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _idle[i] = idle;
            _total[i] = total;
        }
    }
}

/// <summary>Network send and receive rates summed over physical, connected interfaces (GetIfTable2).</summary>
internal sealed unsafe partial class NetworkSampler
{
    private const int RowSize = 1352; // MIB_IF_ROW2
    private long _lastIn, _lastOut, _lastTs;

    public double ReceiveBytesPerSec { get; private set; }
    public double SendBytesPerSec { get; private set; }

    public void Sample(DeviceMonitor? devices = null, bool active = false)
    {
        if (GetIfTable2(out nint table) != 0) return;
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = _lastTs == 0 ? 0 : (double)(stamp - _lastTs) / System.Diagnostics.Stopwatch.Frequency;
        long inOctets = 0, outOctets = 0;
        try
        {
            uint n = *(uint*)table;
            byte* rows = (byte*)table + 8;
            for (uint i = 0; i < n; i++)
            {
                byte* r = rows + i * RowSize;
                devices?.ObserveNetwork(r, active, elapsed);
                // MIB_IF_ROW2: InterfaceAndOperStatusFlags@1152 (bit0 HardwareInterface), OperStatus@1156 (1 = up),
                // InOctets@1208, OutOctets@1280 (row size 1352).
                if ((r[1152] & 1) == 0 || *(int*)(r + 1156) != 1) continue;
                inOctets += *(long*)(r + 1208);
                outOctets += *(long*)(r + 1280);
            }
        }
        finally
        {
            FreeMibTable(table);
        }
        devices?.EndNetworkInventory();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastTs != 0)
        {
            double dt = (double)(now - _lastTs) / System.Diagnostics.Stopwatch.Frequency;
            if (dt > 0)
            {
                ReceiveBytesPerSec = Math.Max(0, (inOctets - _lastIn) / dt);
                SendBytesPerSec = Math.Max(0, (outOctets - _lastOut) / dt);
            }
        }
        _lastIn = inOctets;
        _lastOut = outOctets;
        _lastTs = now;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint table);
}
