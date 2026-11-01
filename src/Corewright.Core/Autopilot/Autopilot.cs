using Corewright.Core.Rules;

namespace Corewright.Core.Autopilot;

/// <summary>App settings (settings.json in the data folder). Rules live separately in rules.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public AutopilotSettings Autopilot { get; set; } = new();
}

/// <summary>
/// Autopilot: automatic scheduling on top of the rule engine. It never acts on its own; it generates low-priority
/// built-in rules (ids "autopilot-…") from these settings, so the user's rules always win and Explain shows why.
/// </summary>
public sealed class AutopilotSettings
{
    /// <summary>Off by default: turning it on changes how every process is scheduled (owner confirms defaults).</summary>
    public bool Enabled { get; set; }

    public GameModeSettings GameMode { get; set; } = new();
    public BalanceSettings Balance { get; set; } = new();
}

public sealed class GameModeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>CPU priority for the detected game.</summary>
    public string GamePriority { get; set; } = "aboveNormal";

    public string GameGpuPriority { get; set; } = "high";

    /// <summary>auto | all | frequency-ccd | vcache-ccd | best-ccd | ccdN. "auto" picks the V-Cache CCD on single V-Cache parts, otherwise the fastest CCD, and all cores on single-CCD CPUs.</summary>
    public string Placement { get; set; } = "auto";

    /// <summary>Move busy background processes off the game's CCD and lower their priority while a game runs.</summary>
    public bool MoveBackground { get; set; } = true;

    public string BackgroundPriority { get; set; } = "belowNormal";

    /// <summary>Always treated as games.</summary>
    public List<string> Games { get; set; } = [];

    /// <summary>Never treated as games, even full-screen with a 3D API (browsers, players, editors).</summary>
    public List<string> NotGames { get; set; } =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "zen.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "vlc.exe", "mpv.exe", "mpc-hc64.exe", "PotPlayerMini64.exe", "explorer.exe", "Code.exe",
        "devenv.exe", "rider64.exe", "Unity.exe", "UnrealEditor.exe", "blender.exe", "obs64.exe",
        "Corewright.Dashboard.exe", "msedgewebview2.exe", "ApplicationFrameHost.exe",
    ];

    /// <summary>Kept responsive while gaming (voice, streaming); never moved or lowered by Autopilot.</summary>
    public List<string> KeepResponsive { get; set; } =
    [
        "Discord.exe", "ts3client_win64.exe", "TeamSpeak.exe", "Mumble.exe", "obs64.exe",
        "audiodg.exe", "steam.exe", "steamwebhelper.exe",
    ];
}

public sealed class BalanceSettings
{
    /// <summary>ProBalance-style restraint of background processes that hog the CPU.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-process CPU, as percent of the whole machine, that counts as hogging.</summary>
    public double CpuPercent { get; set; } = 8;

    public double SustainSeconds { get; set; } = 2;
    public double ReleaseSeconds { get; set; } = 6;
    public string Priority { get; set; } = "belowNormal";
    public List<string> Exclusions { get; set; } = [];
}

/// <summary>Built-in rules from the Autopilot settings. Priorities 1–9: below every user rule.</summary>
public static class AutopilotRules
{
    public const string IdPrefix = "autopilot-";

    public static List<Rule> Build(AutopilotSettings s, SystemTopology topo)
    {
        var rules = new List<Rule>();
        if (!s.Enabled) return rules;
        var keep = s.GameMode.KeepResponsive;

        if (s.GameMode.Enabled)
        {
            var g = s.GameMode;
            int gameCcd = GameCcd(g.Placement, topo);
            string? gameAffinity = g.Placement switch
            {
                "all" => null,
                _ when gameCcd >= 0 => $"ccd{gameCcd}",
                _ => g.Placement is "auto" ? null : g.Placement,
            };

            rules.Add(new Rule
            {
                Id = IdPrefix + "game",
                Name = "Autopilot: game",
                Priority = 9,
                AllowRealtime = false,
                When = Condition.All(Condition.Leaf("isGame", "isTrue", null)),
                Actions = new RuleActions { CpuPriority = g.GamePriority, GpuPriority = g.GameGpuPriority, Affinity = gameAffinity, IoPriority = "normal" },
            });

            if (keep.Count > 0)
                rules.Add(new Rule
                {
                    Id = IdPrefix + "voice",
                    Name = "Autopilot: keep voice and streaming responsive",
                    Priority = 8,
                    When = Condition.All(
                        Condition.Leaf("fact", "isTrue", null, "game.active"),
                        Condition.Leaf("name", "in", string.Join(", ", keep))),
                    Actions = new RuleActions { CpuPriority = "aboveNormal" },
                });

            if (g.MoveBackground)
            {
                // Everything else that is busy moves off the game's CCD (when there is another one) and gets lower priority.
                string? backgroundAffinity = null;
                if (gameCcd >= 0 && topo.CcdMasks.Length > 1)
                {
                    ulong other = 0;
                    for (int i = 0; i < topo.CcdMasks.Length; i++)
                        if (i != gameCcd) other |= topo.CcdMasks[i];
                    if (other != 0) backgroundAffinity = AffinityParser.Format(other & topo.AllMask);
                }
                var when = Condition.All(
                    Condition.Leaf("fact", "isTrue", null, "game.active"),
                    Condition.Leaf("isGame", "isFalse", null),
                    Condition.Leaf("isForeground", "isFalse", null),
                    Condition.Leaf("session", "gt", "0"),
                    Condition.Leaf("cpu", "gt", "0.3"));
                if (keep.Count > 0) when.Children!.Add(Not(Condition.Leaf("name", "in", string.Join(", ", keep))));
                rules.Add(new Rule
                {
                    Id = IdPrefix + "game-background",
                    Name = "Autopilot: background while gaming",
                    Priority = 6,
                    SustainSeconds = 1,
                    ReleaseSeconds = 10,
                    When = when,
                    Actions = new RuleActions { CpuPriority = g.BackgroundPriority, IoPriority = "low", Affinity = backgroundAffinity },
                });
            }
        }

        if (s.Balance.Enabled)
        {
            var b = s.Balance;
            var when = Condition.All(
                Condition.Leaf("isForeground", "isFalse", null),
                Condition.Leaf("isGame", "isFalse", null),
                Condition.Leaf("session", "gt", "0"),
                Condition.Leaf("cpu", "gt", b.CpuPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var excluded = b.Exclusions.Concat(keep).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (excluded.Count > 0) when.Children!.Add(Not(Condition.Leaf("name", "in", string.Join(", ", excluded))));
            rules.Add(new Rule
            {
                Id = IdPrefix + "balance",
                Name = "Autopilot: balance CPU hogs",
                Priority = 4,
                SustainSeconds = b.SustainSeconds,
                ReleaseSeconds = b.ReleaseSeconds,
                When = when,
                Actions = new RuleActions { CpuPriority = b.Priority },
            });
        }

        return rules;
    }

    /// <summary>The CCD a game should run on, or -1 for "no placement".</summary>
    public static int GameCcd(string placement, SystemTopology topo)
    {
        if (topo.CcdMasks.Length < 2) return -1;
        return placement switch
        {
            "auto" => topo.VCacheCcds.Length == 1 ? topo.VCacheCcds[0] : topo.FrequencyCcd,
            "vcache-ccd" => topo.VCacheCcds.Length == 1 ? topo.VCacheCcds[0] : -1,
            "frequency-ccd" or "best-ccd" => topo.FrequencyCcd,
            _ when placement.StartsWith("ccd", StringComparison.OrdinalIgnoreCase) && int.TryParse(placement[3..], out int n) && n < topo.CcdMasks.Length => n,
            _ => -1,
        };
    }

    private static Condition Not(Condition c)
    {
        c.Negate = true;
        return c;
    }
}
