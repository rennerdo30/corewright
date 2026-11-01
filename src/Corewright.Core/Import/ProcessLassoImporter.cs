using System.Globalization;
using System.Text;
using Corewright.Core.Rules;

namespace Corewright.Core.Import;

public sealed record ImportNote(string Kind, string Message);

public sealed record ImportResult(List<Rule> Rules, List<ImportNote> Notes, string? Source);

/// <summary>
/// Converts a Process Lasso configuration (prolasso.ini) into Corewright rules: one rule per process pattern
/// that combines all of its Lasso defaults. Everything Lasso does that Corewright cannot express yet is
/// reported as a note instead of being dropped silently.
/// </summary>
public static class ProcessLassoImporter
{
    public const string IdPrefix = "lasso-";
    private const int ExactNamePriority = 30;
    private const int PatternPriority = 25;
    private const int ForegroundBoostPriority = 20;

    public static IEnumerable<string> DefaultConfigPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessLasso", "config", "prolasso.ini");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcessLasso", "config", "prolasso.ini");
    }

    /// <summary>Reads the file with BOM detection (Lasso writes UTF-16 LE).</summary>
    public static string ReadConfig(string path) => File.ReadAllText(path, Encoding.UTF8);

    public static ImportResult Convert(string iniText, SystemTopology topo, string? source = null)
    {
        var ini = Ini.Parse(iniText);
        var notes = new List<ImportNote>();
        var rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        bool wildcardsToPaths = ini.Bool("ProcessDefaults", "MatchWildcardsToPathnames", true);
        bool matchCommandLine = ini.Bool("ProcessDefaults", "MatchOnCommandLine", false);

        Rule For(string pattern)
        {
            pattern = pattern.Trim();
            if (!rules.TryGetValue(pattern, out var rule))
            {
                bool isPattern = IsPattern(pattern);
                rule = new Rule
                {
                    Id = IdPrefix + Slug(pattern),
                    Name = $"Process Lasso: {pattern}",
                    Enabled = true,
                    Priority = isPattern ? PatternPriority : ExactNamePriority,
                    When = BuildMatch(pattern, wildcardsToPaths, matchCommandLine),
                };
                rules.Add(pattern, rule);
            }
            return rule;
        }

        foreach (var (name, value) in Pairs(ini.Get("ProcessDefaults", "DefaultPriorities")))
        {
            var level = LassoPriority(value);
            if (level is null) notes.Add(new("skipped", $"{name}: unknown priority '{value}'"));
            else if (level == "realtime") { For(name).Actions.CpuPriority = level; For(name).AllowRealtime = true; }
            else For(name).Actions.CpuPriority = level;
        }

        foreach (var (name, value) in Pairs(ini.Get("ProcessDefaults", "DefaultIOPriorities")))
        {
            string? io = value.Trim() switch { "0" => "veryLow", "1" => "low", "2" => "normal", "3" => "high", _ => null };
            if (io is null) notes.Add(new("skipped", $"{name}: unknown I/O priority '{value}'"));
            else For(name).Actions.IoPriority = io;
        }

        foreach (var (name, value) in Pairs(ini.Get("ProcessDefaults", "DefaultGPUPriorities")))
        {
            string? gpu = value.Trim() switch
            {
                "0" => "idle", "1" => "belowNormal", "2" => "normal", "3" => "aboveNormal", "4" => "high", "5" => "realtime", _ => null,
            };
            if (gpu is null) notes.Add(new("skipped", $"{name}: unknown GPU priority '{value}'"));
            else
            {
                For(name).Actions.GpuPriority = gpu;
                if (gpu == "realtime") For(name).AllowRealtime = true;
            }
        }

        foreach (var (name, value) in Pairs(ini.Get("ProcessDefaults", "DefaultMemoryPriorities")))
        {
            string? mem = value.Trim() switch
            {
                "0" or "1" => "veryLow", "2" => "low", "3" => "medium", "4" => "belowNormal", "5" => "normal", _ => null,
            };
            if (mem is null) notes.Add(new("skipped", $"{name}: unknown memory priority '{value}'"));
            else For(name).Actions.MemoryPriority = mem;
        }

        // DefaultAffinitiesEx = name,group,cpus,...
        var aff = Split(ini.Get("ProcessDefaults", "DefaultAffinitiesEx"));
        for (int i = 0; i + 2 < aff.Count; i += 3) ImportAffinity(aff[i], aff[i + 2], "affinity");
        if (aff.Count % 3 != 0) notes.Add(new("skipped", "DefaultAffinitiesEx has an unexpected format; some entries were not read"));

        // CPU Sets are softer than affinity; Corewright maps them to affinity until CPU Sets support lands.
        foreach (var (name, cpus) in Pairs(ini.Get("ProcessDefaults", "CPUSets")))
            ImportAffinity(name, cpus, "CPU set (imported as affinity)");

        foreach (var name in Names(ini.Get("ProcessAllowances", "EfficiencyMode")))
            For(name).Actions.EfficiencyMode = true;

        void ImportAffinity(string name, string cpus, string what)
        {
            if (AffinityParser.TryParse(cpus.Replace(';', ','), topo, out _, out var err))
                For(name).Actions.Affinity = cpus.Replace(';', ',');
            else
                notes.Add(new("skipped", $"{name}: {what} '{cpus}' not imported ({err})"));
        }

        var result = rules.Values.ToList();
        ImportForegroundBoost(ini, result, notes);
        ReportUnsupported(ini, notes);

        foreach (var r in result.Where(r => !r.Actions.HasAny()).ToList())
        {
            result.Remove(r);
            notes.Add(new("skipped", $"{r.Name}: no supported setting"));
        }
        notes.Insert(0, new("info", $"{result.Count} rules from {rules.Count} Process Lasso entries"));
        return new ImportResult(result, notes, source);
    }

    private static void ImportForegroundBoost(Ini ini, List<Rule> rules, List<ImportNote> notes)
    {
        if (!ini.Bool("ForegroundBoosting", "BoostForegroundProcess", false)) return;

        string? cpu = ini.Get("ForegroundBoosting", "ForegroundBoostPriorityClass")?.Trim().ToLowerInvariant() switch
        {
            "0x8000" => "aboveNormal", "0x80" => "high", "0x20" => "normal", _ => null,
        };
        string? gpu = ini.Get("ForegroundBoosting", "ForegroundBoostGPU")?.Trim() switch
        {
            "2" => "normal", "3" => "aboveNormal", "4" => "high", _ => null,
        };
        if (cpu is null && gpu is null) return;

        var when = Condition.All(Condition.Leaf("isForeground", "isTrue", null));
        if (ini.Bool("ForegroundBoosting", "BoostOnlyNormal", true))
            notes.Add(new("info", "Foreground boost: Lasso boosts only normal-priority processes; Corewright's rule priority order handles overlaps instead"));
        var exclusions = Names(ini.Get("ForegroundBoosting", "ForegroundBoostExclusions")).ToList();
        if (exclusions.Count > 0)
        {
            var excluded = Condition.Leaf("name", "in", string.Join(", ", exclusions));
            excluded.Negate = true;
            when.Children!.Add(excluded);
        }

        rules.Add(new Rule
        {
            Id = IdPrefix + "foreground-boost",
            Name = "Process Lasso: foreground boost",
            Enabled = true,
            Priority = ForegroundBoostPriority,
            When = when,
            Actions = new RuleActions { CpuPriority = cpu, GpuPriority = gpu },
        });
    }

    private static void ReportUnsupported(Ini ini, List<ImportNote> notes)
    {
        if (ini.Bool("OutOfControlProcessRestraint", "OocOn", false))
            notes.Add(new("unsupported", "ProBalance (dynamic restraint) is on in Process Lasso; Corewright has no equivalent yet. You can model it with a rule on CPU % plus 'Apply after'."));
        if (ini.Bool("GamingMode", "GamingModeEnabled", false))
            notes.Add(new("unsupported", "Gaming mode: coming with Corewright's game mode (PM-2)."));
        if (ini.Bool("MemoryManagement", "SmartTrimIsEnabled", false))
            notes.Add(new("unsupported", "SmartTrim: not imported; use a soft RAM cap per process instead."));
        if (ini.Bool("PowerSaver", "EnergySaverEnabled", false) || ini.Bool("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherEnabled", false))
            notes.Add(new("unsupported", "Power plan switching: not supported yet."));

        foreach (var (section, key, label) in new[]
        {
            ("ProcessAllowances", "DisallowedProcesses", "Disallowed processes"),
            ("ProcessAllowances", "InstanceLimitedProcesses", "Instance limits"),
            ("ProcessAllowances", "InstanceManagedCPUAffinities", "Instance-managed affinities"),
            ("ProcessAllowances", "ProcessThrottles", "Process throttles"),
            ("ProcessAllowances", "CPULimitRules", "CPU limiter"),
            ("ProcessAllowances", "StartupBoostRules", "Startup boost"),
            ("ProcessDefaults", "ThreadPriorityBoosts", "Thread priority boosts"),
            ("ProcessDefaults", "KeepRunningProcessesEx", "Keep running"),
            ("ProcessDefaults", "DefaultGPUAdapterPreferences", "GPU adapter preferences"),
            ("AdvancedRules", "WatchdogRules2", "Watchdog rules"),
            ("AdvancedRules", "ProcessorGroupExtended", "Processor group extender"),
        })
        {
            if (!string.IsNullOrWhiteSpace(ini.Get(section, key)))
                notes.Add(new("unsupported", $"{label}: present in Process Lasso but not supported yet."));
        }
    }

    private static Condition BuildMatch(string pattern, bool wildcardsToPaths, bool matchCommandLine)
    {
        if (!IsPattern(pattern)) return Condition.All(Condition.Leaf("name", "eq", pattern));

        var any = new List<Condition> { Condition.Leaf("name", "glob", pattern) };
        if (wildcardsToPaths) any.Add(Condition.Leaf("path", "glob", pattern));
        if (matchCommandLine) any.Add(Condition.Leaf("commandLine", "glob", pattern));
        return any.Count == 1 ? Condition.All(any[0]) : Condition.Any([.. any]);
    }

    private static bool IsPattern(string s) => s.IndexOfAny(['*', '?', '/', '\\']) >= 0;

    private static string? LassoPriority(string v) => v.Replace(" ", "").ToLowerInvariant() switch
    {
        "idle" or "low" => "idle",
        "belownormal" => "belowNormal",
        "normal" => "normal",
        "abovenormal" => "aboveNormal",
        "high" => "high",
        "realtime" => "realtime",
        _ => null,
    };

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [.. value.Split(',').Select(s => s.Trim())];

    private static IEnumerable<(string Name, string Value)> Pairs(string? value)
    {
        var parts = Split(value);
        for (int i = 0; i + 1 < parts.Count; i += 2)
            if (parts[i].Length > 0) yield return (parts[i], parts[i + 1]);
    }

    /// <summary>A name list that may carry 0/1 flags after names ("a.exe,1,b.exe,0"); names with a 0 flag are left out.</summary>
    private static IEnumerable<string> Names(string? value)
    {
        var parts = Split(value);
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Length == 0 || parts[i] is "0" or "1") continue;
            bool off = i + 1 < parts.Count && parts[i + 1] == "0";
            if (!off) yield return parts[i];
        }
    }

    private static string Slug(string pattern)
    {
        var sb = new StringBuilder();
        foreach (char ch in pattern.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        if (s.Length > 48) s = s[..40] + "-" + ((uint)pattern.GetHashCode(StringComparison.OrdinalIgnoreCase)).ToString("x", CultureInfo.InvariantCulture);
        return s.Length == 0 ? "rule" : s;
    }

    private sealed class Ini
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Ini Parse(string text)
        {
            var ini = new Ini();
            string section = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[' && line[^1] == ']') { section = line[1..^1]; continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                // Lasso repeats sections; later keys add to the same section.
                ini._values[section + "\u0001" + line[..eq].Trim()] = line[(eq + 1)..];
            }
            return ini;
        }

        public string? Get(string section, string key) => _values.TryGetValue(section + "\u0001" + key, out var v) ? v : null;

        public bool Bool(string section, string key, bool fallback) =>
            Get(section, key)?.Trim().ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => fallback };
    }
}

internal static class RuleActionsExtensions
{
    public static bool HasAny(this RuleActions a) =>
        a.CpuPriority is not null || a.Affinity is not null || a.IoPriority is not null || a.GpuPriority is not null ||
        a.MemoryPriority is not null || a.EfficiencyMode is not null || a.MemoryLimitMb is not null || a.WorkingSetLimitMb is not null;
}
