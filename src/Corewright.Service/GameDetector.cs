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

    private static readonly string[] GraphicsModules = ["d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll"];

    /// <summary>
    /// Full screen alone is weak evidence: screenshot tools (ShareX region capture), overlays and launchers cover the
    /// screen with Direct3D for a few seconds. Such a process must stay full screen in front this long to count.
    /// </summary>
    private const long FullscreenSustainMs = 10_000;

    private readonly Dictionary<ProcessKey, (string Name, string Reason)> _games = [];
    private readonly List<ProcessKey> _gone = [];
    private readonly HashSet<string> _always = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _never = new(StringComparer.OrdinalIgnoreCase);
    private GameModeSettings? _settings;
    private int _lastForeground;
    private bool _lastFullscreen;
    private ProcessKey? _candidate;
    private long _candidateSinceMs;
    private string? _candidateReason;

    public ProcessKey? Game { get; private set; }
    public string? GameName { get; private set; }
    public string? Reason { get; private set; }
    public void InvalidateForeground() => _lastForeground = -1;

    /// <summary>Returns true when the detected game changed.</summary>
    public bool Update(ProcessSampler sampler, int foregroundPid, bool fullscreen, GameModeSettings settings, bool enabled, IEngineEventSink sink)
    {
        bool changed = false;
        if (!ReferenceEquals(_settings, settings))
        {
            _settings = settings; _lastForeground = 0; _candidate = null;
            _always.Clear(); _never.Clear();
            foreach (string name in settings.Games) _always.Add(name);
            foreach (string name in settings.NotGames) _never.Add(name);
        }
        foreach (var (key, game) in _games)
            if (!sampler.IsAlive(key) || _never.Contains(game.Name) || (!enabled && !_always.Contains(game.Name))) _gone.Add(key);
        foreach (var key in _gone)
        {
            var old = _games[key]; _games.Remove(key); changed = true;
            sink.Emit(new(EngineEventKind.Info, key.Pid, old.Name, "Autopilot", null, null, "game mode ended (root exited or excluded)"));
        }
        _gone.Clear();
        foreach (var listed in sampler.Processes)
            if (!listed.IsProtected && _always.Contains(listed.Name) && !_never.Contains(listed.Name) && !_games.ContainsKey(listed.Key))
                changed |= Detect(listed, "on your games list", sink);
        if (Game is not { } current || !_games.ContainsKey(current))
        {
            Game = null; GameName = null; Reason = null;
            foreach (var (key, game) in _games) { Game = key; GameName = game.Name; Reason = game.Reason; }
        }
        UpdateMembers(sampler);
        if (!enabled) { _candidate = null; _lastForeground = 0; return changed; }

        if (foregroundPid == _lastForeground && fullscreen == _lastFullscreen)
        {
            // Unchanged foreground: only a pending full-screen candidate needs the clock (no module scan, no handle).
            if (_candidate is { } c && Environment.TickCount64 - _candidateSinceMs >= FullscreenSustainMs && sampler.TryGet(foregroundPid, out var cp) && cp.Key == c && !_never.Contains(cp.Name))
            {
                _candidate = null;
                changed |= Detect(cp, _candidateReason!, sink); UpdateMembers(sampler); return changed;
            }
            return changed;
        }
        if (!sampler.TryGet(foregroundPid, out var p)) return changed;
        _lastForeground = foregroundPid;
        _lastFullscreen = fullscreen;
        _candidate = null;
        if (p.IsProtected || p.SessionId == 0) return changed;
        if (Game is { } detected && detected == p.Key) return changed;

        string? reason = Classify(p, fullscreen, settings, out bool weak);
        if (reason is null) return changed;
        if (weak)
        {
            _candidate = p.Key;
            _candidateSinceMs = Environment.TickCount64;
            _candidateReason = reason;
            return changed;
        }
        changed |= Detect(p, reason, sink); UpdateMembers(sampler); return changed;
    }

    private bool Detect(ProcessInfo p, string reason, IEngineEventSink sink)
    {

        if (_games.ContainsKey(p.Key)) return false;
        _games[p.Key] = (p.Name, reason);
        Game = p.Key;
        GameName = p.Name;
        Reason = reason;
        sink.Emit(new EngineEvent(EngineEventKind.Info, p.Key.Pid, p.Name, "Autopilot", null, null, $"game detected ({reason})"));
        return true;
    }

    private void UpdateMembers(ProcessSampler sampler)
    {
        foreach (var p in sampler.Processes)
        {
            if (p.GameRoot is { } prior && !_games.ContainsKey(prior)) p.GameRoot = null;
            if (_games.ContainsKey(p.Key)) p.GameRoot = p.Key;
            p.IsGame = p.GameRoot is not null;
            var ancestor = p.Parent;
            for (int depth = 0; !p.IsGame && ancestor is { } key && depth < 32; depth++)
            {
                if (_games.ContainsKey(key)) { p.IsGame = true; p.GameRoot = key; break; }
                if (!sampler.TryGet(key.Pid, out var parent) || parent.Key != key) break;
                if (parent.GameRoot is { } root && _games.ContainsKey(root)) { p.IsGame = true; p.GameRoot = root; break; }
                ancestor = parent.Parent;
            }
        }
    }

    private string? Classify(ProcessInfo p, bool fullscreen, GameModeSettings s, out bool weak)
    {
        weak = false;
        if (_never.Contains(p.Name)) return null;
        if (_always.Contains(p.Name)) return "on your games list";

        string path = p.Path?.ToLowerInvariant() ?? "";
        string? graphics = UsesGraphicsApi(p.Key.Pid);
        foreach (var marker in LibraryMarkers)
            if (path.Contains(marker, StringComparison.Ordinal) && graphics is not null && graphics != "module access denied")
                return $"installed in a game library ({marker.Trim('\\')}) with {graphics}";

        if (fullscreen && graphics is { } api)
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
        if (h == 0) return Marshal.GetLastPInvokeError() == 5 ? "module access denied" : null;
        try
        {
            var modules = new nint[1024];
            uint needed;
            fixed (nint* m = modules)
                if (!K32EnumProcessModulesEx(h, m, (uint)(modules.Length * sizeof(nint)), out needed, 0x03))
                    return Marshal.GetLastPInvokeError() == 5 ? "module access denied" : null;
            int count = Math.Min(modules.Length, (int)(needed / (uint)sizeof(nint)));
            char* name = stackalloc char[260];
            for (int i = 0; i < count; i++)
            {
                uint len = K32GetModuleBaseNameW(h, modules[i], name, 260);
                if (len == 0) continue;
                var module = new ReadOnlySpan<char>(name, (int)len);
                foreach (var g in GraphicsModules)
                    if (module.Equals(g, StringComparison.OrdinalIgnoreCase))
                        return g switch
                        {
                            "vulkan-1.dll" => "Vulkan", "d3d12.dll" => "Direct3D 12", "d3d11.dll" => "Direct3D 11", "d3d10.dll" => "Direct3D 10",
                            "dxgi.dll" => "DXGI", "opengl32.dll" => "OpenGL", _ => "Direct3D 9",
                        };
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
