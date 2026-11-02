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
            nint text = width == 8 ? *(nint*)(directory + 8) : (nint)*(uint*)(directory + 4);
            char[] buffer = new char[length / 2];
            fixed (char* chars = buffer)
                return ReadProcessMemory(h, text, chars, (nuint)length, out var read) && read == (nuint)length ? new string(buffer) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    public static string[]? Services(int pid)
    {
        nint scm = OpenSCManagerW(null, null, 4);
        if (scm == 0) return null;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, 0, 0x30, 1, null, 0, out uint needed, out _, ref resume, null);
            if (needed == 0 || needed > 1 << 20) return null;
            byte[] buffer = new byte[needed];
            fixed (byte* bytes = buffer)
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, 0, 0x30, 1, bytes, (uint)buffer.Length, out _, out uint count, ref resume, null)) return null;
                var names = new List<string>();
                var rows = (ServiceRow*)bytes;
                for (int i = 0; i < count; i++) if (rows[i].Pid == pid) names.Add(new string(rows[i].Name).ToLowerInvariant());
                names.Sort(StringComparer.Ordinal);
                return names.Count == 0 ? null : names.ToArray();
            }
        }
        finally { CloseServiceHandle(scm); }
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
