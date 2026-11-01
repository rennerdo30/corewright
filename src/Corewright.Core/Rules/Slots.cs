namespace Corewright.Core.Rules;

/// <summary>An independently owned per-process setting. Every slot value is encoded as a long.</summary>
public enum Slot
{
    CpuPriority,
    Affinity,
    IoPriority,
    GpuPriority,
    MemoryPriority,
    EfficiencyMode,
    MemoryLimit,
    WorkingSetLimit,
}

public enum CpuPriorityLevel { Idle, BelowNormal, Normal, AboveNormal, High, Realtime }

public enum IoPriorityLevel { VeryLow, Low, Normal, High }

public enum GpuPriorityLevel { Idle, BelowNormal, Normal, AboveNormal, High, Realtime }

/// <summary>Values match MEMORY_PRIORITY_* constants.</summary>
public enum MemoryPriorityLevel { VeryLow = 1, Low = 2, Medium = 3, BelowNormal = 4, Normal = 5 }

/// <summary>Slot value encoding for <see cref="Slot.EfficiencyMode"/>.</summary>
public static class EfficiencyValue
{
    public const long Auto = 0;
    public const long On = 1;
    public const long Off = 2;
}

public static class Slots
{
    public const int Count = 8;
    public const int AllMask = (1 << Count) - 1;

    public static string Name(Slot slot) => slot switch
    {
        Slot.CpuPriority => "cpuPriority",
        Slot.Affinity => "affinity",
        Slot.IoPriority => "ioPriority",
        Slot.GpuPriority => "gpuPriority",
        Slot.MemoryPriority => "memoryPriority",
        Slot.EfficiencyMode => "efficiencyMode",
        Slot.MemoryLimit => "memoryLimitMb",
        Slot.WorkingSetLimit => "workingSetLimitMb",
        _ => slot.ToString(),
    };

    /// <summary>Windows passes only idle and below-normal priority classes on to child processes.</summary>
    public static bool IsInheritedPriority(long level) =>
        (CpuPriorityLevel)level is CpuPriorityLevel.Idle or CpuPriorityLevel.BelowNormal;

    public static string FormatValue(Slot slot, long value) => slot switch
    {
        Slot.CpuPriority => Camel(((CpuPriorityLevel)value).ToString()),
        Slot.Affinity => AffinityParser.Format((ulong)value),
        Slot.IoPriority => Camel(((IoPriorityLevel)value).ToString()),
        Slot.GpuPriority => Camel(((GpuPriorityLevel)value).ToString()),
        Slot.MemoryPriority => Camel(((MemoryPriorityLevel)value).ToString()),
        Slot.EfficiencyMode => value switch { EfficiencyValue.On => "on", EfficiencyValue.Off => "off", _ => "auto" },
        Slot.MemoryLimit or Slot.WorkingSetLimit => value == 0 ? "none" : $"{value} MB",
        _ => value.ToString(),
    };

    /// <summary>The KPRIORITY base priority Windows reports for each priority class.</summary>
    public static int BasePriorityOf(CpuPriorityLevel level) => level switch
    {
        CpuPriorityLevel.Idle => 4,
        CpuPriorityLevel.BelowNormal => 6,
        CpuPriorityLevel.Normal => 8,
        CpuPriorityLevel.AboveNormal => 10,
        CpuPriorityLevel.High => 13,
        CpuPriorityLevel.Realtime => 24,
        _ => 8,
    };

    public static string FormatBasePriority(int basePriority) => basePriority switch
    {
        0 => "",
        <= 4 => "idle",
        <= 6 => "belowNormal",
        <= 8 => "normal",
        <= 10 => "aboveNormal",
        < 16 => "high",
        _ => "realtime",
    };

    internal static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    internal static bool TryParseLevel<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || char.IsDigit(text[0]) || text[0] == '-') return false;
        return Enum.TryParse(text.Trim(), ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}
