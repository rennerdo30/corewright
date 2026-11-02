using System.Text.Json;
using Corewright.Core;
using Corewright.Core.Autopilot;

namespace Corewright.Service;

/// <summary>settings.json is user data like rules.json: atomic writes, previous version kept, unreadable file never overwritten.</summary>
internal sealed class SettingsStore(string dataDirectory, ILogger<SettingsStore> log)
{
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");
    private bool _readOnly;

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            AppSettings s;
            using (var fs = File.OpenRead(_path))
                s = JsonSerializer.Deserialize(fs, CoreJson.Default.AppSettings) ?? new AppSettings();
            return Migrate(s);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _readOnly = true;
            log.LogError("settings.json could not be read ({Message}); using defaults and leaving the file untouched", e.Message);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Older files stored every default explicitly, so a changed default must be carried over once. Version 2: background
    /// placement is on by default (owner, 2026-11-02); files written before stored "off" only because it was the old default.
    /// Version 3: screenshot and overlay tools join the "never games" list (ShareX was detected as a game).
    /// </summary>
    private AppSettings Migrate(AppSettings s)
    {
        if (s.Version >= AppSettings.CurrentVersion) return s;
        if (s.Version < 2) s.Autopilot.Background.Enabled = true;
        if (s.Version < 3)
        {
            var notGames = s.Autopilot.GameMode.NotGames;
            foreach (var tool in GameModeSettings.ScreenTools)
                if (!notGames.Contains(tool, StringComparer.OrdinalIgnoreCase)) notGames.Add(tool);
        }
        if (s.Version < 4)
        {
            foreach (var launcher in GameModeSettings.GameLaunchers)
                if (!s.Autopilot.GameMode.NotGames.Contains(launcher, StringComparer.OrdinalIgnoreCase)) s.Autopilot.GameMode.NotGames.Add(launcher);
            if (s.Autopilot.Balance.ReleaseSeconds == 6) s.Autopilot.Balance.ReleaseSeconds = 30;
        }
        if (s.Version < 5) s.Autopilot.Mode = s.Autopilot.LegacyEnabled ? "on" : "off";
        s.Version = AppSettings.CurrentVersion;
        try { Save(s); }
        catch (IOException e) { log.LogWarning("settings.json migration could not be saved ({Message})", e.Message); }
        log.LogInformation("settings.json migrated to version {Version}", s.Version);
        return s;
    }

    public void Save(AppSettings settings)
    {
        if (_readOnly && File.Exists(_path))
        {
            File.Copy(_path, _path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
            _readOnly = false;
        }
        var tmp = _path + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, settings, CoreJson.Default.AppSettings);
        if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(tmp, _path, overwrite: true);
    }
}
