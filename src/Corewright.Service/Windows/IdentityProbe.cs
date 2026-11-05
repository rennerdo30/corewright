using System.Runtime.InteropServices;
using Corewright.Core.Engine;

namespace Corewright.Service.Windows;

/// <summary>Bounded, lifetime-only identity reads. Failure leaves host identity unknown.</summary>
internal static unsafe partial class IdentityProbe
{
    public static string? WorkingDirectory(ProcessKey key)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | 0x10, false, key.Pid);
        if (h == 0) return null;
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime) return null;
            nint* basic = stackalloc nint[6];
            if (Native.NtQueryInformationProcess(h, 0, basic, (uint)(6 * sizeof(nint)), out _) < 0) return null;
            nint wow = 0;
            Native.NtQueryInformationProcess(h, 26, &wow, (uint)sizeof(nint), out _);
            int width = wow == 0 ? 8 : 4;
            nint peb = wow == 0 ? basic[1] : wow;
            long parameters = 0;
            if (!ReadProcessMemory(h, peb + (width == 8 ? 0x20 : 0x10), &parameters, (nuint)width, out _)) return null;
            byte* directory = stackalloc byte[16];
            if (!ReadProcessMemory(h, (nint)parameters + (width == 8 ? 0x38 : 0x24), directory, (nuint)(width == 8 ? 16 : 8), out _)) return null;
            int length = *(ushort*)directory;
            if (length == 0 || length > 32766 || (length & 1) != 0) return null;
            nint text = width == 8 ? *(nint*)(directory + 8) : (nint)(*(uint*)(directory + 4));
            char[] buffer = new char[length / 2];
            fixed (char* chars = buffer)
                return ReadProcessMemory(h, text, chars, (nuint)length, out var read) && read == (nuint)length ? new string(buffer) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>SERVICE_WIN32 (own and shared processes) plus per-user services and their instances (CDPUserSvc_1a2b...).</summary>
    private const uint ServiceTypes = 0x30 | 0x40 | 0x80;
    private static readonly Lock ServiceLock = new();
    private static Dictionary<int, ServiceInfo[]>? _services;
    /// <summary>When the snapshot was taken: FILETIME (UTC, for creation-time checks) and tick count (throttle).</summary>
    private static long _servicesFileTime, _servicesAttemptMs = long.MinValue / 2;

    /// <summary>Increases with every successful snapshot, so callers can reconcile known service processes once per snapshot.</summary>
    public static int ServicesVersion { get; private set; }

    /// <summary>
    /// The Windows services running in a process (PM-46), sorted by name; null when it runs none or it cannot be told yet.
    /// One snapshot of all running services serves every lookup within a second (the start, with 80+ svchost processes,
    /// costs one enumeration); attempts are throttled to one a second whether they succeed or not. A snapshot only
    /// answers for processes that already existed when it was taken, so a PID reused by a newer process never inherits
    /// the services of the process that had it before; such a process waits for the next snapshot.
    /// </summary>
    public static ServiceInfo[]? Services(ProcessKey key, out bool known)
    {
        lock (ServiceLock)
        {
            long now = Environment.TickCount64;
            if ((_services is null || key.CreateTime >= _servicesFileTime) && now - _servicesAttemptMs >= 1000)
            {
                _servicesAttemptMs = now;
                long taken = DateTime.UtcNow.ToFileTimeUtc();
                if (SnapshotServices() is { } fresh) { _services = fresh; _servicesFileTime = taken; ServicesVersion++; }
            }
            known = _services is not null && key.CreateTime < _servicesFileTime;
            return known && _services!.TryGetValue(key.Pid, out var list) ? list : null;
        }
    }

    private static Dictionary<int, ServiceInfo[]>? SnapshotServices()
    {
        nint scm = OpenSCManagerW(null, null, 4);
        if (scm == 0) return null;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, 0, ServiceTypes, 1, null, 0, out uint needed, out _, ref resume, null);
            if (needed == 0 || needed > 1 << 20) return null;
            byte[] buffer = new byte[needed];
            fixed (byte* bytes = buffer)
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, 0, ServiceTypes, 1, bytes, (uint)buffer.Length, out _, out uint count, ref resume, null)) return null;
                var byPid = new Dictionary<int, List<ServiceInfo>>();
                var rows = (ServiceRow*)bytes;
                for (int i = 0; i < count; i++)
                {
                    int pid = (int)rows[i].Pid;
                    if (pid <= 4 || rows[i].Name == null) continue;
                    string name = new(rows[i].Name);
                    string display = rows[i].Display == null ? name : new string(rows[i].Display);
                    // A per-user service instance is "<template>_<session LUID>" (CDPUserSvc_e903b), new at every sign-in;
                    // the template name keeps rules and identities stable.
                    if ((rows[i].Type & 0x80) != 0) { name = WithoutInstance(name); display = WithoutInstance(display); }
                    if (!byPid.TryGetValue(pid, out var list)) byPid[pid] = list = [];
                    list.Add(new ServiceInfo(name, display));
                }
                var result = new Dictionary<int, ServiceInfo[]>(byPid.Count);
                foreach (var (pid, list) in byPid)
                {
                    list.Sort((a, b) => string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));
                    result[pid] = [.. list];
                }
                return result;
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static string WithoutInstance(string s)
    {
        int cut = s.LastIndexOf('_');
        return cut > 0 && cut < s.Length - 1 && s.AsSpan(cut + 1).ContainsAnyExcept("0123456789abcdefABCDEF") == false ? s[..cut] : s;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceRow
    {
        public char* Name;
        public char* Display;
        public uint Type, State, Accepted, Exit, SpecificExit, Checkpoint, WaitHint, Pid, Flags;
    }
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint length, out nuint read);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(nint scm, int info, uint type, uint state, byte* buffer, uint size,
        out uint needed, out uint count, ref uint resume, string? group);
}
