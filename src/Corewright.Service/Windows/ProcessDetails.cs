using System.Runtime.InteropServices;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service.Windows;

/// <summary>Which per-process values a client asked for. Each group costs handle opens, so only visible columns are read.</summary>
[Flags]
internal enum DetailGroups { None = 0, Settings = 1, Identity = 2, CommandLine = 4, Gui = 8 }

/// <summary>Values read from a process handle on demand (dashboard open, column visible), cached briefly.</summary>
internal sealed class ProcessDetail
{
    public long SettingsAt;
    public long? Affinity, IoPriority, GpuPriority, MemoryPriority, Efficiency, WorkingSetLimitMb;
    public long GuiAt;
    public int? GdiObjects, UserObjects;
    public bool IdentityLoaded;
    public string? UserName;
    public bool? Elevated;
    public int? Bits;
    public bool CommandLineLoaded;
    public string? CommandLine;
}

/// <summary>
/// Reads per-process values for the process list. Never runs on the engine tick: only API requests trigger it,
/// at most every <see cref="RefreshMs"/> per process, outside the monitor lock. The cache is dropped when no
/// client has asked for a while, so a closed dashboard costs no memory.
/// </summary>
internal sealed unsafe class ProcessDetailsCache
{
    private const long RefreshMs = 3000;
    private const long IdleDropMs = 30_000;
    private readonly Dictionary<ProcessKey, ProcessDetail> _map = [];
    private readonly Lock _lock = new();
    private long _lastUse;

    public void Refresh(IReadOnlyList<ProcessKey> keys, DetailGroups groups)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            _lastUse = now;
            var alive = new HashSet<ProcessKey>(keys);
            foreach (var k in _map.Keys.Where(k => !alive.Contains(k)).ToList()) _map.Remove(k);
            foreach (var k in keys)
                if (!_map.ContainsKey(k)) _map[k] = new ProcessDetail();
        }

        foreach (var key in keys)
        {
            if (key.Pid is 0 or 4) continue;
            ProcessDetail d;
            lock (_lock) d = _map[key];
            bool settings = groups.HasFlag(DetailGroups.Settings) && now - d.SettingsAt > RefreshMs;
            bool gui = groups.HasFlag(DetailGroups.Gui) && now - d.GuiAt > RefreshMs;
            bool identity = groups.HasFlag(DetailGroups.Identity) && !d.IdentityLoaded;
            bool cmd = groups.HasFlag(DetailGroups.CommandLine) && !d.CommandLineLoaded;
            if (!settings && !gui && !identity && !cmd) continue;
            Read(key, d, settings, gui, identity, cmd, now);
        }
    }

    public ProcessDetail? Get(ProcessKey key)
    {
        lock (_lock) return _map.GetValueOrDefault(key);
    }

    /// <summary>Called from the tick: frees the cache once nobody has looked for a while.</summary>
    public void DropIfIdle()
    {
        lock (_lock)
            if (_map.Count > 0 && Environment.TickCount64 - _lastUse > IdleDropMs) _map.Clear();
    }

    private static void Read(ProcessKey key, ProcessDetail d, bool settings, bool gui, bool identity, bool cmd, long now)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == 0) h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == 0)
        {
            d.SettingsAt = d.GuiAt = now;
            d.IdentityLoaded = d.CommandLineLoaded = true;
            return;
        }
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime) return;

            if (settings)
            {
                d.SettingsAt = now;
                d.Affinity = Native.GetProcessAffinityMask(h, out var mask, out _) ? (long)(ulong)mask : null;
                int io;
                d.IoPriority = Native.NtQueryInformationProcess(h, Native.ProcessIoPriority, &io, 4, out _) >= 0 ? Math.Clamp(io, 0, 3) : null;
                d.GpuPriority = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out int gpu) >= 0 ? Math.Clamp(gpu, 0, 5) : null;
                uint mp;
                d.MemoryPriority = Native.GetProcessInformation(h, Native.ProcessMemoryPriority, &mp, 4) ? mp : null;
                var pt = new Native.PROCESS_POWER_THROTTLING_STATE { Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                d.Efficiency = Native.GetProcessInformation(h, Native.ProcessPowerThrottling, &pt, (uint)sizeof(Native.PROCESS_POWER_THROTTLING_STATE))
                    ? (pt.ControlMask & 1) == 0 ? EfficiencyValue.Auto : (pt.StateMask & 1) != 0 ? EfficiencyValue.On : EfficiencyValue.Off
                    : null;
                d.WorkingSetLimitMb = Native.GetProcessWorkingSetSizeEx(h, out _, out var max, out uint flags)
                    ? (flags & Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0 ? (long)(max / (1024 * 1024)) : 0
                    : null;
            }

            if (gui)
            {
                d.GuiAt = now;
                d.GdiObjects = (int)Native.GetGuiResources(h, 0);
                d.UserObjects = (int)Native.GetGuiResources(h, 1);
            }

            if (identity)
            {
                d.IdentityLoaded = true;
                d.Bits = Native.IsWow64Process2(h, out ushort proc, out _) ? (proc == 0 ? 64 : 32) : null;
                if (Native.OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out nint token))
                {
                    try
                    {
                        byte* buf = stackalloc byte[256];
                        if (Native.GetTokenInformation(token, 1 /* TokenUser */, buf, 256, out _))
                            d.UserName = LookupSid(*(void**)buf);
                        int elevated;
                        if (Native.GetTokenInformation(token, 20 /* TokenElevation */, &elevated, 4, out _))
                            d.Elevated = elevated != 0;
                    }
                    finally
                    {
                        Native.CloseHandle(token);
                    }
                }
            }
        }
        finally
        {
            Native.CloseHandle(h);
        }

        if (cmd)
        {
            d.CommandLineLoaded = true;
            d.CommandLine = SystemProbe.ReadCommandLine(key);
        }
    }

    private static string? LookupSid(void* sid)
    {
        char* name = stackalloc char[128];
        char* domain = stackalloc char[128];
        uint nameLen = 128, domainLen = 128;
        return Native.LookupAccountSidW(null, sid, name, ref nameLen, domain, ref domainLen, out _)
            ? new string(name, 0, (int)nameLen)
            : null;
    }
}
