namespace Corewright.Core.Engine;

/// <summary>A Windows service running in a process: its key name ("wuauserv") and display name ("Windows Update").</summary>
public sealed record ServiceInfo(string Name, string Display);

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
    /// What this process really is, set once by the Autopilot observer before the engine first sees it: the exe path,
    /// or for generic hosts what they run ("python.exe (train.py)", "svchost.exe (wuauserv)"). Rule field "program".
    /// </summary>
    public string? Program;
    public bool IdentityKnown;
    /// <summary>
    /// Windows services this process runs (PM-46), read with the identity; null for ordinary processes. Most services
    /// have an svchost.exe of their own (Windows 10 1703 and later with enough memory), so a setting on that process
    /// is a setting for that service. Rule field "service".
    /// </summary>
    public ServiceInfo[]? Services;
    /// <summary>Started by services.exe or an svchost.exe: its service list is looked up and kept current.</summary>
    public bool ServiceCandidate;
    /// <summary>Lookups that found no service yet (a service reports its PID shortly after its process starts).</summary>
    public int ServiceChecks;
    /// <summary>Changes whenever <see cref="Services"/> changes, so the engine re-evaluates static rules (field "service").</summary>
    public int ServicesVersion;
    public int IdentityAttempts;
    /// <summary>The topmost ancestor below the shell (explorer, services, …): processes with the same root are one app.</summary>
    public ProcessKey AppRoot;
    public bool AppRootUncertain;
    public bool HasUnknownParent;
    /// <summary>Audio, input, display-driver and network-core processes: Autopilot never slows them down.</summary>
    public bool LatencyCritical;
    public string? BackgroundVerdict;
    public string? BackgroundReason;
    public bool IsLauncher;
    public bool IsGame;
    public ProcessKey? GameRoot;
    public bool HasChildren;
    public long LastChildStartMs;

    /// <summary>
    /// The process group another tool registered this process in (PM-39, PUT /api/v1/groups/{id}): only the group's
    /// own settings and rules that name the group apply to it; Autopilot and other rules let go of it. Null = no group.
    /// </summary>
    public string? GroupId;
    /// <summary>The group's owner, e.g. "unity-tools" (rule field "group").</summary>
    public string? GroupOwner;
    /// <summary>Set once the group store has looked at this process (inheritance from a grouped parent).</summary>
    public bool GroupResolved;

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
    public double NetSend, NetReceive;
    public bool NetAvailable;
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

    /// <summary>CPU time (100 ns) and I/O bytes since the previous sample; the usage history adds these up.</summary>
    public long CpuDelta, IoDelta;
    /// <summary>Usage history bookkeeping: the accumulator this process adds to and the generation it belongs to.</summary>
    public object? UsageSlot;
    public string? UsageKey;
    public int UsageGeneration;

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
    public bool ForegroundBusy { get; set; }
    public bool ForegroundKnown { get; set; }
    public int ForegroundSessionId { get; set; } = -1;
    public int LogicalCount { get; set; } = 1;
    public bool SystemUnderPressure { get; set; }

    /// <summary>App root of the foreground process: every process of that app counts as "in front" (rule field isForegroundApp).</summary>
    public ProcessKey ForegroundRoot { get; set; }
    public string? ForegroundProgram { get; set; }
    public bool ForegroundFrameHost { get; set; }

    public bool IsForegroundApp(ProcessInfo p) => p.Key.Pid == ForegroundPid || p.AppRootUncertain ||
        (ForegroundRoot.Pid != 0 && p.AppRoot == ForegroundRoot) ||
        (ForegroundProgram is not null && p.Program == ForegroundProgram) ||
        (ForegroundFrameHost && (p.Name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ||
            (p.Path?.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) ?? false)));

    /// <summary>The detected game (Autopilot game mode); 0 when none. Every process with the same exe name counts.</summary>
    public int GamePid { get; set; }
    public string? GameName { get; set; }

    /// <summary>
    /// Programs the user marked as games (Autopilot "Always games", right-click "Mark as a game"): they count as a game
    /// whenever they run, for rules (isGame) and Autopilot, not only while detected in front (owner, 2026-11-02).
    /// </summary>
    public IReadOnlySet<string>? AlwaysGames { get; set; }

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
