using System.Globalization;
using System.Text.RegularExpressions;
using Corewright.Core.Engine;

namespace Corewright.Core.Rules;

public sealed record RuleError(string RuleId, string RuleName, string Message);

public sealed class CompiledRule
{
    internal CompiledRule(Rule source, Func<ProcessInfo, EvalContext, bool> match, bool isDynamic)
    {
        Source = source;
        Fingerprint = source.Origin + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(source, CoreJson.Default.Rule))));
        Match = match;
        IsDynamic = isDynamic;
        SustainMs = (long)(Math.Max(0, source.SustainSeconds) * 1000);
        ReleaseMs = (long)(Math.Max(0, source.ReleaseSeconds) * 1000);
    }

    public Rule Source { get; }
    public string Fingerprint { get; }
    public int StateIndex { get; internal set; } = -1;
    public string Id => Source.Id;
    public string Name => Source.Name;
    public int Priority => Source.Priority;
    public Func<ProcessInfo, EvalContext, bool> Match { get; }

    /// <summary>False when the condition only reads lifetime-static fields; then it is evaluated once per process.</summary>
    public bool IsDynamic { get; }
    public bool UsesNetwork { get; internal set; }

    /// <summary>Rules that apply to grouped processes: a group's own settings and rules that name group or groupId.</summary>
    public bool GroupAware { get; internal set; }

    /// <summary>Built-in Autopilot rule (frozen while a group freezes Autopilot).</summary>
    public bool IsAutopilot => Source.Origin == RuleOrigin.Autopilot;
    public bool ForegroundDependent => IsAutopilot && (Id == "autopilot-balance" || Id == "autopilot-game-background" || Id.StartsWith("autopilot-background-", StringComparison.Ordinal));

    public long SustainMs { get; }
    public long ReleaseMs { get; }
    public long[] Values { get; } = new long[Slots.Count];
    public int SlotMask { get; internal set; }

    public bool Sets(Slot slot) => (SlotMask & (1 << (int)slot)) != 0;
}

public sealed class CompiledRuleSet
{
    public static readonly CompiledRuleSet Empty = new([]);

    internal CompiledRuleSet(CompiledRule[] rules) { Rules = rules; NeedsNetwork = rules.Any(r => r.UsesNetwork); }
    public bool NeedsNetwork { get; }

    /// <summary>Enabled, valid rules ordered by priority (highest first, then file order).</summary>
    public CompiledRule[] Rules { get; }
}

public static class RuleCompiler
{
    private const int MaxDepth = 16;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(20);

    public static readonly string[] StringFields = ["name", "path", "parentName", "commandLine"];
    public static readonly string[] StaticNumberFields = ["pid", "session"];
    public static readonly string[] DynamicNumberFields = ["cpu", "workingSetMb", "privateMb", "ioMBps", "netMBps", "threads", "handles"];

    /// <summary>Compiles all rules. Errors are collected for every rule, enabled or not; only enabled valid rules are returned.</summary>
    public static CompiledRuleSet Compile(RuleSet set, SystemTopology topo, List<RuleError> errors)
    {
        var compiled = new List<(CompiledRule Rule, int Order)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < set.Rules.Count; i++)
        {
            var rule = set.Rules[i];
            var messages = new List<string>();
            if (rule.Origin == RuleOrigin.User && (rule.Id.StartsWith("autopilot-", StringComparison.OrdinalIgnoreCase) || rule.Id.StartsWith("group-", StringComparison.OrdinalIgnoreCase)))
                messages.Add("rule ids beginning with 'autopilot-' or 'group-' are reserved for built-ins");
            if (string.IsNullOrWhiteSpace(rule.Id)) messages.Add("rule id is empty");
            else if (!ids.Add(rule.Id)) messages.Add($"duplicate rule id '{rule.Id}'");

            bool dynamic = false;
            var match = CompileNode(rule.When ?? Condition.Always(), ref dynamic, messages, 0);
            var values = new long[Slots.Count];
            int mask = CompileActions(rule, topo, values, messages);
            if (mask == 0) messages.Add("rule sets no action");

            foreach (var m in messages) errors.Add(new RuleError(rule.Id, rule.Name, m));
            if (messages.Count > 0 || !rule.Enabled || match is null) continue;

            var cr = new CompiledRule(rule, match, dynamic)
            {
                SlotMask = mask,
                UsesNetwork = UsesNetwork(rule.When),
                GroupAware = rule.Origin == RuleOrigin.Group || NamesGroup(rule.When),
            };
            values.CopyTo(cr.Values, 0);
            compiled.Add((cr, i));
        }

        var ordered = compiled
            .OrderByDescending(c => c.Rule.Source.Origin)
            .ThenByDescending(c => c.Rule.Priority)
            .ThenBy(c => c.Order)
            .Select(c => c.Rule)
            .ToArray();
        return new CompiledRuleSet(ordered);
    }
    private static bool UsesNetwork(Condition? c) => c is not null &&
        (c.Field == "netMBps" || (c.Children?.Any(UsesNetwork) ?? false));

    private static int CompileActions(Rule rule, SystemTopology topo, long[] values, List<string> errors)
    {
        var a = rule.Actions ?? new RuleActions();
        int mask = 0;

        void Set(Slot slot, long v) { values[(int)slot] = v; mask |= 1 << (int)slot; }

        if (a.CpuPriority is not null)
        {
            if (!Slots.TryParseLevel<CpuPriorityLevel>(a.CpuPriority, out var v)) errors.Add($"unknown cpuPriority '{a.CpuPriority}'");
            else if (v == CpuPriorityLevel.Realtime && !rule.AllowRealtime) errors.Add("realtime CPU priority needs allowRealtime");
            else Set(Slot.CpuPriority, (long)v);
        }
        if (a.Affinity is not null)
        {
            if (!AffinityParser.TryParse(a.Affinity, topo, out var m, out var err)) errors.Add($"affinity: {err}");
            else Set(Slot.Affinity, (long)m);
        }
        if (a.IoPriority is not null)
        {
            if (!Slots.TryParseLevel<IoPriorityLevel>(a.IoPriority, out var v)) errors.Add($"unknown ioPriority '{a.IoPriority}'");
            else Set(Slot.IoPriority, (long)v);
        }
        if (a.GpuPriority is not null)
        {
            if (!Slots.TryParseLevel<GpuPriorityLevel>(a.GpuPriority, out var v)) errors.Add($"unknown gpuPriority '{a.GpuPriority}'");
            else if (v == GpuPriorityLevel.Realtime && !rule.AllowRealtime) errors.Add("realtime GPU priority needs allowRealtime");
            else Set(Slot.GpuPriority, (long)v);
        }
        if (a.MemoryPriority is not null)
        {
            if (!Slots.TryParseLevel<MemoryPriorityLevel>(a.MemoryPriority, out var v)) errors.Add($"unknown memoryPriority '{a.MemoryPriority}'");
            else Set(Slot.MemoryPriority, (long)v);
        }
        if (a.EfficiencyMode is bool eco) Set(Slot.EfficiencyMode, eco ? EfficiencyValue.On : EfficiencyValue.Off);
        if (a.MemoryLimitMb is long ml)
        {
            if (ml < 16) errors.Add("memoryLimitMb must be at least 16");
            else Set(Slot.MemoryLimit, ml);
        }
        if (a.WorkingSetLimitMb is long ws)
        {
            if (ws < 4) errors.Add("workingSetLimitMb must be at least 4");
            else Set(Slot.WorkingSetLimit, ws);
        }
        if (a.NetworkBlocked is bool block) Set(Slot.NetworkBlocked, block ? 1 : 0);
        if (a.UploadLimitMbps is double mbps)
        {
            if (!double.IsFinite(mbps) || mbps < 0 || mbps >= long.MaxValue / 1_000_000.0 || (mbps > 0 && mbps < 0.000001))
                errors.Add("uploadLimitMbps must be zero or a finite positive rate of at least 0.000001 Mbps");
            else Set(Slot.UploadLimit, (long)(mbps * 1_000_000));
        }
        if (a.NetworkPriority is int dscp)
        {
            if (dscp is < 0 or > 63) errors.Add("networkPriority must be a DSCP number from 0 to 63");
            else Set(Slot.NetworkPriority, dscp);
        }
        return mask;
    }

    private static Func<ProcessInfo, EvalContext, bool>? CompileNode(Condition c, ref bool dynamic, List<string> errors, int depth)
    {
        if (depth > MaxDepth) { errors.Add("condition tree is too deep"); return null; }

        if (string.Equals(c.Type, "group", StringComparison.OrdinalIgnoreCase))
        {
            var kids = new List<Func<ProcessInfo, EvalContext, bool>>();
            foreach (var child in c.Children ?? [])
            {
                var f = CompileNode(child, ref dynamic, errors, depth + 1);
                if (f is null) return null;
                kids.Add(f);
            }
            var arr = kids.ToArray();
            bool or = string.Equals(c.Combinator, "or", StringComparison.OrdinalIgnoreCase);
            if (!or && c.Combinator is not null && !string.Equals(c.Combinator, "and", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"unknown combinator '{c.Combinator}'");
                return null;
            }

            Func<ProcessInfo, EvalContext, bool> group = arr.Length switch
            {
                0 => static (_, _) => true,
                1 => arr[0],
                _ when or => (p, x) => { foreach (var k in arr) if (k(p, x)) return true; return false; },
                _ => (p, x) => { foreach (var k in arr) if (!k(p, x)) return false; return true; },
            };
            if (!c.Negate) return group;
            return (p, x) => !group(p, x);
        }

        if (!string.Equals(c.Type, "cond", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"unknown condition type '{c.Type}'");
            return null;
        }

        var leaf = CompileLeaf(c, ref dynamic, errors);
        if (leaf is null || !c.Negate) return leaf;
        return (p, x) => !leaf(p, x);
    }

    private static Func<ProcessInfo, EvalContext, bool>? CompileLeaf(Condition c, ref bool dynamic, List<string> errors)
    {
        string field = c.Field ?? "";
        string op = c.Op ?? "";

        switch (field)
        {
            case "name": return Str(p => p.Name);
            case "path": return Str(p => p.Path ?? "");
            case "parentName": return Str(p => p.ParentName);
            case "commandLine": return Str(p => p.CommandLine);
            case "pid": return Num(p => p.Key.Pid);
            case "session": return Num(p => p.SessionId);
            case "program": return Str(p => p.Program ?? p.Name);
            case "latencyCritical":
                return op switch
                {
                    "isTrue" => static (p, _) => p.LatencyCritical,
                    "isFalse" => static (p, _) => !p.LatencyCritical,
                    _ => Error($"operator '{op}' does not apply to latencyCritical (use isTrue or isFalse)"),
                };
        }

        dynamic = true;
        switch (field)
        {
            case "backgroundVerdict": return Str(p => p.BackgroundVerdict ?? "observing");
            case "sessionPresent":
                return op switch
                {
                    "isTrue" => static (p, x) => x.ForegroundKnown && (p.SessionId == 0 || p.SessionId == x.ForegroundSessionId),
                    "isFalse" => static (p, x) => !x.ForegroundKnown || (p.SessionId != 0 && p.SessionId != x.ForegroundSessionId),
                    _ => Error("sessionPresent uses isTrue or isFalse"),
                };
            case "isLauncher":
                return op switch
                {
                    "isTrue" => static (p, _) => p.IsLauncher,
                    "isFalse" => static (p, _) => !p.IsLauncher,
                    _ => Error($"operator '{op}' does not apply to isLauncher (use isTrue or isFalse)"),
                };
            case "cpu": return Num(p => p.CpuPercent);
            case "cpuCores":
                var cores = NumberPredicate(op, c.Value, errors);
                return cores is null ? null : (p, x) => cores(p.CpuPercent * x.LogicalCount / 100.0);
            case "systemPressure":
                return op switch
                {
                    "isTrue" => static (_, x) => x.SystemUnderPressure,
                    "isFalse" => static (_, x) => !x.SystemUnderPressure,
                    _ => Error("systemPressure uses isTrue or isFalse"),
                };
            case "ageSeconds": return Num(p => (DateTime.UtcNow.ToFileTimeUtc() - p.Key.CreateTime) / 10_000_000.0);
            case "workingSetMb": return Num(p => p.WorkingSet / 1048576.0);
            case "privateMb": return Num(p => p.PrivateBytes / 1048576.0);
            case "ioMBps": return Num(p => p.IoBytesPerSec / 1048576.0);
            case "netMBps":
                var net = NumberPredicate(op, c.Value, errors);
                return net is null ? null : (p, _) => p.NetAvailable && net((p.NetSend + p.NetReceive) / 1048576.0);
            case "threads": return Num(p => p.Threads);
            case "handles": return Num(p => p.Handles);
            case "isForeground":
                return op switch
                {
                    "isTrue" => static (p, x) => p.Key.Pid == x.ForegroundPid,
                    "isFalse" => static (p, x) => p.Key.Pid != x.ForegroundPid,
                    _ => Error($"operator '{op}' does not apply to isForeground (use isTrue or isFalse)"),
                };
            case "isForegroundApp":
                // The window you use and every process of the same app (Electron helpers, browser tabs, a terminal's children).
                return op switch
                {
                    "isTrue" => static (p, x) => x.IsForegroundApp(p),
                    "isFalse" => static (p, x) => !x.IsForegroundApp(p),
                    _ => Error($"operator '{op}' does not apply to isForegroundApp (use isTrue or isFalse)"),
                };
            case "group": return Str(p => p.GroupOwner ?? "");
            case "groupId": return Str(p => p.GroupId ?? "");
            case "isGame":
                return op switch
                {
                    "isTrue" => static (p, x) => IsGame(p, x),
                    "isFalse" => static (p, x) => !IsGame(p, x),
                    _ => Error($"operator '{op}' does not apply to isGame (use isTrue or isFalse)"),
                };
            case "fact":
                return CompileFact(c, errors);
        }

        errors.Add($"unknown field '{field}'");
        return null;

        Func<ProcessInfo, EvalContext, bool>? Error(string message) { errors.Add(message); return null; }

        Func<ProcessInfo, EvalContext, bool>? Str(Func<ProcessInfo, string> get)
        {
            var pred = StringPredicate(op, c.Value, errors);
            return pred is null ? null : (p, _) => pred(get(p));
        }

        Func<ProcessInfo, EvalContext, bool>? Num(Func<ProcessInfo, double> get)
        {
            var pred = NumberPredicate(op, c.Value, errors);
            return pred is null ? null : (p, _) => pred(get(p));
        }
    }

    private static Func<ProcessInfo, EvalContext, bool>? CompileFact(Condition c, List<string> errors)
    {
        var key = c.Key?.Trim();
        if (string.IsNullOrEmpty(key)) { errors.Add("fact condition needs a key"); return null; }

        switch (c.Op)
        {
            case "exists": return (_, x) => x.Facts.ContainsKey(key);
            case "notExists": return (_, x) => !x.Facts.ContainsKey(key);
            case "isTrue": return (_, x) => x.Facts.TryGetValue(key, out var v) && IsTruthy(v);
            case "isFalse": return (_, x) => !x.Facts.TryGetValue(key, out var v) || !IsTruthy(v);
        }

        if (c.Op is "gt" or "gte" or "lt" or "lte")
        {
            var num = NumberPredicate(c.Op, c.Value, errors);
            if (num is null) return null;
            return (_, x) => x.Facts.TryGetValue(key, out var v) &&
                             double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && num(d);
        }

        var pred = StringPredicate(c.Op ?? "", c.Value, errors);
        if (pred is null) return null;
        return (_, x) => x.Facts.TryGetValue(key, out var v) && pred(v);
    }

    /// <summary>Id prefix of the rule built from a process group's settings.</summary>
    public const string GroupRulePrefix = "group-";

    private static bool NamesGroup(Condition? c) =>
        c is not null && (c.Field is "group" or "groupId" || (c.Children?.Any(NamesGroup) ?? false));

    private static bool IsGame(ProcessInfo p, EvalContext x) => p.IsGame;

    public static bool IsTruthy(string v) =>
        v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
        v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static Func<string, bool>? StringPredicate(string op, string? value, List<string> errors)
    {
        if (value is null) { errors.Add($"operator '{op}' needs a value"); return null; }
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;

        switch (op)
        {
            case "eq": return s => string.Equals(s, value, ic);
            case "ne": return s => !string.Equals(s, value, ic);
            case "contains": return s => s.Contains(value, ic);
            case "startsWith": return s => s.StartsWith(value, ic);
            case "endsWith": return s => s.EndsWith(value, ic);
            case "in":
                var set = new HashSet<string>(
                    value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                return set.Contains;
            case "glob":
                return MakeRegex(GlobToRegex(value), errors);
            case "regex":
                return MakeRegex(value, errors);
        }

        errors.Add($"unknown text operator '{op}'");
        return null;
    }

    /// <summary>"*" any text, "?" one character; "/" and "\" are interchangeable so "C:/Games/*" matches Windows paths.</summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^", glob.Length * 2 + 2);
        foreach (char ch in glob)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                '/' or '\\' => @"[\\/]",
                _ => Regex.Escape(ch.ToString()),
            });
        }
        return sb.Append('$').ToString();
    }

    private static Func<string, bool>? MakeRegex(string pattern, List<string> errors)
    {
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException e)
        {
            errors.Add($"invalid pattern: {e.Message}");
            return null;
        }
        return s =>
        {
            try { return re.IsMatch(s); }
            catch (RegexMatchTimeoutException) { return false; }
        };
    }

    private static Func<double, bool>? NumberPredicate(string op, string? value, List<string> errors)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            errors.Add($"'{value}' is not a number");
            return null;
        }

        switch (op)
        {
            case "eq": return d => d == v;
            case "ne": return d => d != v;
            case "gt": return d => d > v;
            case "gte": return d => d >= v;
            case "lt": return d => d < v;
            case "lte": return d => d <= v;
        }

        errors.Add($"unknown number operator '{op}'");
        return null;
    }
}
