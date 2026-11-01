using System.Runtime.InteropServices;
namespace Corewright.Service.Windows;

internal static unsafe partial class GpuDeviceProbe
{
    public static List<DeviceMonitor.State>? Inventory()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(&iid, out nint factory) < 0) return null;
        var list = new List<DeviceMonitor.State>();
        byte* desc = stackalloc byte[312]; // DXGI_ADAPTER_DESC1, win-x64
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint i = 0; i < 64; i++)
            {
                nint adapter;
                if (enumerate(factory, i, &adapter) < 0) break;
                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(nint**)adapter)[10];
                    if (getDesc(adapter, desc) < 0 || (*(uint*)(desc + 304) & 2) != 0) continue;
                    ulong luid = *(ulong*)(desc + 296);
                    list.Add(new(new(GpuMonitor.Id(luid), "gpu", new string((char*)desc), DedicatedMemory: *(long*)(desc + 272), SharedMemory: *(long*)(desc + 288))) { Luid = luid });
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return list;
    }
    public static void Sample(DeviceMonitor.State s, GpuMonitor? gpu, long time)
    {
        var v = s.Info.Values;
        v.Load = null; v.DedicatedUsed = v.SharedUsed = null; v.SampledAt = time;
        if (gpu is not null)
            foreach (var a in gpu.Adapters)
                if (a.Id == s.Info.Id)
                { v.Load = a.LoadAvailable ? a.Utilization : null; v.DedicatedUsed = a.MemoryAvailable ? a.DedicatedUsed : null; v.SharedUsed = a.SharedUsed; break; }
        var health = s.Info.Health;
        byte* data = stackalloc byte[64];
        health.Temperature = health.FanRpm = health.PowerPercent = health.EngineClockMhz = null;
        if (s.Adapter == 0)
        {
            var open = new OpenAdapter { Luid = s.Luid };
            if (D3DKMTOpenAdapterFromLuid(&open) < 0) return;
            s.Adapter = open.Handle;
            new Span<byte>(data, 64).Clear();
            s.FanAvailable = Query(s.Adapter, 63, data, 40) >= 0 && *(uint*)(data + 24) > 0;
        }
        new Span<byte>(data, 64).Clear();
        if (Query(s.Adapter, 62, data, 64) >= 0)
        {
            uint temp = *(uint*)(data + 56), fan = *(uint*)(data + 48), power = *(uint*)(data + 52);
            health.Temperature = temp > 0 && temp < 2000 ? temp / 10.0 : null;
            health.FanRpm = (fan > 0 || s.FanAvailable) && fan < 100000 ? fan : null;
            // Windows reports tenths of a percent, NOT watts. Zero often means unsupported.
            health.PowerPercent = power > 0 && power < 10000 ? power / 10.0 : null;
        }
        new Span<byte>(data, 64).Clear(); // Node 0; no scan through engines each tick.
        if (Query(s.Adapter, 61, data, 56) >= 0 && *(ulong*)(data + 8) > 0)
            health.EngineClockMhz = *(ulong*)(data + 8) / 1e6;
        health.SampledAt = time;
    }
    private static int Query(uint handle, int type, void* data, uint size)
    { var q = new QueryInfo { Handle = handle, Type = type, Data = data, Size = size }; return D3DKMTQueryAdapterInfo(&q); }
    public static void Close(uint handle) => D3DKMTCloseAdapter(&handle);
    private static void Release(nint obj) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)obj)[2])(obj);
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapter { public ulong Luid; public uint Handle; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public uint Handle; public int Type; public void* Data; public uint Size; }
    [LibraryImport("dxgi.dll")] private static partial int CreateDXGIFactory1(Guid* iid, out nint factory);
    [LibraryImport("gdi32.dll")] private static partial int D3DKMTOpenAdapterFromLuid(OpenAdapter* open);
    [LibraryImport("gdi32.dll")] private static partial int D3DKMTQueryAdapterInfo(QueryInfo* info);
    [LibraryImport("gdi32.dll")] private static partial int D3DKMTCloseAdapter(uint* handle);
}
