using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corewright.Core;
using Microsoft.Win32;

namespace Corewright.Dashboard;

internal sealed partial class MainForm
{
    private DashboardUpdater? _updater;
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Also check when the service is offline and the fallback page is shown.
        _ = OnUpdateMessageAsync("update-check", automatic: true);
    }

    private async Task OnUpdateMessageAsync(string action, bool automatic, string? channel = null)
    {
        if (_updater is null)
        {
            _updater = new DashboardUpdater(state =>
            {
                if (!IsDisposed && _web.CoreWebView2 is { } core)
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(state));
            });
            FormClosed += (_, _) => _updater.Dispose();
        }
        await _updater.HandleAsync(action, automatic, channel);
    }
}

/// <summary>Only lives with the dashboard. No timers, service work, credentials or background polling.</summary>
internal sealed class DashboardUpdater(Action<DashboardUpdater.Status> changed) : IDisposable
{
    private const string Repo = "https://github.com/rennerdo30/corewright";
    private const string Downloads = Repo + "/releases/download/";
    private const string Api = "https://api.github.com/repos/rennerdo30/corewright/releases/latest";
    private const string ApiList = "https://api.github.com/repos/rennerdo30/corewright/releases?per_page=30";
    private static readonly string CachePath = Path.Combine(AppPaths.UserDataDirectory, "update-check.json");
    private static readonly string ChannelPath = Path.Combine(AppPaths.UserDataDirectory, "update-channel.json");
    private readonly CancellationTokenSource _closed = new();
    private Cache _cache = ReadCache();
    private string _channel = ReadChannel();
    private bool _busy;
    private string _phase = "idle", _message = "";
    internal static string CurrentVersion => typeof(DashboardUpdater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    internal sealed record Release(string Version, string Url, string SetupUrl, string SumsUrl);
    private sealed record Cache(DateTimeOffset? CheckedAt, Release? Release, string Message);
    // Camel-case names are the WebView message contract, independent of service JSON settings.
    internal sealed record Status(string type, string currentVersion, string? latestVersion, DateTimeOffset? lastChecked,
        bool available, bool canInstall, bool busy, string phase, string message, string releaseUrl, string channel);

    /// <summary>
    /// "stable" or "beta" (owner, 2026-11-02). Until the user picks one, the channel follows the installed version:
    /// a beta setup updates to newer betas, a stable setup only to stable releases.
    /// </summary>
    private static string ReadChannel()
    {
        try
        {
            if (new FileInfo(ChannelPath) is { Exists: true, Length: <= 4096 } &&
                JsonDocument.Parse(File.ReadAllText(ChannelPath)).RootElement.TryGetProperty("channel", out var c) &&
                c.GetString() is "stable" or "beta")
                return c.GetString()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return CurrentVersion.Contains('-') ? "beta" : "stable";
    }

    private void SetChannel(string channel)
    {
        if (channel is not ("stable" or "beta") || channel == _channel) return;
        _channel = channel;
        Directory.CreateDirectory(AppPaths.UserDataDirectory);
        File.WriteAllText(ChannelPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["channel"] = channel }));
        // A release found on the other channel must not be offered any more.
        _cache = _cache with { CheckedAt = null, Release = null, Message = "" };
        SaveCache();
    }

    private static Cache ReadCache()
    {
        try
        {
            if (new FileInfo(CachePath) is { Exists: true, Length: <= 65536 })
                return JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) ?? new(null, null, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new(null, null, "");
    }

    private void SaveCache()
    {
        Directory.CreateDirectory(AppPaths.UserDataDirectory);
        string temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_cache));
        File.Move(temporary, CachePath, overwrite: true);
    }

    private static bool Installed()
    {
        try
        {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Corewright");
        return key?.GetValue("InstallLocation") is string location &&
            string.Equals(Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return false; }
    }

    private void Post()
    {
        var release = _cache.Release;
        bool available = release is not null && SemVersion.TryParse(release.Version, out var latest) &&
            SemVersion.TryParse(CurrentVersion, out var current) && latest.CompareTo(current) > 0;
        changed(new("update-status", CurrentVersion, release?.Version, _cache.CheckedAt, available,
            available && Installed(), _busy, _phase, _message.Length > 0 ? _message : _cache.Message,
            release?.Url is { } url && ValidReleaseUrl(url) ? url : Repo + "/releases", _channel));
    }

    internal async Task HandleAsync(string action, bool automatic, string? channel = null)
    {
        if (_closed.IsCancellationRequested) return;
        if (action == "update-channel" && !_busy)
        {
            SetChannel(channel ?? "");
            action = "update-check";
            automatic = false;
        }
        if (action == "update-status" || _busy) { Post(); return; }
        _busy = true;
        try
        {
            if (action == "update-install") await InstallAsync();
            else if (!automatic || _cache.CheckedAt is not { } checkedAt || DateTimeOffset.UtcNow - checkedAt >= TimeSpan.FromDays(1))
                await CheckAsync();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception e)
        {
            _phase = "error";
            _message = e is Win32Exception { NativeErrorCode: 1223 } ? "Administrator approval was cancelled." :
                e is OperationCanceledException ? "The update operation timed out. Try again." : e.Message;
        }
        finally
        {
            _busy = false;
            if (!_closed.IsCancellationRequested) Post();
        }
    }

    private void Progress(string phase, string message) { _phase = phase; _message = message; Post(); }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Corewright/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private async Task CheckAsync()
    {
        Progress("checking", "Checking GitHub Releases…");
        // Persist the attempt first: network failures also respect the automatic daily limit.
        _cache = _cache with { CheckedAt = DateTimeOffset.UtcNow, Release = null, Message = "The last check did not complete. Choose Check now to retry." };
        SaveCache();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var release = await FetchReleaseAsync(_channel == "beta", timeout.Token);
        _cache = _cache with { Release = release, Message = release is null ? "No published release is available yet." :
            SemVersion.TryParse(release.Version, out var latest) && SemVersion.TryParse(CurrentVersion, out var current) && latest.CompareTo(current) > 0
                ? "An update is available." : "You’re up to date." };
        SaveCache();
        Progress("idle", _cache.Message);
    }

    // Kept separate so a unit-free metadata smoke can call the real API without
    // opening a window, touching the user's cache, or downloading executables.
    internal static async Task<Release?> FetchReleaseAsync(bool beta, CancellationToken token)
    {
        using var client = Client();
        using var response = await client.GetAsync(beta ? ApiList : Api, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        byte[] body = await ReadBoundedAsync(response, 4 * 1024 * 1024, token);
        if (!beta) return ParseRelease(body, allowPrerelease: false);

        // Beta channel: the highest published version, beta or stable (a stable 0.1.0 is newer than 0.1.0-beta.3).
        using var document = JsonDocument.Parse(body);
        Release? best = null;
        SemVersion? bestVersion = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            Release candidate;
            try { candidate = ParseRelease(item, allowPrerelease: true); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException) { continue; }
            if (!SemVersion.TryParse(candidate.Version, out var v)) continue;
            if (bestVersion is null || v.CompareTo(bestVersion) > 0) { best = candidate; bestVersion = v; }
        }
        return best;
    }

    internal static Release ParseRelease(byte[] json, bool allowPrerelease)
    {
        using var document = JsonDocument.Parse(json);
        return ParseRelease(document.RootElement, allowPrerelease);
    }

    internal static Release ParseRelease(JsonElement root, bool allowPrerelease)
    {
        if (root.GetProperty("draft").GetBoolean() || (!allowPrerelease && root.GetProperty("prerelease").GetBoolean()))
            throw new InvalidDataException(allowPrerelease ? "Only published releases can be installed." : "Only published stable releases can be installed.");
        string version = root.GetProperty("tag_name").GetString() ?? "";
        if (version.StartsWith('v')) version = version[1..];
        if (!SemVersion.TryParse(version, out _)) throw new InvalidDataException("The release version is not valid SemVer.");
        string url = root.GetProperty("html_url").GetString() ?? "";
        if (!ValidReleaseUrl(url)) throw new InvalidDataException("The release link is outside Corewright’s repository.");
        string asset(string name)
        {
            var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"The release must contain exactly one {name} asset.");
            string value = matches[0].GetProperty("browser_download_url").GetString() ?? "";
            ValidateAssetUrl(value);
            return value;
        }
        return new(version, url, asset($"Corewright-{version}-setup.exe"), asset("SHA256SUMS.txt"));
    }

    private static bool ValidReleaseUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith("/rennerdo30/corewright/releases/", StringComparison.Ordinal);

    private static void ValidateAssetUrl(string url)
    {
        if (!url.StartsWith(Downloads, StringComparison.Ordinal) || !ValidReleaseUrl(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.StartsWith("/rennerdo30/corewright/releases/download/", StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("An update asset URL is outside Corewright’s release downloads.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("The update response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        return buffer.ToArray();
    }

    private async Task InstallAsync()
    {
        if (!Installed()) throw new InvalidOperationException("Install updates from GitHub when running a development copy.");
        var release = _cache.Release ?? throw new InvalidDataException("Check for an update first.");
        if (!SemVersion.TryParse(release.Version, out var latest) || !SemVersion.TryParse(CurrentVersion, out var current) || latest.CompareTo(current) <= 0)
            throw new InvalidDataException("No newer version is available.");
        ValidateAssetUrl(release.SetupUrl);
        ValidateAssetUrl(release.SumsUrl);
        // Only trusted, installed code is elevated: Corewright.Updater.exe next to this window in the admin-only
        // install folder (Installed() checked that). It takes nothing from us but the version; it downloads and
        // verifies the release itself. The admin prompt names Corewright, not PowerShell (owner, 2026-11-02).
        string updater = Path.Combine(AppContext.BaseDirectory, "Corewright.Updater.exe");
        if (!File.Exists(updater)) throw new IOException("Corewright Updater is missing from this installation. Download the setup from GitHub.");
        var started = DateTimeOffset.UtcNow;
        Progress("installing", "Approve the Windows administrator prompt from Corewright Updater. It downloads, verifies and installs the update, then reopens Corewright.");
        using var process = await Task.Run(() => Process.Start(new ProcessStartInfo(updater, $"--version {release.Version}")
            { UseShellExecute = true, Verb = "runas" }), _closed.Token)
            ?? throw new IOException("Windows could not start Corewright Updater.");
        Debug.WriteLine($"Corewright Updater started: PID {process.Id}");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        wait.CancelAfter(TimeSpan.FromMinutes(15));
        await process.WaitForExitAsync(wait.Token);
        if (process.ExitCode != 0)
        {
            // The elevated helper leaves its reason (and setup's last log line) next to Corewright's data.
            string? reason = null;
            try
            {
                var file = new FileInfo(Path.Combine(AppPaths.ServiceDataDirectory, "update-last-error.txt"));
                if (file is { Exists: true, Length: < 4096 } && file.LastWriteTimeUtc >= started.UtcDateTime.AddSeconds(-5))
                    reason = File.ReadAllText(file.FullName).Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw new IOException(reason is { Length: > 0 }
                ? $"The update did not install: {reason}"
                : $"The update did not install (exit {process.ExitCode}). Nothing was changed; the setup log is %TEMP%\\Corewright-installer-{release.Version}.log.");
        }
        Progress("installed", "Update installed. Reopen Corewright from the tray.");
    }

    public void Dispose() { _closed.Cancel(); }

    internal sealed record SemVersion(string[] Numbers, string[] Pre) : IComparable<SemVersion>
    {
        internal static bool TryParse(string value, out SemVersion version)
        {
            var match = Regex.Match(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
            string[] pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            version = new([match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], pre);
            return match.Success && !pre.Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit));
        }
        private static int Numeric(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        public int CompareTo(SemVersion? other)
        {
            if (other is null) return 1;
            for (int i = 0; i < 3; i++) { int n = Numeric(Numbers[i], other.Numbers[i]); if (n != 0) return n; }
            if (Pre.Length == 0 || other.Pre.Length == 0) return (other.Pre.Length > 0).CompareTo(Pre.Length > 0);
            for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
            {
                bool a = Pre[i].All(char.IsAsciiDigit), b = other.Pre[i].All(char.IsAsciiDigit);
                int n = a && b ? Numeric(Pre[i], other.Pre[i]) : a != b ? (a ? -1 : 1) : string.CompareOrdinal(Pre[i], other.Pre[i]);
                if (n != 0) return n;
            }
            return Pre.Length.CompareTo(other.Pre.Length);
        }
    }
}
