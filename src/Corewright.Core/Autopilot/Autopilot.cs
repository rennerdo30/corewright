using Corewright.Core.Rules;

namespace Corewright.Core.Autopilot;

/// <summary>App settings (settings.json in the data folder). Rules live separately in rules.json.</summary>
public sealed class AppSettings
{
    /// <summary>2: background placement on by default. 3: screen tools added to "never games" (owner, 2026-11-02).</summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
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

    public static List<Rule> Build(AutopilotSettings s, SystemTopology topo, IReadOnlyList<BackgroundVerdict>? background = null)
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
                When = Condition.All(Condition.Leaf("isGame", "isTrue", null), Condition.Leaf("isLauncher", "isFalse", null)),
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
                    Actions = new RuleActions { CpuPriority = g.BackgroundPriority, IoPriority = "low", Affinity = backgroundAffinity is null ? null : "auto-background" },
                });
            }
        }

        if (s.Background.Enabled && background is not null)
        {
            // One rule per classified program; game mode (higher priority) still wins for its slots while gaming.
            foreach (var v in background)
            {
                // Short-lived tools (grep, cut, a script's helpers) are not worth an affinity write each.
                var when = Condition.All(Condition.Leaf("program", "eq", v.Program), Condition.Leaf("ageSeconds", "gt", "15"),
                    Condition.Leaf("sessionPresent", "isTrue", null),
                    Condition.Leaf("isLauncher", "isFalse", null), Condition.Leaf("isGame", "isFalse", null));
                // A program only counts as background while no window of its app is in front. Services have none.
                if (!v.Service) when.Children!.Add(Condition.Leaf("isForegroundApp", "isFalse", null));
                rules.Add(new Rule
                {
                    Id = IdPrefix + "background-" + Slug(v.Display) + "-" + Fnv(v.Program),
                    Name = $"Autopilot: background {v.Display}",
                    Priority = 5,
                    When = when,
                    // Placement is dynamic and sticky (AffinityPlacer): sized to the program's real demand, never on the
                    // strongest cores, never on the game's CCD while a game runs.
                    Actions = v.Kind == "single"
                        ? new RuleActions { Affinity = "auto-background" }
                        : new RuleActions { CpuPriority = s.Background.Priority, IoPriority = "low", Affinity = "auto-background" },
                });
            }
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
