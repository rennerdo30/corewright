using Corewright.Core.Rules;

namespace Corewright.Core.Autopilot;

/// <summary>App settings (settings.json in the data folder). Rules live separately in rules.json.</summary>
public sealed class AppSettings
{
    /// <summary>2: background placement on by default. 3: screen tools added to "never games" (owner, 2026-11-02). 6: remote desktop viewers added to "never games" (owner, 2026-11-04).</summary>
    public const int CurrentVersion = 6;

    public int Version { get; set; } = CurrentVersion;
    public AutopilotSettings Autopilot { get; set; } = new();
    private UsageSettings _usage = new();
    /// <summary>Clients written before beta.12 leave it out of PUT /settings; the stored value is kept then (see <see cref="UsageProvided"/>).</summary>
    public UsageSettings Usage { get => _usage; set { _usage = value; UsageProvided = true; } }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsageProvided { get; private set; }
    public string[] Warnings { get; set; } = [];
}

/// <summary>
/// Usage history (PM-45, owner 2026-11-05): hourly CPU time, I/O and memory per program, kept on disk so a program that
/// eats the PC all day (an antivirus scan, an updater) shows up. The files never grow past <see cref="MaxStorageMb"/>;
/// the oldest hours are dropped first.
/// </summary>
public sealed class UsageSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxStorageMb { get; set; } = 64;
}

/// <summary>
/// Autopilot: automatic scheduling on top of the rule engine. It never acts on its own; it generates low-priority
/// built-in rules (ids "autopilot-...") from these settings, so the user's rules always win and Explain shows why.
/// </summary>
public sealed class AutopilotSettings
{
    /// <summary>Off by default: turning it on changes how every process is scheduled (owner confirms defaults).</summary>
    private bool _legacyEnabled;
    public bool Enabled { get => Mode == "on"; set => _legacyEnabled = value; }
    public string Mode { get; set; } = "observe";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LegacyEnabled => _legacyEnabled;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Active => Mode is "observe" or "on";
    public int PlacementLimit { get; set; } = 40;

    /// <summary>
    /// PM-49 (owner, 2026-11-12: "keep core 0 clean so that the Windows system stays responsive"): while Autopilot is on,
    /// every process except the protected system processes and the Windows shell moves off physical core 0, which keeps
    /// it for interrupts, DPCs, input, audio and the desktop. User rules that set affinity win. Off by default.
    /// </summary>
    public bool KeepCoreZeroFree { get; set; }

    public GameModeSettings GameMode { get; set; } = new();
    public BalanceSettings Balance { get; set; } = new();
    public BackgroundSettings Background { get; set; } = new();
    public BuildToolsSettings BuildTools { get; set; } = new();

    public List<string> Launchers { get; set; } =
    [
        "cmd.exe", "pwsh.exe", "powershell.exe", "bash.exe", "sh.exe", "conhost.exe", "OpenConsole.exe",
        "WindowsTerminal.exe", "explorer.exe", "claude.exe", "codex.exe", "node.exe", "Code.exe", "devenv.exe",
        "rider64.exe", "idea64.exe", "cargo.exe", "MSBuild.exe", "dotnet.exe", "ninja.exe", "cmake.exe", "make.exe",
        "gradle.exe", "java.exe", "Unity.exe", "UnityHub.exe", "steam.exe", "EpicGamesLauncher.exe", "T3 Code (Nightly).exe",
    ];
}

/// <summary>
/// Compilers, linkers and build drivers: high load but not latency critical (owner, 2026-11-02), so they run below
/// normal and with low I/O priority. Efficiency mode is deliberately not used: it would slow builds down a lot.
/// </summary>
public sealed class BuildToolsSettings
{
    public bool Enabled { get; set; } = true;
    public string Priority { get; set; } = "belowNormal";
    public string IoPriority { get; set; } = "low";

    public List<string> Programs { get; set; } =
    [
        // MSVC and Windows SDK
        "cl.exe", "link.exe", "lib.exe", "ml64.exe", "rc.exe", "mt.exe", "midl.exe", "cvtres.exe", "MSBuild.exe", "MSBuildTaskHost.exe",
        // .NET
        "csc.exe", "vbc.exe", "VBCSCompiler.exe", "ilc.exe", "crossgen2.exe", "fsc.exe",
        // LLVM / GCC
        "clang.exe", "clang++.exe", "clang-cl.exe", "lld-link.exe", "ld.lld.exe", "lld.exe", "gcc.exe", "g++.exe", "cc1.exe", "cc1plus.exe", "ld.exe", "as.exe",
        // Rust, Go, Zig, Swift, Java/Kotlin
        "rustc.exe", "cargo.exe", "clippy-driver.exe", "go.exe", "compile.exe", "zig.exe", "swiftc.exe", "swift-frontend.exe", "javac.exe", "kotlinc.exe",
        // Build drivers
        "ninja.exe", "make.exe", "mingw32-make.exe", "cmake.exe", "jom.exe", "bazel.exe", "buck2.exe",
        // GPU and shader compilers
        "nvcc.exe", "cicc.exe", "ptxas.exe", "dxc.exe", "fxc.exe", "glslc.exe", "glslangValidator.exe",
        "UnityShaderCompiler.exe", "bee_backend.exe", "ShaderCompileWorker.exe", "UnrealBuildTool.exe",
        // Web toolchains
        "esbuild.exe", "tsgo.exe",
    ];
}
/// <summary>
/// Long-term background classification (PM-12): processes observed for a long time that are (almost) never in the
/// foreground are placed away from the strongest cores. On by default (owner, 2026-11-02); careful by design: services
/// excluded, classification needs an hour of observation, and every decision is visible and reversible.
/// </summary>
public sealed class BackgroundSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Cumulative observed runtime before a process can be classified.</summary>
    public double MinObservedMinutes { get; set; } = 60;

    /// <summary>
    /// Services are judged like programs (owner, 2026-11-02: "generally saying services are left alone is wrong");
    /// latency-critical ones (audio, input, drivers, network core) are always left alone.
    /// </summary>
    public bool IncludeServices { get; set; } = true;

    /// <summary>Above this share of its runtime in the foreground it is interactive, never background.</summary>
    public double MaxForegroundPercent { get; set; } = 1;

    /// <summary>Average load up to this many cores counts as single-threaded: it gets one physical core, filled from the back.</summary>
    public double SingleCoreLoad { get; set; } = 1.2;

    /// <summary>Strongest cores (CPPC ranking) that background placement never uses.</summary>
    public int KeepBestCoresFree { get; set; } = 4;

    /// <summary>Priority for classified multi-threaded background processes.</summary>
    public string Priority { get; set; } = "belowNormal";

    public List<string> Exclusions { get; set; } = [];
}

/// <summary>A classification decision, produced by the service's observer and turned into rules here.</summary>
/// <param name="Program">Identity key (rule field "program"); <param name="Display">what the user sees.</param></param>
public sealed record BackgroundVerdict(string Program, string Display, string Kind, bool Service, string Reason);

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
        .. ScreenTools,
        .. RemoteViewers,
        .. GameLaunchers,
    ];

    public static readonly string[] GameLaunchers =
    [
        "Battle.net.exe", "RiotClientServices.exe", "RiotClientUx.exe", "EADesktop.exe", "EABackgroundService.exe",
        "EpicGamesLauncher.exe", "GalaxyClient.exe", "upc.exe", "UbisoftConnect.exe", "steam.exe", "steamwebhelper.exe",
    ];

    /// <summary>
    /// Screenshot, overlay and shell tools that cover the whole screen with Direct3D for a moment (owner report,
    /// 2026-11-02: ShareX region capture was detected as a game). Added to existing settings by the version 3 migration.
    /// </summary>
    public static readonly string[] ScreenTools =
    [
        "ShareX.exe", "Greenshot.exe", "SnippingTool.exe", "ScreenClippingHost.exe", "Lightshot.exe", "flameshot.exe",
        "ScreenToGif.exe", "Snagit32.exe", "SnagitCapture.exe", "SnagitEditor.exe", "ZoomIt.exe", "ZoomIt64.exe", "Magnify.exe",
        "PowerToys.ColorPickerUI.exe", "PowerToys.MeasureToolUI.exe", "PowerToys.PowerOCR.exe", "PowerToys.PowerLauncher.exe",
        "PowerToys.CropAndLock.exe", "LockApp.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
        "TextInputHost.exe", "GameBar.exe", "XboxGameBarWidgets.exe", "NVIDIA Overlay.exe", "RadeonSoftware.exe",
    ];

    /// <summary>
    /// Remote desktop and virtual machine viewers draw a full-screen remote session with Direct3D (owner report,
    /// 2026-11-04: mstsc.exe was detected as a game). Added to existing settings by the version 6 migration.
    /// </summary>
    public static readonly string[] RemoteViewers =
    [
        "mstsc.exe", "msrdc.exe", "msrdcw.exe", "Windows365.exe", "vmconnect.exe", "VirtualBoxVM.exe", "vmware.exe",
        "vmware-vmx.exe", "vmplayer.exe", "AnyDesk.exe", "TeamViewer.exe", "rustdesk.exe", "vncviewer.exe", "tvnviewer.exe",
        "RemoteDesktopManager.exe", "mRemoteNG.exe", "RDCMan.exe",
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

    /// <summary>Core-relative contribution, independent of the machine's logical processor count.</summary>
    public double CpuCores { get; set; } = 0.7;

    /// <summary>Settings before version 5 stored a share of the whole machine; read once by the migration, never written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? CpuPercent { get => null; set => LegacyCpuPercent = value; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double? LegacyCpuPercent { get; private set; }

    public double SustainSeconds { get; set; } = 2;
    public double ReleaseSeconds { get; set; } = 30;
    public string Priority { get; set; } = "belowNormal";
    public List<string> Exclusions { get; set; } = [];
}

/// <summary>Built-in rules from the Autopilot settings. Priorities 1-9: below every user rule.</summary>
public static class AutopilotRules
{
    public const string IdPrefix = "autopilot-";
    public const string GameRuleId = IdPrefix + "game";
    public const string GameBuildsRuleId = IdPrefix + "game-builds";

    public static List<Rule> Build(AutopilotSettings s, SystemTopology topo, IReadOnlyList<BackgroundVerdict>? background = null)
    {
        var rules = new List<Rule>();
        if (!s.Active) return rules;
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
            if (gameAffinity is not null && !AffinityParser.TryParse(gameAffinity, topo, out _, out _)) gameAffinity = null;

            rules.Add(new Rule
            {
                Id = GameRuleId,
                Name = "Autopilot: game",
                Priority = 9,
                AllowRealtime = false,
                // No launcher guard here: nearly every game starts helpers (anti-cheat, crash handler, overlay), and the
                // game tree is this rule's target, not a bystander (review M2). Its children belong to the game tree.
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
                    Condition.Leaf("sessionPresent", "isTrue", null),
                    Condition.Leaf("fact", "isTrue", null, "game.active"),
                    Condition.Leaf("isGame", "isFalse", null),
                    Condition.Leaf("isLauncher", "isFalse", null),
                    // The whole app in front is left alone, and services are included except latency-critical ones (owner, 2026-11-02).
                    Condition.Leaf("isForegroundApp", "isFalse", null),
                    Condition.Leaf("latencyCritical", "isFalse", null),
                    Condition.Leaf("ageSeconds", "gt", "15"),
                    Condition.Leaf("cpuCores", "gte", "0.5"));
                if (keep.Count > 0) when.Children!.Add(Not(Condition.Leaf("name", "in", string.Join(", ", keep))));
                rules.Add(new Rule
                {
                    Id = IdPrefix + "game-background",
                    Name = "Autopilot: background while gaming",
                    Priority = 6,
                    SustainSeconds = 5,
                    ReleaseSeconds = 15,
                    When = when,
                    Actions = new RuleActions { CpuPriority = g.BackgroundPriority, IoPriority = "low", Affinity = backgroundAffinity is null ? null : "non-game-ccd" },
                });

                // Builds stay off the game's CCD while a game runs (owner, 2026-11-08). No load threshold, age or launcher
                // guard: compilers are short-lived and known to be heavy, and a placed cargo/ninja hands the mask to the
                // compilers it starts. Not foreground-dependent: a build driver's children make it a launcher.
                if (backgroundAffinity is not null && s.BuildTools.Enabled && s.BuildTools.Programs.Count > 0)
                    rules.Add(new Rule
                    {
                        Id = GameBuildsRuleId,
                        Name = "Autopilot: builds while gaming",
                        Priority = 7,
                        When = Condition.All(
                            Condition.Leaf("fact", "isTrue", null, "game.active"),
                            Condition.Leaf("isGame", "isFalse", null),
                            Condition.Leaf("name", "in", string.Join(", ", s.BuildTools.Programs))),
                        Actions = new RuleActions { Affinity = "non-game-ccd" },
                    });
            }
        }

        if (s.Background.Enabled)
        {
            var when = Condition.All(Condition.Leaf("backgroundVerdict", "in", "single,multi"),
                Condition.Leaf("ageSeconds", "gt", "15"), Condition.Leaf("sessionPresent", "isTrue", null),
                Condition.Leaf("isLauncher", "isFalse", null), Condition.Leaf("isGame", "isFalse", null),
                Condition.Any(Condition.Leaf("session", "eq", "0"), Condition.Leaf("isForegroundApp", "isFalse", null)));
            if (!s.Background.IncludeServices) when.Children!.Add(Condition.Leaf("session", "ne", "0"));
            rules.Add(new Rule
            {
                Id = IdPrefix + "background-learned", Name = "Autopilot: learned background program", Priority = 5,
                When = when,
                Actions = new RuleActions { Affinity = "auto-background", CpuPriority = s.Background.Priority, IoPriority = "low", BackgroundMultiOnly = true },
            });
        }

        if (s.BuildTools.Enabled && s.BuildTools.Programs.Count > 0)
        {
            rules.Add(new Rule
            {
                Id = IdPrefix + "build-tools",
                Name = "Autopilot: compilers and build tools",
                Priority = 7,
                When = Condition.All(Condition.Leaf("name", "in", string.Join(", ", s.BuildTools.Programs))),
                Actions = new RuleActions { CpuPriority = s.BuildTools.Priority, IoPriority = s.BuildTools.IoPriority },
            });
        }

        if (s.Balance.Enabled)
        {
            var b = s.Balance;
            var when = Condition.All(
                Condition.Leaf("sessionPresent", "isTrue", null),
                Condition.Leaf("isLauncher", "isFalse", null),
                Condition.Leaf("isForegroundApp", "isFalse", null),
                Condition.Leaf("isGame", "isFalse", null),
                Condition.Leaf("latencyCritical", "isFalse", null),
                Condition.Leaf("systemPressure", "isTrue", null),
                Condition.Leaf("cpuCores", "gte", b.CpuCores.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
                Actions = new RuleActions { CpuPriority = b.Priority, CpuPriorityLowerOnly = true },
            });
        }

        foreach (var rule in rules)
        {
            rule.Origin = RuleOrigin.Autopilot;
            // Outside games, build tools keep every core: no learned or balancing rule gives them an affinity.
            if (rule.Actions.Affinity is not null && rule.Id != GameBuildsRuleId && s.BuildTools.Programs.Count > 0)
                rule.When = Condition.All(rule.When, Not(Condition.Leaf("name", "in", string.Join(", ", s.BuildTools.Programs))));
        }
        return rules;
    }

    /// <summary>The CCD a game should run on, or -1 for "no placement".</summary>
    public static int GameCcd(string placement, SystemTopology topo)
    {
        if (topo.CcdMasks.Length < 2) return -1;
        return placement switch
        {
            "auto" => topo.VCacheCcds.Length == 1 ? topo.VCacheCcds[0] : Fastest(topo),
            "vcache-ccd" => FastestVCache(topo),
            "frequency-ccd" or "best-ccd" => topo.FrequencyCcd,
            _ when placement.StartsWith("ccd", StringComparison.OrdinalIgnoreCase) && int.TryParse(placement[3..], out int n) && n < topo.CcdMasks.Length => n,
            _ => -1,
        };
    }

    private static int Fastest(SystemTopology topo) => topo.FrequencyCcd >= 0 && topo.FrequencyCcd < topo.CcdMasks.Length
        ? topo.FrequencyCcd : FastestVCache(topo);

    private static int FastestVCache(SystemTopology topo)
    {
        foreach (ulong core in topo.CoresByRank)
            foreach (int ccd in topo.VCacheCcds)
                if (ccd >= 0 && ccd < topo.CcdMasks.Length && (topo.CcdMasks[ccd] & core) != 0) return ccd;
        foreach (int ccd in topo.VCacheCcds) if (ccd >= 0 && ccd < topo.CcdMasks.Length) return ccd;
        return 0;
    }

    public static List<RuleError> ValidateSettings(AppSettings settings, SystemTopology topo)
    {
        var errors = new List<RuleError>();
        void Range(string field, double value, double min, double max)
        { if (!double.IsFinite(value) || value < min || value > max) errors.Add(new("settings", field, $"{field} must be between {min} and {max}")); }
        if (settings.Usage is null) { errors.Add(new("settings", "usage", "usage must not be null")); return errors; }
        Range("usage.maxStorageMb", settings.Usage.MaxStorageMb, 8, 1024);
        var a = settings.Autopilot;
        if (a?.Background is null || a.Balance is null || a.GameMode is null || a.BuildTools is null)
        { errors.Add(new("settings", "autopilot", "Autopilot sections must not be null")); return errors; }
        if (a.Mode is not ("off" or "observe" or "on")) errors.Add(new("settings", "mode", "mode must be off, observe or on"));
        Range("placementLimit", a.PlacementLimit, 1, 1000);
        if (a.Launchers is null || a.Background.Exclusions is null || a.Balance.Exclusions is null || a.BuildTools.Programs is null ||
            a.GameMode.Games is null || a.GameMode.NotGames is null || a.GameMode.KeepResponsive is null || a.GameMode.Placement is null)
        { errors.Add(new("settings", "autopilot", "Autopilot lists and game placement must not be null")); return errors; }
        Range("background.minObservedMinutes", a.Background.MinObservedMinutes, 1, 10080);
        Range("background.maxForegroundPercent", a.Background.MaxForegroundPercent, 0, 100);
        Range("background.singleCoreLoad", a.Background.SingleCoreLoad, 0.1, 64);
        Range("background.keepBestCoresFree", a.Background.KeepBestCoresFree, 0, topo.PhysicalCores.Length > 0 ? topo.PhysicalCores.Length : topo.LogicalCount);
        Range("balance.cpuCores", a.Balance.CpuCores, 0.1, 64);
        Range("balance.sustainSeconds", a.Balance.SustainSeconds, 0, 3600);
        Range("balance.releaseSeconds", a.Balance.ReleaseSeconds, 0, 3600);
        settings.Warnings = a.GameMode.Placement is "auto" or "all" || AffinityParser.TryParse(a.GameMode.Placement, topo, out _, out _)
            ? [] : ["Game placement is unavailable on this CPU; game priority remains active without affinity."];
        return errors;
    }

    /// <summary>Stable short hash (string.GetHashCode is randomized per process), so two programs with one display name get two ids.</summary>
    private static string Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static Condition Not(Condition c)
    {
        c.Negate = true;
        return c;
    }
}
