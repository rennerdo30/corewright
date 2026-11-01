using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using Corewright.Core;
using static Corewright.Tray.Win32;

namespace Corewright.Tray;

/// <summary>
/// Tray icon with zero idle cost: no timers, no polling. Foreground changes arrive through SetWinEventHook
/// and are forwarded to the service, which cannot see the desktop from session 0.
/// </summary>
internal static unsafe class Program
{
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_FOREGROUND = WM_APP + 2;
    /// <summary>Posted by the window when its close button is set to exit Corewright.</summary>
    private const uint WM_EXIT_ALL = WM_APP + 3;
    private const int CmdOpen = 1, CmdPause = 2, CmdResume = 3, CmdExit = 9;
    public const string DashboardExitEvent = @"Local\Corewright.Dashboard.Exit";

    private static nint _hwnd;
    private static nint _icon;
    private static uint _taskbarCreated;
    private static int _lastForeground;
    private static bool _lastFullscreen;
    private const nuint RecheckTimer = 1;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    [STAThread]
    private static int Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\Corewright.Tray", out bool created);
        if (!created) return 0;
        // "--background": started by the window itself, which is already open.
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        StartupPreferences.ApplyMachineDefault();

        // Menus follow the system theme (dark, rounded Windows 11 menus).
        try
        {
            SetPreferredAppMode(AppModeAllowDark);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }

        nint instance = GetModuleHandleW(null);
        fixed (char* className = "CorewrightTray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            RegisterClassExW(&wc);
        }
        _hwnd = CreateWindowExW(0, "CorewrightTray", "Corewright", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadTrayIcon();
        AddIcon();

        nint hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        ReportForeground(GetForegroundWindow());
        // Settings > Window: "Open the window when Corewright starts" (off = start minimized in the tray).
        if (!background && WindowPreferences.OpenOnStart) OpenDashboard();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        if (hook != 0) UnhookWinEvent(hook);
        // Tell the service the foreground is unknown from now on, so it does not learn from a stale window.
        Call(HttpMethod.Put, "/api/v1/session/foreground", "{\"pid\":0,\"fullscreen\":false}");
        RemoveIcon();
        return 0;
    }

    /// <summary>The icon file ships next to the exe; loaded at the exact small-icon size for the system DPI.</summary>
    private static nint LoadTrayIcon()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        string path = Path.Combine(AppContext.BaseDirectory, "corewright.ico");
        nint icon = File.Exists(path) ? LoadImageW(0, path, IMAGE_ICON, size, size, LR_LOADFROMFILE) : 0;
        return icon != 0 ? icon : LoadIconW(0, IDI_APPLICATION);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (msg == WM_TRAY)
        {
            uint ev = (uint)(l & 0xFFFF);
            if (ev is WM_LBUTTONDBLCLK or WM_LBUTTONUP) OpenDashboard();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            return 0;
        }
        if (msg == WM_EXIT_ALL)
        {
            ExitAll(QueryStatus() is { Service: false });
            return 0;
        }
        if (msg == WM_FOREGROUND)
        {
            SendForeground((int)w, l != 0);
            return 0;
        }
        if (msg == WM_TIMER && (nuint)w == RecheckTimer)
        {
            // One-shot: borderless games often go full screen a moment after they take focus.
            KillTimer(hwnd, RecheckTimer);
            ReportForeground(GetForegroundWindow(), force: true);
            return 0;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon();
            return 0;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time) =>
        ReportForeground(hwnd);

    private static void ReportForeground(nint hwnd, bool force = false)
    {
        if (hwnd == 0) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        bool fullscreen = IsFullscreen(hwnd);
        bool changedProcess = pid != (uint)_lastForeground;
        if (pid == 0 || (!changedProcess && fullscreen == _lastFullscreen)) return;
        _lastForeground = (int)pid;
        _lastFullscreen = fullscreen;
        PostMessageW(_hwnd, WM_FOREGROUND, (nint)pid, fullscreen ? 1 : 0);
        if (changedProcess && !force) SetTimer(_hwnd, RecheckTimer, 3000, 0);
    }

    /// <summary>Full screen = exclusive D3D, or the window covers its whole monitor (borderless); never the desktop or taskbar.</summary>
    private static bool IsFullscreen(nint hwnd)
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state == 3) return true;
        char* cls = stackalloc char[64];
        int n = GetClassNameW(hwnd, cls, 64);
        var name = new ReadOnlySpan<char>(cls, n);
        if (name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW") || name.SequenceEqual("Shell_TrayWnd")) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MONITORINFO { Size = (uint)sizeof(MONITORINFO) };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), &info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    private static void SendForeground(int pid, bool fullscreen) =>
        _ = Task.Run(() => Call(HttpMethod.Put, "/api/v1/session/foreground", $"{{\"pid\":{pid},\"fullscreen\":{(fullscreen ? "true" : "false")}}}"));

    private static void AddIcon()
    {
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _icon;
        SetTip(ref data, "Corewright");
        Shell_NotifyIconW(NIM_ADD, &data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private static void RemoveIcon()
    {
        var data = NewIconData();
        Shell_NotifyIconW(NIM_DELETE, &data);
    }

    private static NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private static void SetTip(ref NOTIFYICONDATAW data, string text)
    {
        int n = Math.Min(text.Length, 127);
        for (int i = 0; i < n; i++) data.szTip[i] = text[i];
        data.szTip[n] = '\0';
    }

    private static void ShowMenu()
    {
        // Only here does the tray ask the service for state, once per right-click.
        var status = QueryStatus();
        nint menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdOpen, "Open Corewright");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        if (status is null) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, "Service is not running");
        else if (status.Value.Paused) AppendMenuW(menu, MF_STRING, CmdResume, "Resume rules");
        else AppendMenuW(menu, MF_STRING, CmdPause, "Pause rules and revert all");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdExit,
            status is { Service: true } ? "Close (the service keeps applying rules)" : "Exit Corewright");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdOpen: OpenDashboard(); break;
            case CmdPause: _ = Task.Run(() => Call(HttpMethod.Post, "/api/v1/engine/pause", null)); break;
            case CmdResume: _ = Task.Run(() => Call(HttpMethod.Post, "/api/v1/engine/resume", null)); break;
            case CmdExit: ExitAll(status is { Service: false }); break;
        }
    }

    /// <summary>Closes the dashboard, stops a console-mode service (it reverts every change on the way out), then the tray.</summary>
    private static void ExitAll(bool stopConsoleService)
    {
        if (EventWaitHandle.TryOpenExisting(DashboardExitEvent, out var ev))
            using (ev) ev.Set();
        if (stopConsoleService) Call(HttpMethod.Post, "/api/v1/app/shutdown", null);
        DestroyWindow(_hwnd);
    }

    private static void OpenDashboard()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Corewright.Dashboard.exe");
        if (!File.Exists(exe))
        {
            // Development build: src/Corewright.Tray/bin/<cfg>/<tfm>/ → src/Corewright.Dashboard/bin/<cfg>/<tfm>/
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (dir.Parent?.Parent?.Parent?.Parent is { } src)
                exe = Path.Combine(src.FullName, "Corewright.Dashboard", "bin", dir.Parent!.Name, dir.Name, "Corewright.Dashboard.exe");
        }
        if (!File.Exists(exe)) return;
        // The dashboard is single-instance and brings its own window to the front.
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        if (p is not null) AllowSetForegroundWindow(p.Id);
    }

    private static (bool Paused, bool Service)? QueryStatus()
    {
        var body = Call(HttpMethod.Get, "/api/v1/status", null);
        if (body is null) return null;
        // Tiny reader to avoid a JSON model in the tray.
        bool paused = body.Contains("\"paused\":true", StringComparison.Ordinal);
        bool service = body.Contains("\"mode\":\"service\"", StringComparison.Ordinal);
        return (paused, service);
    }

    /// <summary>Re-reads endpoint.json on every call: the service creates a new token at each start.</summary>
    private static string? Call(HttpMethod method, string path, string? json)
    {
        var ep = EndpointInfo.TryReadAny();
        if (ep is null) return null;
        try
        {
            using var req = new HttpRequestMessage(method, ep.Url + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.Token);
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = Http.Send(req);
            if (!res.IsSuccessStatusCode) return null;
            using var reader = new StreamReader(res.Content.ReadAsStream());
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }
}
