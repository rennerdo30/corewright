using System.Runtime.InteropServices;
using Corewright.Core.Autopilot;
using Corewright.Core.Engine;
using Corewright.Service.Windows;

namespace Corewright.Service;

/// <summary>
/// Decides which foreground process is a game. Runs only when the foreground process or its full-screen state
/// changes, so it costs nothing while you play. A detected game stays the game until its process exits, so
/// alt-tabbing out keeps game mode on. Signals, strongest first: the user's games list, a launcher library path
/// (Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot, Battle.net), full screen together with a 3D API (Direct3D 9/11/12,
/// Vulkan). The "not a game" list always wins.
/// </summary>
internal sealed unsafe partial class GameDetector
{
    private static readonly string[] LibraryMarkers =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\",
        @"\ea games\", @"\electronic arts\", @"\ubisoft game launcher\games\", @"\riot games\", @"\battle.net\",
        @"\blizzard entertainment\", @"\rockstar games\", @"\amazon games\library\", @"\itch\apps\",
    ];

    private static readonly string[] GraphicsModules = ["d3d9.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll"];

    /// <summary>
    /// Full screen alone is weak evidence: screenshot tools (ShareX region capture), overlays and launchers cover the
    /// screen with Direct3D for a few seconds. Such a process must stay full screen in front this long to count.
    /// </summary>
    private const long FullscreenSustainMs = 10_000;

    private int _lastForeground;
    private bool _lastFullscreen;
    private ProcessKey? _candidate;
    private long _candidateSinceMs;
    private string? _candidateReason;

    public ProcessKey? Game { get; private set; }
    public string? GameName { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>Returns true when the detected game changed.</summary>
    public bool Update(ProcessSampler sampler, int foregroundPid, bool fullscreen, GameModeSettings settings, bool enabled, IEngineEventSink sink)
    {
        bool changed = false;
        if (!enabled)
        {
            if (Game is not null) { Clear(sink, "game mode off"); changed = true; }
            _lastForeground = 0;
            return changed;
        }

        if (Game is { } g && !sampler.IsAlive(g))
        {
            Clear(sink, "game exited");
            changed = true;
        }
        else if (Game is not null && settings.NotGames.Contains(GameName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            Clear(sink, "marked as not a game");
            changed = true;
        }

        if (foregroundPid == _lastForeground && fullscreen == _lastFullscreen)
        {
            // Unchanged foreground: only a pending full-screen candidate needs the clock (no module scan, no handle).
            if (_candidate is { } c && Environment.TickCount64 - _candidateSinceMs >= FullscreenSustainMs && sampler.TryGet(foregroundPid, out var cp) && cp.Key == c)
            {
                _candidate = null;
                return Detect(cp, _candidateReason!, sink) || changed;
            }
            return changed;
        }
        _lastForeground = foregroundPid;
        _lastFullscreen = fullscreen;
        _candidate = null;

        if (!sampler.TryGet(foregroundPid, out var p) || p.IsProtected || p.SessionId == 0) return changed;
        if (Game is { } current && current == p.Key) return changed;

        string? reason = Classify(p, fullscreen, settings, out bool weak);
        if (reason is null) return changed;
        if (weak)
        {
            _candidate = p.Key;
            _candidateSinceMs = Environment.TickCount64;
            _candidateReason = reason;
            return changed;
        }
        return Detect(p, reason, sink) || changed;
    }

    private bool Detect(ProcessInfo p, string reason, IEngineEventSink sink)
    {

        Game = p.Key;
        GameName = p.Name;
        Reason = reason;
        sink.Emit(new EngineEvent(EngineEventKind.Info, p.Key.Pid, p.Name, "Autopilot", null, null, $"game detected ({reason})"));
        return true;
    }

    private void Clear(IEngineEventSink sink, string why)
    {
        if (Game is { } g) sink.Emit(new EngineEvent(EngineEventKind.Info, g.Pid, GameName ?? "", "Autopilot", null, null, $"game mode ended ({why})"));
        Game = null;
        GameName = null;
        Reason = null;
    }

    private static string? Classify(ProcessInfo p, bool fullscreen, GameModeSettings s, out bool weak)
    {
        weak = false;
        if (s.NotGames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return null;
        if (s.Games.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return "on your games list";

        string path = p.Path?.ToLowerInvariant() ?? "";
        foreach (var marker in LibraryMarkers)
            if (path.Contains(marker, StringComparison.Ordinal)) return $"installed in a game library ({marker.Trim('\\')})";

        if (fullscreen && UsesGraphicsApi(p.Key.Pid) is { } api)
        {
            weak = true;
            return $"full screen with {api}";
        }
        return null;
    }

    /// <summary>Checks the loaded modules once, on a foreground change only.</summary>
    private static string? UsesGraphicsApi(int pid)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | 0x0010 /* PROCESS_VM_READ */, false, pid);
        if (h == 0) return null;
        try
        {
            var modules = new nint[1024];
            uint needed;
            fixed (nint* m = modules)
                if (!K32EnumProcessModulesEx(h, m, (uint)(modules.Length * sizeof(nint)), out needed, 0x03)) return null;
            int count = Math.Min(modules.Length, (int)(needed / (uint)sizeof(nint)));
            char* name = stackalloc char[260];
            for (int i = 0; i < count; i++)
            {
                uint len = K32GetModuleBaseNameW(h, modules[i], name, 260);
                if (len == 0) continue;
                var module = new ReadOnlySpan<char>(name, (int)len);
                foreach (var g in GraphicsModules)
                    if (module.Equals(g, StringComparison.OrdinalIgnoreCase))
                        return g switch { "vulkan-1.dll" => "Vulkan", "d3d12.dll" => "Direct3D 12", "d3d11.dll" => "Direct3D 11", _ => "Direct3D 9" };
            }
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EnumProcessModulesEx(nint process, nint* modules, uint size, out uint needed, uint filter);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint K32GetModuleBaseNameW(nint process, nint module, char* name, uint size);
}
