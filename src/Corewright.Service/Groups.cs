using System.Numerics;
using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service;

// ---- API shapes (PM-39) ------------------------------------------------------------------------------------------

public sealed record GroupRootDto(int Pid, long CreationTime);

/// <summary>
/// A process group, registered by another tool. <see cref="Actions"/> uses exactly the rule actions schema; affinity
/// also takes <c>exclusive:N</c>: N whole cores reserved for this group for its whole life.
/// </summary>
public sealed record GroupRequestDto(string? Owner, string? Label, GroupRootDto[]? Roots, bool IncludeDescendants, int? TtlSeconds,
    bool FreezeAutopilot, RuleActions? Actions);

public sealed record GroupAppliedDto(string? Cpus, string? CpuPriority, string? IoPriority, string? MemoryPriority);

public sealed record GroupWarningDto(int Pid, string Process, string Slot, string Error);

public sealed record GroupDto(string Id, string Owner, string Label, string State, bool Frozen, GroupAppliedDto Applied, int Processes,
    int ExpiresInSeconds, GroupWarningDto[] Warnings);

public sealed record GroupErrorDto(string Error, string? Detail);

/// <summary>
/// Process groups (PM-39). A tool says "these exact process trees are mine for now" and, optionally, which settings
/// they get, in the same vocabulary as rules. No tool-specific words (owner, 2026-11-02: "not an island solution for
/// only one use-case"). Grouped processes get the group's settings (priority 100, above user and Autopilot rules);
/// Autopilot lets go of them and user rules apply only if they name the group. Groups expire after their TTL unless
/// renewed, so a crashed tool can never leave processes grouped or Autopilot frozen.
/// </summary>
internal sealed class GroupStore
{
    public const int GroupRulePriority = 100;
    public const string ExclusivePrefix = "exclusive:";

    internal sealed class Group
    {
        public required string Id;
        public required string Owner;
        public required string Label;
        public required ProcessKey[] Roots;
        public bool IncludeDescendants;
        public bool Freeze;
        public long ExpiresMs;
        public RuleActions? Actions;
        /// <summary>Cores reserved by exclusive:N, chosen once for the group's life (0 = none).</summary>
        public ulong Exclusive;
    }

    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private int _resolvedVersion = -1;

    /// <summary>Bumped on every add, delete or expiry; membership is recomputed then.</summary>
    public int Version { get; private set; }

    public bool AnyFreeze { get; private set; }
    /// <summary>Cores held exclusively by groups: Autopilot's placements never use them.</summary>
    public ulong ExclusiveMask { get; private set; }
    public IEnumerable<Group> All => _groups.Values;
    public int Count => _groups.Count;
    public bool TryGet(string id, out Group group) => _groups.TryGetValue(id, out group!);

    public void Put(Group group)
    {
        _groups[group.Id] = group;
        Changed();
    }

    public bool Remove(string id)
    {
        if (!_groups.Remove(id)) return false;
        Changed();
        return true;
    }

    /// <summary>Drops expired groups. True when something expired (the rule set must be rebuilt).</summary>
    public bool Expire(long nowMs)
    {
        if (_groups.Count == 0) return false;
        List<string>? gone = null;
        foreach (var g in _groups.Values)
            if (g.ExpiresMs <= nowMs) (gone ??= []).Add(g.Id);
        if (gone is null) return false;
        foreach (var id in gone) _groups.Remove(id);
        Changed();
        return true;
    }

    private void Changed()
    {
        Version++;
        AnyFreeze = _groups.Values.Any(g => g.Freeze);
        ExclusiveMask = _groups.Values.Aggregate(0UL, (m, g) => m | g.Exclusive);
    }

    /// <summary>
    /// Marks grouped processes. After a change every process is looked at again; otherwise only new ones, which
    /// inherit their parent's group when it includes descendants. No allocation per tick.
    /// </summary>
    public void Resolve(IReadOnlyList<ProcessInfo> processes, Func<int, ProcessInfo?> byPid)
    {
        bool full = _resolvedVersion != Version;
        _resolvedVersion = Version;
        if (_groups.Count == 0)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (full) { p.GroupId = null; p.GroupOwner = null; }
                p.GroupResolved = true;
            }
            return;
        }
        if (full)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                p.GroupId = null;
                p.GroupOwner = null;
                p.GroupResolved = false;
                foreach (var g in _groups.Values)
                    if (Array.IndexOf(g.Roots, p.Key) >= 0) { p.GroupId = g.Id; p.GroupOwner = g.Owner; p.GroupResolved = true; break; }
            }
        }
        // Descendants: a few passes so children listed before their parents are caught too.
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (p.GroupResolved) continue;
                if (p.Parent is { } parentKey && byPid(parentKey.Pid) is { } parent && parent.Key == parentKey)
                {
                    if (!parent.GroupResolved) continue; // decide after the parent
                    if (parent.GroupId is { } id && _groups.TryGetValue(id, out var g) && g.IncludeDescendants)
                    {
                        p.GroupId = id;
                        p.GroupOwner = g.Owner;
                    }
                }
                p.GroupResolved = true;
                changed = true;
            }
            if (!changed) break;
        }
        for (int i = 0; i < processes.Count; i++) processes[i].GroupResolved = true;
    }

    /// <summary>One rule per group with settings: priority 100, matched by group id.</summary>
    public IEnumerable<Rule> Rules()
    {
        foreach (var g in _groups.Values)
        {
            if (g.Actions is not { } a) continue;
            var actions = new RuleActions
            {
                CpuPriority = a.CpuPriority,
                IoPriority = a.IoPriority,
                GpuPriority = a.GpuPriority,
                MemoryPriority = a.MemoryPriority,
                EfficiencyMode = a.EfficiencyMode,
                MemoryLimitMb = a.MemoryLimitMb,
                WorkingSetLimitMb = a.WorkingSetLimitMb,
                NetworkBlocked = a.NetworkBlocked,
                UploadLimitMbps = a.UploadLimitMbps,
                NetworkPriority = a.NetworkPriority,
                Affinity = g.Exclusive != 0 ? AffinityParser.Format(g.Exclusive) : a.Affinity,
            };
            yield return new Rule
            {
                Id = RuleCompiler.GroupRulePrefix + g.Id,
                Origin = RuleOrigin.Group,
                Name = $"Group: {g.Owner} ({g.Label})",
                Priority = GroupRulePriority,
                When = Condition.All(Condition.Leaf("groupId", "eq", g.Id)),
                Actions = actions,
            };
        }
    }

    public static string? Validate(GroupRequestDto r)
    {
        if (string.IsNullOrWhiteSpace(r.Owner) || r.Owner.Length > 64) return "owner is required (at most 64 characters)";
        if (r.Label is { Length: > 128 }) return "label must be at most 128 characters";
        if (r.Roots is not { Length: > 0 } || r.Roots.Length > 64) return "roots must list 1 to 64 processes";
        if (r.TtlSeconds is { } ttl && (ttl < 5 || ttl > 3600)) return "ttlSeconds must be between 5 and 3600";
        if (ExclusiveCount(r.Actions?.Affinity) is { } n && (n < 1 || n > 64)) return "exclusive:N needs 1 to 64 cores";
        return null;
    }

    /// <summary>N for "exclusive:N", else null.</summary>
    public static int? ExclusiveCount(string? affinity) =>
        affinity is { } a && a.StartsWith(ExclusivePrefix, StringComparison.OrdinalIgnoreCase)
            ? (int.TryParse(a.AsSpan(ExclusivePrefix.Length), out int n) ? n : -1)
            : null;

    /// <summary>
    /// exclusive:N: N whole cores, chosen once and the same every time on the same machine: strongest first (CPPC),
    /// outside the CCD Autopilot keeps for games, skipping cores other groups already hold. Null when N do not fit.
    /// </summary>
    public ulong? PickExclusive(int n, SystemTopology topo, int gameCcd, string exceptGroup, ulong isolated, out string? why)
    {
        why = null;
        var cores = topo.CoresByRank.Length > 0 ? topo.CoresByRank : Enumerable.Range(0, topo.LogicalCount).Select(i => 1UL << i).ToArray();
        ulong game = gameCcd >= 0 && gameCcd < topo.CcdMasks.Length && topo.CcdMasks.Length > 1 ? topo.CcdMasks[gameCcd] : 0;
        // Cores a rule isolates (PM-49) are taken as well.
        ulong taken = _groups.Values.Where(g => g.Id != exceptGroup).Aggregate(isolated, (m, g) => m | g.Exclusive);
        var free = cores.Where(c => (c & game) == 0 && (c & taken) == 0 && (c & topo.AllMask) == c).ToList();
        if (free.Count < n)
        {
            why = $"exclusive:{n} needs {n} whole cores; {free.Count} are free outside the game CCD, other groups' cores and isolated cores";
            return null;
        }
        return free.Take(n).Aggregate(0UL, (m, c) => m | c);
    }

    public static int Bits(ulong mask) => BitOperations.PopCount(mask);
}
