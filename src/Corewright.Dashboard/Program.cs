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
        Application.Run(new MainForm());
    }
}

/// <summary>
/// Hosts the UI in WebView2 with a native Windows 11 frame. Suspends the page while minimized and ends the
/// process on close, so the dashboard costs nothing when nobody looks at it.
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly EventWaitHandle _show = new(false, EventResetMode.AutoReset, Program.ShowEvent);
    private readonly EventWaitHandle _exit = new(false, EventResetMode.ManualReset, Program.ExitEvent);
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _exitWait;

    public MainForm()
    {
        Text = "Corewright";
        Width = 1400;
        Height = 900;
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "corewright.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        ApplyTheme();
        Controls.Add(_web);

        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => BeginInvoke(BringToFront), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => BeginInvoke(Close), null, -1, true);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Load += async (_, _) => await InitAsync();
        Resize += async (_, _) => await OnResizedAsync();
        FormClosed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
        };
    }

    private new void BringToFront()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            BeginInvoke(ApplyTheme);
    }

    // ---- Native Windows 11 frame -------------------------------------------------------------

    private static bool IsDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;

    /// <summary>Title bar and window background in the Windows 11 base colours, so frame and page read as one surface.</summary>
    private void ApplyTheme()
    {
        bool dark = IsDarkTheme();
        var bg = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
        BackColor = bg;
        _web.DefaultBackgroundColor = bg;
        if (!IsHandleCreated) HandleCreated += (_, _) => ApplyFrame(dark, bg);
        else ApplyFrame(dark, bg);
    }

    private void ApplyFrame(bool dark, Color bg)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);                       // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);                    // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(Handle, 35, ref caption, 4);                  // DWMWA_CAPTION_COLOR
        int text = dark ? 0x00FFFFFF : 0x001A1A1A;
        DwmSetWindowAttribute(Handle, 36, ref text, 4);                     // DWMWA_TEXT_COLOR
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
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#endif
        core.WebMessageReceived += OnWebMessage;

        var ep = EndpointInfo.TryReadAny();
        if (ep is null)
        {
            bool dark = IsDarkTheme();
            core.NavigateToString(
                $"<html><body style='background:{(dark ? "#202020" : "#f3f3f3")};color:{(dark ? "#fff" : "#1a1a1a")};" +
                "font:14px \"Segoe UI Variable Text\",\"Segoe UI\";padding:40px'>" +
                "<h2 style='font-weight:600'>The Corewright service is not running</h2>" +
                "<p>Start it with start.bat, then reopen this window.</p></body></html>");
            return;
        }

        // Token and theme go to the page in memory only; nothing appears in a URL.
        var boot = new Dictionary<string, object> { ["token"] = ep.Token, ["accent"] = AccentPalette(), ["host"] = "dashboard" };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__COREWRIGHT__ = " + JsonSerializer.Serialize(boot) + ";");

        string url = Environment.GetEnvironmentVariable("COREWRIGHT_UI_URL") is { Length: > 0 } dev ? dev : ep.Url + "/";
        core.Navigate(url);
    }

    /// <summary>Host actions the page cannot do itself. Only "reveal" exists, and only for an existing file.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "reveal" &&
                root.TryGetProperty("path", out var pathEl) && pathEl.GetString() is { } path && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            }
        }
        catch (JsonException) { }
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
