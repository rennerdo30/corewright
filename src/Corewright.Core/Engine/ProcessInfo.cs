namespace Corewright.Core.Engine;

/// <summary>A process instance. Windows reuses PIDs, so identity includes the creation time.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime);

/// <summary>
/// Live facts about one process. The sampler reuses one instance for the whole process lifetime
/// and updates the dynamic fields in place, so a tick allocates nothing for known processes.
/// </summary>
public sealed class ProcessInfo(ProcessKey key, string name)
{
    public ProcessKey Key { get; } = key;
    public string Name { get; } = name;

    // Static for the process lifetime.
    public ProcessKey? Parent;
    public string ParentName = "";
    public string? Path;
    public int SessionId;
    public bool IsProtected;

    /// <summary>
    /// Reads a process command line; set by the host. Command lines are long and only static rules use them,
    /// and those are evaluated once per process, so the value is read on demand and never stored (memory constraint).
    /// </summary>
    public static Func<ProcessKey, string?>? CommandLineReader { get; set; }

    public string CommandLine => CommandLineReader?.Invoke(Key) ?? "";

    // Dynamic, refreshed every tick.
    public double CpuPercent;
    public long WorkingSet;
    public long PrivateBytes;
    public double IoBytesPerSec;
    public int Threads;
    public int Handles;
    public int BasePriority;

    // Task Manager "Details" values; they come with the same system call, so they cost nothing extra.
    public long PrivateWorkingSet;
    public long PeakWorkingSet;
    public long PeakPrivateBytes;
    public long VirtualSize;
    public long PeakVirtualSize;
    public long PagedPool;
    public long NonPagedPool;
    public long UserTime;    // 100 ns units
    public long KernelTime;  // 100 ns units
    public long CycleTime;
    public uint PageFaults;
    public uint HardFaults;
    public long ReadOperations, WriteOperations, OtherOperations;
    public long ReadBytes, WriteBytes, OtherBytes;

    // Sampler bookkeeping.
    public long LastCpuTime;
    public long LastIoBytes;
    public long LastSeenTick;
}

/// <summary>Per-evaluation context shared by all processes of one tick.</summary>
public sealed class EvalContext
{
    private static readonly Dictionary<string, string> NoFacts = [];

    public IReadOnlyDictionary<string, string> Facts { get; set; } = NoFacts;
    public int ForegroundPid { get; set; }

    /// <summary>The detected game (Autopilot game mode); 0 when none. Every process with the same exe name counts.</summary>
    public int GamePid { get; set; }
    public string? GameName { get; set; }

    /// <summary>Monotonic milliseconds (Environment.TickCount64).</summary>
    public long NowMs { get; set; }
}

public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "LsaIso.exe", "dwm.exe", "fontdrvhost.exe",
    };

    public static bool Is(int pid, string name, int selfPid) =>
        pid is 0 or 4 || pid == selfPid || Names.Contains(name);
}
