using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Corewright.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Corewright.Dashboard;

internal static class Program
{
    public const string ShowEvent = @"Local\Corewright.Dashboard.Show";
    public const string ExitEvent = @"Local\Corewright.Dashboard.Exit";

    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, @"Local\Corewright.Dashboard", out bool created);
        if (!created)
        {
            // Already open: bring that window to the front instead.
            if (EventWaitHandle.TryOpenExisting(ShowEvent, out var show))
                using (show) show.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        // The window never runs without the tray (owner, 2026-11-02): start it when it is missing,
        // whichever way the window was opened (Start menu, installer, updater, tray).
        TrayGuard.Ensure();
        Application.Run(new MainForm());
    }
}

/// <summary>
/// Hosts the UI in WebView2 like a modern Windows 11 app: no system caption bar; the page draws its own title area
/// (drag region through WebView2's non-client region support) and the window buttons, which call back into the host.
/// The page is suspended while minimized and the process ends on close, so the dashboard costs nothing when unused.
/// </summary>
internal sealed partial class MainForm : Form
{
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, HTCLIENT = 1, HTTOP = 12;
    private const int SM_CXPADDEDBORDER = 92, SM_CYSIZEFRAME = 33;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly EventWaitHandle _show = new(false, EventResetMode.AutoReset, Program.ShowEvent);
    private readonly EventWaitHandle _exit = new(false, EventResetMode.ManualReset, Program.ExitEvent);
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _exitWait;
    private static readonly string PlacementFile = Path.Combine(AppPaths.UserDataDirectory, "dashboard-window.json");

    public MainForm()
    {
        Text = "Corewright";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "corewright.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        ApplyTheme();
        Controls.Add(_web);
        RestorePlacement();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => BeginInvoke(BringToFront), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => BeginInvoke(Close), null, -1, true);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        HandleCreated += (_, _) =>
        {
            // Recalculate the frame now that WM_NCCALCSIZE removes the caption.
            SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
            UpdateResizeBand();
        };
        Load += async (_, _) => await InitAsync();
        Resize += async (_, _) =>
        {
            // Settings > Window: "Minimize to the tray" hides the window instead of a taskbar button.
            if (WindowState == FormWindowState.Minimized && Visible && WindowPreferences.MinimizeToTray) Hide();
            UpdateResizeBand();
            PostWindowState();
            await OnResizedAsync();
        };
        Activated += (_, _) => PostWindowState();
        Deactivate += (_, _) => PostWindowState();
        FormClosing += (_, _) =>
        {
            SavePlacement();
            // Settings > Window: the close button either leaves Corewright in the tray (default) or exits it.
            // The exit event means the tray itself is closing us, so it needs no message back.
            if (!WindowPreferences.CloseToTray && !_exit.WaitOne(0)) TrayGuard.ExitAll();
        };
        TrayGuard.Watch(this);
        FormClosed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
        };
    }

    private new void BringToFront()
    {
        if (!Visible) Show(); // hidden by "minimize to tray"
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            BeginInvoke(ApplyTheme);
    }

    // ---- Frame without a caption bar -----------------------------------------------------------

    /// <summary>
    /// 1 px strip above the page (invisible) so the very top edge always resizes; where the page passes hit tests
    /// through (its drag region), the top <see cref="ResizeGrip"/> pixels resize as well.
    /// </summary>
    private int ResizeBand => WindowState == FormWindowState.Maximized ? 0 : 1;

    private int ResizeGrip => WindowState == FormWindowState.Maximized ? 0 : Math.Max(5, DeviceDpi * 6 / 96);

    private void UpdateResizeBand()
    {
        int band = ResizeBand;
        if (Padding.Top != band) Padding = new Padding(0, band, 0, 0);
    }

    protected override unsafe void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != 0)
        {
            // Let Windows compute the normal frame, then give the caption area to the client.
            // Left, right and bottom keep their (invisible) resize borders, rounded corners and shadow.
            var rect = (RECT*)m.LParam;
            int top = rect->Top;
            base.WndProc(ref m);
            rect->Top = top;
            if (WindowState == FormWindowState.Maximized)
                rect->Top += GetSystemMetricsForDpi(SM_CYSIZEFRAME, (uint)DeviceDpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)DeviceDpi);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT && WindowState != FormWindowState.Maximized)
        {
            var pt = PointToClient(new Point((short)(m.LParam & 0xFFFF), (short)((m.LParam >> 16) & 0xFFFF)));
            if (pt.Y < ResizeGrip) m.Result = HTTOP;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    // ---- Window size and position --------------------------------------------------------------

    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    /// <summary>First launch: about 70 % × 78 % of the monitor's work area, centred. Later: the last placement if still visible.</summary>
    private void RestorePlacement()
    {
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int scale(int v) => (int)(v * (DeviceDpi / 96.0));
        MinimumSize = new Size(scale(900), scale(600));

        try
        {
            if (File.Exists(PlacementFile) &&
                JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementFile)) is { } p &&
                Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p.X + 40, p.Y, p.Width - 80, 40))))
            {
                Bounds = new Rectangle(p.X, p.Y, Math.Max(p.Width, MinimumSize.Width), Math.Max(p.Height, MinimumSize.Height));
                if (p.Maximized) WindowState = FormWindowState.Maximized;
                return;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }

        int w = Math.Clamp((int)(screen.Width * 0.70), Math.Min(scale(1100), screen.Width), scale(1600));
        int h = Math.Clamp((int)(screen.Height * 0.78), Math.Min(scale(720), screen.Height), scale(1000));
        Bounds = new Rectangle(screen.X + (screen.Width - w) / 2, screen.Y + (screen.Height - h) / 2, w, h);
    }

    private void SavePlacement()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementFile)!);
            File.WriteAllText(PlacementFile, JsonSerializer.Serialize(new Placement(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- Theme -----------------------------------------------------------------------------------

    private static readonly string ThemeFile = Path.Combine(AppPaths.UserDataDirectory, "dashboard-theme.json");

    /// <summary>"system", "light" or "dark" (Settings → Appearance; owner, 2026-11-02).</summary>
    private string _scheme = LoadScheme();

    private static string LoadScheme()
    {
        try
        {
            if (File.Exists(ThemeFile) && JsonDocument.Parse(File.ReadAllText(ThemeFile)).RootElement.TryGetProperty("scheme", out var s)
                && s.GetString() is "light" or "dark" or "system")
                return s.GetString()!;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return "system";
    }

    private void SetScheme(string scheme)
    {
        if (scheme is not ("system" or "light" or "dark")) return;
        _scheme = scheme;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
            File.WriteAllText(ThemeFile, JsonSerializer.Serialize(new Dictionary<string, string> { ["scheme"] = scheme }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        ApplyTheme();
    }

    private static bool IsSystemDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;

    private bool IsDarkTheme() => _scheme switch { "light" => false, "dark" => true, _ => IsSystemDark() };

    private void ApplyTheme()
    {
        bool dark = IsDarkTheme();
        // The page follows the chosen scheme through prefers-color-scheme, so every stylesheet works unchanged.
        if (_web.CoreWebView2 is { } wv)
            wv.Profile.PreferredColorScheme = _scheme switch
            {
                "light" => CoreWebView2PreferredColorScheme.Light,
                "dark" => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        var bg = dark ? Color.FromArgb(0x19, 0x19, 0x19) : Color.FromArgb(0xE9, 0xE9, 0xE9);
        BackColor = bg;
        _web.DefaultBackgroundColor = bg;
        void Frame()
        {
            int on = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE: dark border and system menu
        }
        if (IsHandleCreated) Frame();
        else HandleCreated += (_, _) => Frame();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>The user's Windows accent palette (light to dark shades), so buttons and selection use their colour.</summary>
    private static string[] AccentPalette()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null)
            is not byte[] { Length: >= 28 } p)
            return [];
        var list = new string[7];
        for (int i = 0; i < 7; i++) list[i] = $"#{p[i * 4]:X2}{p[i * 4 + 1]:X2}{p[i * 4 + 2]:X2}";
        return list;
    }

    // ---- WebView2 ----------------------------------------------------------------------------

    private async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.UserDataDirectory, "WebView2"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsNonClientRegionSupportEnabled = true; // CSS app-region: drag becomes the window caption
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#endif
        ApplyTheme(); // the profile exists now: apply the saved light/dark choice before the first paint
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => PostWindowState();
        core.AddWebResourceRequestedFilter($"https://{HostName}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnHostResource;

        // Right after an update or a restart the service needs a few seconds before it writes its endpoint: wait for
        // it instead of declaring it dead (2026-11-02: a window opened after an update stayed on "not running").
        var ep = EndpointInfo.TryReadAny();
        if (ep is null)
        {
            ShowMessage(core, "Connecting to the Corewright service…", "This takes a few seconds after Windows starts or after an update.");
            for (int i = 0; i < 60 && ep is null && !IsDisposed; i++)
            {
                await Task.Delay(1000);
                ep = EndpointInfo.TryReadAny();
            }
        }
        if (ep is null)
        {
            ShowMessage(core, "The Corewright service is not running", NotRunningHelp());
            return;
        }

        var boot = new Dictionary<string, object>
        {
            ["token"] = ep.Token,
            ["accent"] = AccentPalette(),
            ["scheme"] = _scheme,
            ["host"] = "dashboard",
        };
        // Token and theme go to the page in memory only; nothing appears in a URL.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__COREWRIGHT__ = " + JsonSerializer.Serialize(boot) + ";");

        string url = Environment.GetEnvironmentVariable("COREWRIGHT_UI_URL") is { Length: > 0 } dev ? dev : ep.Url + "/";
        core.Navigate(url);
    }

    /// <summary>What to do when no service answers: an installed Corewright has a Windows service, a development copy
    /// is started with start.bat. The last update error is shown when there is one.</summary>
    private static string NotRunningHelp()
    {
        bool installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "install-manifest.txt"));
        string help = installed
            ? "Corewright's Windows service did not start. Restart the PC, or open Services (services.msc), select “Corewright” and choose Start. If that fails, run the Corewright setup again to repair the installation; your settings and rules are kept."
            : "Start it with start.bat, then reopen this window.";
        try
        {
            var file = new FileInfo(Path.Combine(AppPaths.ServiceDataDirectory, "update-last-error.txt"));
            if (file is { Exists: true, Length: < 4096 } && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1))
                help += " The last update reported: " + File.ReadAllText(file.FullName).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return help;
    }

    private void ShowMessage(CoreWebView2 core, string title, string text)
    {
        bool dark = IsDarkTheme();
        string Html(string s) => System.Net.WebUtility.HtmlEncode(s);
        core.NavigateToString(
            $"<html><body style='margin:0;background:{(dark ? "#191919" : "#e9e9e9")};color:{(dark ? "#fff" : "#1a1a1a")};" +
            "font:14px \"Segoe UI Variable Text\",\"Segoe UI\"'>" +
            "<div style='app-region:drag;height:40px;display:flex;justify-content:flex-end'>" +
            "<button onclick=\"chrome.webview.postMessage({type:'close'})\" style='app-region:no-drag;width:46px;border:0;" +
            "background:none;color:inherit;font-family:\"Segoe Fluent Icons\";font-size:10px'>&#xE8BB;</button></div>" +
            $"<div style='padding:24px 40px;max-width:70ch'><h2 style='font-weight:600'>{Html(title)}</h2>" +
            $"<p style='line-height:1.5'>{Html(text)}</p></div></body></html>");
    }

    private void PostWindowPrefs() =>
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "window-prefs",
            ["openOnStart"] = WindowPreferences.OpenOnStart,
            ["closeToTray"] = WindowPreferences.CloseToTray,
            ["minimizeToTray"] = WindowPreferences.MinimizeToTray,
        }));

    private void PostWindowState()
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "window",
            ["maximized"] = WindowState == FormWindowState.Maximized,
            ["active"] = ActiveForm == this,
        }));
    }

    /// <summary>Host actions the page cannot do itself: window buttons, and "reveal" for an existing file.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "maximize":
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case "close":
                    Close();
                    break;
                case "autostart-get":
                    PostAutostart();
                    break;
                case "update-status":
                case "update-check":
                case "update-install":
                case "update-channel":
                    if (Uri.TryCreate(e.Source, UriKind.Absolute, out var source) && source.IsLoopback)
                        _ = OnUpdateMessageAsync(type.GetString()!, root.TryGetProperty("automatic", out var automatic) && automatic.ValueKind == JsonValueKind.True,
                            root.TryGetProperty("channel", out var channel) ? channel.GetString() : null);
                    break;
                case "autostart-set":
                    SetAutostart(root.TryGetProperty("enabled", out var en) && en.GetBoolean());
                    PostAutostart();
                    break;
                case "window-prefs-get":
                    PostWindowPrefs();
                    break;
                case "window-prefs-set":
                    if (root.TryGetProperty("openOnStart", out var oos)) WindowPreferences.OpenOnStart = oos.GetBoolean();
                    if (root.TryGetProperty("closeToTray", out var ctt)) WindowPreferences.CloseToTray = ctt.GetBoolean();
                    if (root.TryGetProperty("minimizeToTray", out var mtt)) WindowPreferences.MinimizeToTray = mtt.GetBoolean();
                    PostWindowPrefs();
                    break;
                case "theme-set":
                    if (root.TryGetProperty("scheme", out var sch) && sch.GetString() is { } scheme) SetScheme(scheme);
                    break;
                case "reveal":
                    if (root.TryGetProperty("path", out var pathEl) && pathEl.GetString() is { } path && File.Exists(path))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
                    break;
            }
        }
        catch (JsonException) { }
    }

    // ---- Icons and friendly names (served by the dashboard, so the service pays nothing) ----------

    private const string HostName = "corewright.host";
    private readonly Dictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// https://corewright.host/icon?path=… returns the exe's icon as PNG; /describe?path=… returns its file description
    /// ("Google Chrome") like Task Manager. Only existing .exe files are read; results are cached for the window's lifetime.
    /// </summary>
    private void OnHostResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string? path = query["path"];
        var env = _web.CoreWebView2.Environment;
        const string cors = "Access-Control-Allow-Origin: *\r\nCache-Control: max-age=86400\r\n";

        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", cors);
            return;
        }

        if (uri.AbsolutePath == "/icon")
        {
            if (!_icons.TryGetValue(path, out var png))
            {
                png = ExtractIconPng(path, query["size"] is { } s && int.TryParse(s, out int px) ? Math.Clamp(px, 16, 64) : 32);
                _icons[path] = png;
            }
            e.Response = png is null
                ? env.CreateWebResourceResponse(null, 404, "Not Found", cors)
                : env.CreateWebResourceResponse(new MemoryStream(png), 200, "OK", cors + "Content-Type: image/png");
            return;
        }

        if (uri.AbsolutePath == "/describe")
        {
            if (!_descriptions.TryGetValue(path, out var text))
            {
                try { text = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; }
                _descriptions[path] = text;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["description"] = text }));
            e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", cors + "Content-Type: application/json");
            return;
        }

        e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", cors);
    }

    private static byte[]? ExtractIconPng(string path, int size)
    {
        nint large = 0, small = 0;
        try
        {
            // The large (32 px) icon scaled down looks better than the 16 px one at 150 % and 200 % scaling.
            if (ExtractIconExW(path, 0, ref large, ref small, 1) == 0 || (large == 0 && small == 0)) return null;
            using var icon = Icon.FromHandle(large != 0 ? large : small);
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                g.DrawIcon(icon, new Rectangle(0, 0, size, size));
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException) { return null; }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0) DestroyIcon(small);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, ref nint large, ref nint small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    // ---- Start with Windows: the per-user Run value the installer also writes (HKCU ...\Run, "Corewright") ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Corewright";

    /// <summary>The tray next to this dashboard (installed or published). Development builds do not register autostart.</summary>
    private static string? TrayPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Corewright.Tray.exe");
        return File.Exists(path) ? path : null;
    }

    private void PostAutostart()
    {
        string? tray = TrayPath();
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        bool enabled = key?.GetValue(RunValue) is string v && v.Length > 0;
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "autostart",
            ["enabled"] = enabled,
            ["available"] = tray is not null,
            ["note"] = tray is null ? "Only available in an installed or published Corewright (start.bat or the installer)." : null,
        }));
    }

    private static void SetAutostart(bool enabled)
    {
        string? tray = TrayPath();
        if (tray is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{tray}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
        StartupPreferences.RememberDecision();
    }

    private async Task OnResizedAsync()
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        if (WindowState == FormWindowState.Minimized)
        {
            _web.Visible = false;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            try { await core.TrySuspendAsync(); } catch (InvalidOperationException) { }
        }
        else if (!_web.Visible)
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _web.Visible = true;
        }
    }
}
