namespace Corewright.Core.Rules;

public enum RuleOrigin { Autopilot, User, Group }

/// <summary>The persisted rule file (rules.json).</summary>
public sealed class RuleSet
{
    public int Version { get; set; } = 1;
    public List<Rule> Rules { get; set; } = [];
}

public sealed class Rule
{
    [System.Text.Json.Serialization.JsonIgnore]
    public RuleOrigin Origin { get; set; } = RuleOrigin.User;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Higher wins. Each slot goes to the highest-priority matching rule that sets it.</summary>
    public int Priority { get; set; }

    /// <summary>The condition must hold this long before the rule becomes active.</summary>
    public double SustainSeconds { get; set; }

    /// <summary>The condition must be false this long before an active rule releases its slots.</summary>
    public double ReleaseSeconds { get; set; }

    /// <summary>Required for realtime CPU or GPU priority; realtime can starve input and audio.</summary>
    public bool AllowRealtime { get; set; }

    public Condition When { get; set; } = Condition.Always();
    public RuleActions Actions { get; set; } = new();
}

/// <summary>
/// A node of the condition tree. <c>type = "group"</c> uses Combinator/Negate/Children;
/// <c>type = "cond"</c> uses Field/Key/Op/Value. An empty group matches everything.
/// </summary>
public sealed class Condition
{
    public string Type { get; set; } = "group";
    public string? Combinator { get; set; }
    public bool Negate { get; set; }
    public List<Condition>? Children { get; set; }
    public string? Field { get; set; }
    public string? Key { get; set; }
    public string? Op { get; set; }
    public string? Value { get; set; }

    public static Condition Always() => new() { Type = "group", Combinator = "and", Children = [] };

    public static Condition Leaf(string field, string op, string? value, string? key = null) =>
        new() { Type = "cond", Field = field, Op = op, Value = value, Key = key };

    public static Condition All(params Condition[] children) =>
        new() { Type = "group", Combinator = "and", Children = [.. children] };

    public static Condition Any(params Condition[] children) =>
        new() { Type = "group", Combinator = "or", Children = [.. children] };
}

/// <summary>One optional value per slot; null leaves the slot to lower rules.</summary>
public sealed class RuleActions
{
    public string? CpuPriority { get; set; }
    public bool CpuPriorityLowerOnly { get; set; }
    /// <summary>For the generic learned rule, single verdicts claim affinity only.</summary>
    public bool BackgroundMultiOnly { get; set; }
    public string? Affinity { get; set; }
    public string? IoPriority { get; set; }
    public string? GpuPriority { get; set; }
    public string? MemoryPriority { get; set; }
    public bool? EfficiencyMode { get; set; }
    public long? MemoryLimitMb { get; set; }
    public long? WorkingSetLimitMb { get; set; }
    public bool? NetworkBlocked { get; set; }
    public double? UploadLimitMbps { get; set; }
    public int? NetworkPriority { get; set; }
}
