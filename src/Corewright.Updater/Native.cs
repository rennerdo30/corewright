using System.Runtime.InteropServices;

/// <summary>
/// Starting a program unelevated as the desktop's user from an elevated process: duplicate the primary token of the
/// shell window's process (Explorer, always the signed-in user, even when an administrator approved our prompt with
/// another account) and use CreateProcessWithTokenW. Administrators hold SeImpersonatePrivilege, which this needs.
/// </summary>
internal static class Native
{
    public static bool StartAsShellUser(string exe)
    {
        nint shell = GetShellWindow();
        if (shell == 0) return false;
        GetWindowThreadProcessId(shell, out uint pid);
        if (pid == 0) return false;
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return false;
        nint token = 0, primary = 0;
        try
        {
            if (!OpenProcessToken(process, TokenDuplicate, out token)) return false;
            const uint access = TokenQuery | TokenAssignPrimary | TokenDuplicate | TokenAdjustDefault | TokenAdjustSessionId;
            if (!DuplicateTokenEx(token, access, 0, SecurityImpersonation, TokenPrimary, out primary)) return false;
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            string? dir = Path.GetDirectoryName(exe);
            if (!CreateProcessWithTokenW(primary, 0, exe, $"\"{exe}\"", 0, 0, dir, ref si, out var pi)) return false;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (primary != 0) CloseHandle(primary);
            if (token != 0) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001, TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAdjustDefault = 0x0080,
        TokenAdjustSessionId = 0x0100;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string app, string commandLine, uint flags,
        nint environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
