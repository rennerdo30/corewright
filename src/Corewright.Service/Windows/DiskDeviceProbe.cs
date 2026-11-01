using System.Runtime.InteropServices;
using System.Text;

namespace Corewright.Service.Windows;

internal static unsafe partial class DiskDeviceProbe
{
    private const uint QueryProperty = 0x2D1400;
    public static List<DeviceMonitor.State>? Inventory(Func<bool> clientActive)
    {
        var guid = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK
        byte* buf = stackalloc byte[4096];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint count, &guid, null, 0) != 0 || count > 65536) return null;
            var names = new char[Math.Max(count, 2)];
            fixed (char* p = names)
            {
                if (CM_Get_Device_Interface_ListW(&guid, null, p, (uint)names.Length, 0) != 0) continue;
                var result = new List<DeviceMonitor.State>();
                for (int off = 0; off < names.Length && p[off] != 0;)
                {
                    if (!clientActive()) return null;
                    string path = new(p + off); off += path.Length + 1;
                    nint h = Open(path);
                    if (h == -1) continue;
                    try
                    {
                        if (!clientActive()) return null;
                        if (!Io(h, 0x2D1080, null, 0, buf, 12, out uint got) || got < 12) continue;
                        int index = *(int*)(buf + 4);
                        string model = $"Physical disk {index}", bus = "Unknown";
                        long? size = null;
                        if (Property(h, 0, buf, 4096, out got) && got >= 36)
                        {
                            string vendor = Text(buf, got, *(uint*)(buf + 12)), product = Text(buf, got, *(uint*)(buf + 16));
                            if ((vendor + product).Length > 0) model = (vendor + " " + product).Trim();
                            bus = *(uint*)(buf + 28) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 1 => "SCSI", 3 => "ATA", 15 => "Virtual", 16 => "Storage Spaces", _ => "Unknown" };
                        }
                        if (Io(h, 0x700A0, null, 0, buf, 4096, out got) && got >= 32) size = *(long*)(buf + 24);
                        result.Add(new(new($"disk:{index}", "disk", model, index, bus, size)) { Path = path });
                    }
                    finally { Native.CloseHandle(h); }
                }
                ReadVolumes(result, clientActive);
                result.Sort(static (a, b) => Nullable.Compare(a.Info.Index, b.Info.Index));
                return result;
            }
        }
        return null;
    }
    private static string Text(byte* buf, uint length, uint offset)
    {
        if (offset == 0 || offset >= length) return "";
        int n = 0; while (offset + n < length && buf[offset + n] != 0) n++;
        return Encoding.ASCII.GetString(new ReadOnlySpan<byte>(buf + offset, n)).Trim();
    }
    private static void ReadVolumes(List<DeviceMonitor.State> disks, Func<bool> clientActive)
    {
        char* name = stackalloc char[1024];
        byte* extents = stackalloc byte[4096];
        char* paths = stackalloc char[32768];
        var volumes = new Dictionary<int, List<string>>();
        nint find = FindFirstVolumeW(name, 1024);
        if (find == -1) return;
        try
        {
            do
            {
                if (!clientActive()) return;
                string volume = new(name);
                nint h = Open(volume.TrimEnd('\\'));
                if (h == -1) continue;
                try
                {
                    if (!clientActive()) return;
                    if (!Io(h, 0x560000, null, 0, extents, 4096, out uint got) || got < 8) continue;
                    uint count = *(uint*)extents;
                    if (count > (got - 8) / 24) continue;
                    var labels = new List<string>();
                    if (GetVolumePathNamesForVolumeNameW(volume, paths, 32768, out uint used) && used <= 32768)
                        for (int off = 0; off < used && paths[off] != 0;)
                        { string s = new(paths + off); off += s.Length + 1; labels.Add(s); }
                    if (labels.Count == 0) labels.Add(volume);
                    for (int i = 0; i < count; i++)
                    {
                        int index = *(int*)(extents + 8 + i * 24);
                        if (!volumes.TryGetValue(index, out var list)) volumes[index] = list = [];
                        list.AddRange(labels);
                    }
                }
                finally { Native.CloseHandle(h); }
            } while (FindNextVolumeW(find, name, 1024));
        }
        finally { FindVolumeClose(find); }
        foreach (var s in disks)
            s.Info = s.Info with { Volumes = volumes.TryGetValue(s.Info.Index!.Value, out var list) ? [.. list.Distinct()] : null };
    }
    public static void Sample(DeviceMonitor.State s, long now, long time, Func<bool> clientActive)
    {
        if (s.Disk == -1 && now >= s.NextOpenAttemptMs)
        {
            s.Disk = Open(s.Path);
            s.NextOpenAttemptMs = now + 60_000;
            s.Baseline = false;
        }
        var v = s.Info.Values;
        v.Read = v.Write = v.Active = null; v.SampledAt = time;
        if (s.Disk == -1) return;
        if (!clientActive()) return;
        byte* b = stackalloc byte[4096];
        if (Io(s.Disk, 0x70020, null, 0, b, 88, out uint got) && got >= 64)
        {
            long read = *(long*)b, write = *(long*)(b + 8), idle = *(long*)(b + 32), query = *(long*)(b + 56);
            long dt = query - s.LastQuery;
            if (s.Baseline && dt > 0 && read >= s.LastRead && write >= s.LastWrite && idle >= s.LastIdle)
            {
                v.Read = (read - s.LastRead) * 1e7 / dt; v.Write = (write - s.LastWrite) * 1e7 / dt;
                v.Active = Math.Clamp(100.0 * (1 - (double)(idle - s.LastIdle) / dt), 0, 100);
            }
            s.LastRead = read; s.LastWrite = write; s.LastIdle = idle; s.LastQuery = query; s.Baseline = true;
        }
        else s.Baseline = false;
        if (now < s.HealthDue || !clientActive()) return;
        s.HealthDue = now + 60_000;
        var health = s.Info.Health;
        health.Temperature = null;
        health.WearPercent = health.SparePercent = health.CriticalWarning = null;
        health.MediaErrors = health.PowerOnHours = health.UnsafeShutdowns = health.DataUnitsRead = health.DataUnitsWritten = null;
        bool temperature = Property(s.Disk, 52, b, 4096, out got);
        if (!clientActive()) { s.HealthDue = 0; return; }
        if (temperature && got >= 40 && *(ushort*)(b + 12) > 0 && *(short*)(b + 26) != short.MinValue)
            health.Temperature = *(short*)(b + 26);
        if (s.Info.Bus == "NVMe")
        {
            // STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA; SMART/health log 02h, 512 bytes.
            new Span<byte>(b, 4096).Clear();
            *(uint*)b = 49; *(uint*)(b + 8) = 3; *(uint*)(b + 12) = 2; *(uint*)(b + 16) = 2;
            *(uint*)(b + 24) = 40; *(uint*)(b + 28) = 512;
            if (Io(s.Disk, QueryProperty, b, 48, b, 4096, out got) && got >= 48 && *(uint*)b >= 48 && *(uint*)(b + 4) >= 48)
            {
                uint offset = *(uint*)(b + 24), length = *(uint*)(b + 28);
                if (offset >= 40 && length >= 512 && offset <= got - 8 && length <= got - 8 - offset)
                {
                    byte* log = b + 8 + offset;
                    health.CriticalWarning = log[0]; health.SparePercent = log[3]; health.WearPercent = log[5];
                    int kelvin = *(ushort*)(log + 1);
                    if (kelvin > 0 && kelvin < 1000) health.Temperature ??= kelvin - 273.15;
                    health.DataUnitsRead = Counter(log + 32); health.DataUnitsWritten = Counter(log + 48);
                    health.PowerOnHours = Counter(log + 128); health.UnsafeShutdowns = Counter(log + 144); health.MediaErrors = Counter(log + 160);
                }
            }
        }
        health.SampledAt = time;
    }
    private static string Counter(byte* p) => ((UInt128)(*(ulong*)(p + 8)) << 64 | *(ulong*)p).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool Property(nint h, uint id, byte* b, uint size, out uint got)
    { uint* query = stackalloc uint[3]; query[0] = id; query[1] = query[2] = 0; return Io(h, QueryProperty, query, 12, b, size, out got); }
    private static nint Open(string path) => CreateFileW(path, 0, 7, 0, 3, 0, 0);
    private static bool Io(nint h, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint got) => DeviceIoControl(h, code, input, inputSize, output, outputSize, out got, 0);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeviceIoControl(nint h, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint got, nint overlapped);
    [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Get_Device_Interface_List_SizeW(out uint count, Guid* guid, char* device, uint flags);
    [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Get_Device_Interface_ListW(Guid* guid, char* device, char* list, uint count, uint flags);
    [LibraryImport("kernel32.dll")] private static partial nint FindFirstVolumeW(char* name, uint count);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool FindNextVolumeW(nint find, char* name, uint count);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool FindVolumeClose(nint find);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetVolumePathNamesForVolumeNameW(string name, char* paths, uint count, out uint used);
}
