using Corewright.Core.Rules;

namespace Corewright.Service.Windows;

internal static unsafe class SystemProbe
{
    /// <summary>Reads CPU Sets once at start: logical processor index and efficiency class of group 0.</summary>
    public static SystemTopology ReadTopology()
    {
        int count = (int)Math.Clamp(Native.GetActiveProcessorCount(0), 1, 64);
        Native.GetSystemCpuSetInformation(null, 0, out uint needed, Native.GetCurrentProcess(), 0);
        if (needed == 0) return SystemTopology.Uniform(count);

        byte* buf = stackalloc byte[(int)Math.Min(needed, 64 * 1024)];
        if (needed > 64 * 1024 || !Native.GetSystemCpuSetInformation(buf, needed, out needed, Native.GetCurrentProcess(), 0))
            return SystemTopology.Uniform(count);

        // SYSTEM_CPU_SET_INFORMATION: Size@0, Type@4, Id@8, Group@12, LogicalProcessorIndex@14, EfficiencyClass@18.
        var classes = new List<(int Lp, byte Class)>();
        byte max = 0;
        for (uint off = 0; off < needed;)
        {
            byte* e = buf + off;
            uint size = *(uint*)e;
            if (size == 0) break;
            if (*(int*)(e + 4) == 0 && *(ushort*)(e + 12) == 0)
            {
                byte lp = e[14], cls = e[18];
                if (lp < 64)
                {
                    classes.Add((lp, cls));
                    if (cls > max) max = cls;
                }
            }
            off += size;
        }

        ulong perf = 0, eff = 0;
        foreach (var (lp, cls) in classes)
        {
            if (cls == max) perf |= 1UL << lp;
            else eff |= 1UL << lp;
        }
        return new SystemTopology(count, perf, eff);
    }

    /// <summary>Enables the privileges a SYSTEM service holds but has disabled by default. No-op when not held.</summary>
    public static void EnablePrivileges()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0020 | 0x0008, out nint token)) return;
        try
        {
            foreach (var name in new[] { "SeDebugPrivilege", "SeIncreaseBasePriorityPrivilege", "SeIncreaseQuotaPrivilege", "SeIncWorkingSetPrivilege" })
            {
                if (!Native.LookupPrivilegeValueW(null, name, out long luid)) continue;
                var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 0x2 };
                Native.AdjustTokenPrivileges(token, false, &tp, (uint)sizeof(Native.TOKEN_PRIVILEGES), 0, 0);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    /// <summary>Reads a command line (ProcessCommandLineInformation) after checking the creation time against PID reuse.</summary>
    public static string? ReadCommandLine(Corewright.Core.Engine.ProcessKey key)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == 0) return null;
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime) return null;
            uint size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
                try
                {
                    fixed (byte* p = buf)
                    {
                        int st = Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, p, (uint)buf.Length, out uint needed);
                        if (st == Native.STATUS_INFO_LENGTH_MISMATCH || st == unchecked((int)0xC0000023))
                        {
                            size = Math.Max(needed, size * 2);
                            continue;
                        }
                        if (st < 0) return null;
                        // UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer } at the start of the buffer.
                        int len = *(ushort*)p / 2;
                        char* text = (char*)*(nint*)(p + 8);
                        return len == 0 || text == null ? "" : new string(text, 0, len);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                }
            }
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static int ForegroundPid() => ForegroundInfo().Pid;

    /// <summary>Foreground process and whether its window covers its whole monitor (full screen or borderless).</summary>
    public static (int Pid, bool Fullscreen) ForegroundInfo()
    {
        nint w = Native.GetForegroundWindow();
        if (w == 0) return (0, false);
        Native.GetWindowThreadProcessId(w, out uint pid);
        return ((int)pid, IsFullscreen(w));
    }

    public static bool IsFullscreen(nint window)
    {
        char* cls = stackalloc char[64];
        int n = Native.GetClassNameW(window, cls, 64);
        var name = new ReadOnlySpan<char>(cls, n);
        if (name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW") || name.SequenceEqual("Shell_TrayWnd")) return false;
        if (!Native.GetWindowRect(window, out var r)) return false;
        nint monitor = Native.MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new Native.MONITORINFO { Size = (uint)sizeof(Native.MONITORINFO) };
        if (!Native.GetMonitorInfoW(monitor, &info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }
}
