using System.Runtime.InteropServices;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service.Windows;

/// <summary>
/// Applies slot values with Win32 calls. Every handle is checked against the process creation time,
/// so a reused PID never receives another process's settings. Called only from the engine thread.
/// </summary>
internal sealed unsafe class WindowsActuator : IActuator, IDisposable
{
    private const uint Query = Native.PROCESS_QUERY_LIMITED_INFORMATION;
    private const uint QueryFull = Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION;
    private const uint Set = Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION;
    private const long MB = 1024 * 1024;

    // Job objects for memory caps; kept open while the process lives so the limit can be changed or removed.
    private readonly Dictionary<ProcessKey, nint> _jobs = [];
    public NetworkPolicies Network { get; } = new();

    public bool TryRead(in ProcessKey key, Slot slot, out long value, out string? error)
    {
        value = 0;
        if (NetworkPolicies.IsSlot(slot)) { value = NetworkPolicies.Original(slot); error = null; return true; }
        if (slot == Slot.MemoryLimit)
        {
            // Without our job the process has no cap from us; "0" restores to "no cap".
            error = null;
            return true;
        }

        nint h = Open(key, slot is Slot.IoPriority or Slot.GpuPriority ? QueryFull : Query, out error);
        if (h == 0 && slot is Slot.IoPriority or Slot.GpuPriority) h = Open(key, Query, out error);
        if (h == 0) return false;
        try
        {
            switch (slot)
            {
                case Slot.CpuPriority:
                {
                    uint pc = Native.GetPriorityClass(h);
                    if (pc == 0) return Win32Fail(out error);
                    value = (long)FromPriorityClass(pc);
                    return true;
                }
                case Slot.Affinity:
                {
                    if (!Native.GetProcessAffinityMask(h, out var mask, out _)) return Win32Fail(out error);
                    value = (long)(ulong)mask;
                    return true;
                }
                case Slot.IoPriority:
                {
                    int io;
                    int st = Native.NtQueryInformationProcess(h, Native.ProcessIoPriority, &io, 4, out _);
                    if (st < 0) { value = (long)IoPriorityLevel.Normal; return true; }
                    value = Math.Clamp(io, 0, 3);
                    return true;
                }
                case Slot.GpuPriority:
                {
                    int st = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out int gpu);
                    value = st < 0 ? (long)GpuPriorityLevel.Normal : Math.Clamp(gpu, 0, 5);
                    return true;
                }
                case Slot.MemoryPriority:
                {
                    uint mp;
                    if (!Native.GetProcessInformation(h, Native.ProcessMemoryPriority, &mp, 4)) return Win32Fail(out error);
                    value = mp;
                    return true;
                }
                case Slot.EfficiencyMode:
                {
                    var s = new Native.PROCESS_POWER_THROTTLING_STATE { Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                    if (!Native.GetProcessInformation(h, Native.ProcessPowerThrottling, &s, (uint)sizeof(Native.PROCESS_POWER_THROTTLING_STATE)) ||
                        (s.ControlMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) == 0)
                    {
                        value = EfficiencyValue.Auto;
                        return true;
                    }
                    value = (s.StateMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 ? EfficiencyValue.On : EfficiencyValue.Off;
                    return true;
                }
                case Slot.WorkingSetLimit:
                {
                    if (!Native.GetProcessWorkingSetSizeEx(h, out _, out var max, out uint flags)) return Win32Fail(out error);
                    value = (flags & Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0 ? (long)(max / MB) : 0;
                    return true;
                }
            }
            error = "unsupported slot";
            return false;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public bool TryWrite(in ProcessKey key, Slot slot, long value, out string? error)
    {
        if (NetworkPolicies.IsSlot(slot))
        {
            if (value == NetworkPolicies.Original(slot) && Network.Has(key)) return Network.Write(key, null, slot, value, out error);
            if (!Environment.IsPrivilegedProcess && value != NetworkPolicies.Original(slot))
                return Network.Write(key, null, slot, value, out error);
            nint process = Open(key, Query, out error);
            if (process == 0) return false;
            try
            {
                char* path = stackalloc char[32768]; uint length = 32768;
                if (!Native.QueryFullProcessImageNameW(process, 0, path, ref length)) return Win32Fail(out error);
                return Network.Write(key, new string(path, 0, (int)length), slot, value, out error);
            }
            finally { Native.CloseHandle(process); }
        }
        uint access = slot switch
        {
            Slot.MemoryLimit => Native.PROCESS_SET_QUOTA | Native.PROCESS_TERMINATE | Query,
            Slot.WorkingSetLimit => Native.PROCESS_SET_QUOTA | Query,
            _ => Set,
        };
        nint h = Open(key, access, out error);
        if (h == 0) return false;
        try
        {
            switch (slot)
            {
                case Slot.CpuPriority:
                    return Native.SetPriorityClass(h, ToPriorityClass((CpuPriorityLevel)value)) || Win32Fail(out error);

                case Slot.Affinity:
                    return Native.SetProcessAffinityMask(h, (nuint)(ulong)value) || Win32Fail(out error);

                case Slot.IoPriority:
                {
                    int io = (int)value;
                    int st = Native.NtSetInformationProcess(h, Native.ProcessIoPriority, &io, 4);
                    return st >= 0 || NtFail(st, out error);
                }
                case Slot.GpuPriority:
                {
                    int st = Native.D3DKMTSetProcessSchedulingPriorityClass(h, (int)value);
                    return st >= 0 || NtFail(st, out error);
                }
                case Slot.MemoryPriority:
                {
                    uint mp = (uint)value;
                    return Native.SetProcessInformation(h, Native.ProcessMemoryPriority, &mp, 4) || Win32Fail(out error);
                }
                case Slot.EfficiencyMode:
                {
                    var s = new Native.PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = value == EfficiencyValue.Auto ? 0 : Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = value == EfficiencyValue.On ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                    };
                    return Native.SetProcessInformation(h, Native.ProcessPowerThrottling, &s, (uint)sizeof(Native.PROCESS_POWER_THROTTLING_STATE))
                           || Win32Fail(out error);
                }
                case Slot.MemoryLimit:
                    return WriteMemoryLimit(key, h, value, out error);

                case Slot.WorkingSetLimit:
                {
                    if (!Native.GetProcessWorkingSetSizeEx(h, out var min, out var max, out _)) return Win32Fail(out error);
                    if (value <= 0)
                        return Native.SetProcessWorkingSetSizeEx(h, min, max,
                            Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE) || Win32Fail(out error);
                    nuint newMax = (nuint)(value * MB);
                    nuint newMin = Math.Min(min, newMax / 2);
                    return Native.SetProcessWorkingSetSizeEx(h, newMin, newMax,
                        Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE) || Win32Fail(out error);
                }
            }
            error = "unsupported slot";
            return false;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private bool WriteMemoryLimit(in ProcessKey key, nint process, long valueMb, out string? error)
    {
        error = null;
        if (!_jobs.TryGetValue(key, out nint job))
        {
            if (valueMb <= 0) return true;
            job = Native.CreateJobObjectW(0, null);
            if (job == 0) return Win32Fail(out error);
            if (!Native.AssignProcessToJobObject(job, process))
            {
                Win32Fail(out error);
                Native.CloseHandle(job);
                return false;
            }
            _jobs[key] = job;
        }

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        if (valueMb > 0)
        {
            info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = (nuint)(valueMb * MB);
        }
        return Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, &info,
                   (uint)sizeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) || Win32Fail(out error);
    }

    public void Forget(in ProcessKey key)
    {
        Network.Forget(key);
        if (_jobs.Remove(key, out nint job)) Native.CloseHandle(job);
    }

    public void Dispose()
    {
        Network.Dispose();
        foreach (var job in _jobs.Values) Native.CloseHandle(job);
        _jobs.Clear();
    }

    private static nint Open(in ProcessKey key, uint access, out string? error)
    {
        nint h = Native.OpenProcess(access, false, key.Pid);
        if (h == 0)
        {
            error = Describe(Marshal.GetLastPInvokeError());
            return 0;
        }
        if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime)
        {
            Native.CloseHandle(h);
            error = "process exited (pid reused)";
            return 0;
        }
        error = null;
        return h;
    }

    private static bool Win32Fail(out string? error)
    {
        error = Describe(Marshal.GetLastPInvokeError());
        return false;
    }

    private static bool NtFail(int status, out string? error)
    {
        error = status switch
        {
            unchecked((int)0xC0000022) => "access denied",
            // D3DKMT returns this for processes that never opened a GPU device.
            unchecked((int)0xC000000D) => "not applicable (the process has no GPU context)",
            _ => $"NTSTATUS 0x{status:X8}",
        };
        return false;
    }

    private static string Describe(int code) => code switch
    {
        5 => "access denied",
        87 => "invalid parameter",
        1314 => "a required privilege is not held",
        _ => Marshal.GetPInvokeErrorMessage(code),
    };

    private static uint ToPriorityClass(CpuPriorityLevel level) => level switch
    {
        CpuPriorityLevel.Idle => 0x40,
        CpuPriorityLevel.BelowNormal => 0x4000,
        CpuPriorityLevel.Normal => 0x20,
        CpuPriorityLevel.AboveNormal => 0x8000,
        CpuPriorityLevel.High => 0x80,
        CpuPriorityLevel.Realtime => 0x100,
        _ => 0x20,
    };

    private static CpuPriorityLevel FromPriorityClass(uint pc) => pc switch
    {
        0x40 => CpuPriorityLevel.Idle,
        0x4000 => CpuPriorityLevel.BelowNormal,
        0x8000 => CpuPriorityLevel.AboveNormal,
        0x80 => CpuPriorityLevel.High,
        0x100 => CpuPriorityLevel.Realtime,
        _ => CpuPriorityLevel.Normal,
    };
}
