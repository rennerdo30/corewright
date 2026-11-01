using System.Text.Json;
using Corewright.Core;
using Corewright.Core.Rules;

namespace Corewright.Service;

/// <summary>rules.json is user data: atomic writes, the previous version kept as .bak, an unreadable file is never overwritten.</summary>
internal sealed class RuleStore(string dataDirectory, ILogger<RuleStore> log)
{
    private readonly string _path = Path.Combine(dataDirectory, "rules.json");
    private bool _readOnly;

    public RuleSet Load()
    {
        if (!File.Exists(_path))
        {
            var seed = Seed();
            Save(seed);
            return seed;
        }
        try
        {
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize(fs, CoreJson.Default.RuleSet) ?? new RuleSet();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _readOnly = true;
            log.LogError("rules.json could not be read ({Message}); starting without rules and leaving the file untouched", e.Message);
            return new RuleSet();
        }
    }

    public void Save(RuleSet set)
    {
        if (_readOnly && File.Exists(_path))
        {
            File.Copy(_path, _path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
            _readOnly = false;
        }
        var tmp = _path + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, set, CoreJson.Default.RuleSet);
        if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Disabled examples so the rule list is not empty on first start.</summary>
    private static RuleSet Seed() => new()
    {
        Rules =
        [
            new Rule
            {
                Id = "example-background-builds",
                Name = "Example: calm down compilers in the background",
                Enabled = false,
                Priority = 10,
                When = Condition.All(
                    Condition.Leaf("name", "in", "cl.exe, link.exe, MSBuild.exe, cargo.exe, rustc.exe"),
                    Condition.Leaf("isForeground", "isFalse", null)),
                Actions = new RuleActions { CpuPriority = "belowNormal", IoPriority = "low", EfficiencyMode = true },
            },
            new Rule
            {
                Id = "example-browser-cap",
                Name = "Example: cap a browser process at 4 GB",
                Enabled = false,
                Priority = 20,
                When = Condition.All(Condition.Leaf("name", "eq", "chrome.exe")),
                Actions = new RuleActions { MemoryLimitMb = 4096 },
            },
        ],
    };
}
