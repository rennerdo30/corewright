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
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize(fs, CoreJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _readOnly = true;
            log.LogError("settings.json could not be read ({Message}); using defaults and leaving the file untouched", e.Message);
            return new AppSettings();
        }
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
