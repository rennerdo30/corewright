using System.Text.Json;
using Microsoft.Win32;

namespace Corewright.Core;

/// <summary>Per-user startup work runs in the tray/dashboard, never in an elevated installer.</summary>
public static class StartupPreferences
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Preferences = @"Software\Corewright";
    public static void RememberDecision()
    {
        using var preferences = Registry.CurrentUser.CreateSubKey(Preferences);
        preferences.SetValue("AutostartDecided", 1, RegistryValueKind.DWord);
    }

    public static void ApplyMachineDefault()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var installed = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Corewright");
            if (installed?.GetValue("InstallLocation") is not string location ||
                !string.Equals(Path.GetFullPath(location).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            using var preferences = Registry.CurrentUser.CreateSubKey(Preferences);
            if (preferences.GetValue("AutostartDecided") is int) return;
            string path = Path.Combine(AppPaths.ServiceDataDirectory, "defaults.json");
            if (new FileInfo(path) is not { Exists: true, Length: <= 16384 }) return;
            using var defaults = JsonDocument.Parse(File.ReadAllText(path));
            using var run = Registry.CurrentUser.CreateSubKey(Run);
            // Preserve existing registrations, including those predating the marker.
            if (run.GetValue("Corewright") is null && defaults.RootElement.GetProperty("autostart").GetBoolean())
                run.SetValue("Corewright", $"\"{Path.Combine(AppContext.BaseDirectory, "Corewright.Tray.exe")}\"");
            RememberDecision();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
    }
}
