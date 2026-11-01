using System.Text.Json;
using System.Text.Json.Serialization;
using Corewright.Core.Rules;

namespace Corewright.Core;

/// <summary>Written by the service at start: where the API listens and the bearer token for this run.</summary>
public sealed class EndpointInfo
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public int Pid { get; set; }
    public string Mode { get; set; } = "";

    public const string FileName = "endpoint.json";

    /// <summary>Console-mode data folder first (development), then the service's.</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        yield return AppPaths.UserDataDirectory;
        yield return AppPaths.ServiceDataDirectory;
    }

    public static EndpointInfo? TryReadAny()
    {
        foreach (var dir in CandidateDirectories())
        {
            var info = TryRead(Path.Combine(dir, FileName));
            if (info is not null) return info;
        }
        return null;
    }

    public static EndpointInfo? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, CoreJson.Default.EndpointInfo);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}

public static class AppPaths
{
    public const string AppFolder = "Corewright";

    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);

    public static string ServiceDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(EndpointInfo))]
[JsonSerializable(typeof(RuleSet))]
public sealed partial class CoreJson : JsonSerializerContext;
