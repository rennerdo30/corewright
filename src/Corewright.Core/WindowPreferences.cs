using Microsoft.Win32;

namespace Corewright.Core;

/// <summary>
/// How the Corewright window behaves (owner, 2026-11-02: "start minimized, close to tray and minimize to tray").
/// Per user, in HKCU\Software\Corewright, so the tray (NativeAOT) and the window read the same values without JSON.
/// </summary>
public static class WindowPreferences
{
    private const string Key = @"Software\Corewright";

    /// <summary>Open the window when Corewright starts at sign-in. Off = start minimized in the tray (default).</summary>
    public static bool OpenOnStart
    {
        get => Read("OpenWindowOnStart", false);
        set => Write("OpenWindowOnStart", value);
    }

    /// <summary>The window's close button keeps Corewright running in the tray (default). Off = it exits Corewright.</summary>
    public static bool CloseToTray
    {
        get => Read("CloseToTray", true);
        set => Write("CloseToTray", value);
    }

    /// <summary>Minimizing hides the window in the tray instead of the taskbar.</summary>
    public static bool MinimizeToTray
    {
        get => Read("MinimizeToTray", false);
        set => Write("MinimizeToTray", value);
    }

    private static bool Read(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int v ? v != 0 : fallback;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return fallback; }
    }

    private static void Write(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }
}
