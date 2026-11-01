# Embedded by NSIS; never run by package.ps1. All mutations require installer elevation.
param([ValidateSet('SameUser', 'SecureData')][string]$Action, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class InstallerSecurity {
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string s, string n, out long l);
    [StructLayout(LayoutKind.Sequential, Pack=4)] struct Privilege { public uint Count; public long Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref Privilege p, int n, IntPtr a, IntPtr b);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr d, uint c, uint f, IntPtr t);
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, HighSize, LowSize, Links, HighIndex, LowIndex; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info i);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string s, uint r, out IntPtr d, IntPtr n);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetSecurityDescriptorOwner(IntPtr d, out IntPtr o, out bool b);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetSecurityDescriptorDacl(IntPtr d, out bool p, out IntPtr a, out bool b);
    [DllImport("advapi32.dll")] static extern uint SetSecurityInfo(SafeFileHandle h, uint t, uint i, IntPtr o, IntPtr g, IntPtr d, IntPtr s);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("wtsapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool WTSQuerySessionInformation(IntPtr s, int id, int c, out IntPtr b, out int n);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
    public static string SessionValue(int id, int kind) {
        IntPtr b; int n;
        if (!WTSQuerySessionInformation(IntPtr.Zero, id, kind, out b, out n)) throw new Win32Exception();
        try { return Marshal.PtrToStringUni(b); } finally { WTSFreeMemory(b); }
    }
    static void Enable(string name) {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception();
        try {
            long luid; if (!LookupPrivilegeValue(null, name, out luid)) throw new Win32Exception();
            Privilege p = new Privilege { Count=1, Luid=luid, Attributes=2 };
            if (!AdjustTokenPrivileges(token, false, ref p, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error()!=0) throw new Win32Exception();
        } finally { CloseHandle(token); }
    }
    public static void Prepare() { Enable("SeBackupPrivilege"); Enable("SeRestorePrivilege"); }

}
'@
    if ($Action -eq 'SameUser') {
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $name = [InstallerSecurity]::SessionValue($session, 5)
        $domain = [InstallerSecurity]::SessionValue($session, 7)
        if (!$name) { throw 'No interactive session user was found.' }
        $account = New-Object Security.Principal.NTAccount("$domain\$name")
        $sid = $account.Translate([Security.Principal.SecurityIdentifier])
        if ($sid.Value -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
            Write-Output 'Different elevation identity: skip per-user registry work and console-rule migration.'
            exit 2
        }
        exit 0
    }
    [InstallerSecurity]::Prepare()
    Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'secure-files.cs')))
    $preserved = [SecureFiles]::ReplaceMachineData($DataDirectory)
    if ($preserved) { Write-Output "Previous Corewright data preserved at $preserved" }
    Write-Output 'Corewright data migrated into freshly created protected objects.'
    exit 0
} catch {
    Write-Output "Installer security check failed: $($_.Exception.Message)"
    if (('SecureFiles' -as [type]) -and ![SecureFiles]::MachinePathReady) { exit 3 }
    exit 1
}
