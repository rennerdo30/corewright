using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Corewright.Dashboard;

/// <summary>
/// The window never runs without the tray icon (owner, 2026-11-02: "it should not be possible to have the gui without
/// the tray!"). The tray holds the named mutex <c>Local\Corewright.Tray</c> for its whole life; the window starts the
/// tray when the mutex is missing and, while open, waits on that mutex: when the tray goes away (crash or closed by
/// hand) the window restarts it, unless the tray is closing the window itself. No polling: one blocked thread.
/// </summary>
internal static partial class TrayGuard
{
    private const string TrayMutex = @"Local\Corewright.Tray";
    private const uint WM_EXIT_ALL = 0x8000 + 3; // WM_APP + 3, see Corewright.Tray

    public static void Ensure()
    {
        if (Mutex.TryOpenExisting(TrayMutex, out var existing)) { existing.Dispose(); return; }
        string exe = Path.Combine(AppContext.BaseDirectory, "Corewright.Tray.exe");
        if (!File.Exists(exe))
        {
            // Development build: src/Corewright.Dashboard/bin/<cfg>/<tfm>/ → src/Corewright.Tray/bin/<cfg>/<tfm>/
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (dir.Parent?.Parent?.Parent?.Parent is { } src)
                exe = Path.Combine(src.FullName, "Corewright.Tray", "bin", dir.Parent!.Name, dir.Name, "Corewright.Tray.exe");
        }
        if (!File.Exists(exe)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            // Give the tray a moment to take its mutex, so Watch sees it.
            for (int i = 0; i < 40; i++)
            {
                if (Mutex.TryOpenExisting(TrayMutex, out var started)) { started.Dispose(); break; }
                Thread.Sleep(50);
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Waits for the tray to go away and restarts it while the window stays open.</summary>
    public static void Watch(Form window)
    {
        var thread = new Thread(() =>
        {
            while (!window.IsDisposed)
            {
                if (!Mutex.TryOpenExisting(TrayMutex, out var mutex))
                {
                    Ensure();
                    if (!Mutex.TryOpenExisting(TrayMutex, out mutex)) return; // tray missing from this install: nothing to watch
                }
                using (mutex)
                {
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { } // the tray crashed
                    mutex.ReleaseMutex();
                }
                // A tray that exits on purpose closes the window first; give that a moment before restarting.
                Thread.Sleep(1500);
                if (window.IsDisposed || window.Disposing) return;
            }
        }) { IsBackground = true, Name = "Corewright tray guard" };
        thread.Start();
    }

    /// <summary>Asks the tray to exit Corewright (window, tray and a console-mode service).</summary>
    public static void ExitAll()
    {
        nint tray = FindWindowW("CorewrightTray", null);
        if (tray != 0) PostMessageW(tray, WM_EXIT_ALL, 0, 0);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
}
