using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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

    private async Task OnUpdateMessageAsync(string action, bool automatic)
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
        await _updater.HandleAsync(action, automatic);
    }
}

/// <summary>Only lives with the dashboard. No timers, service work, credentials or background polling.</summary>
internal sealed class DashboardUpdater(Action<DashboardUpdater.Status> changed) : IDisposable
{
    private const string Repo = "https://github.com/rennerdo30/corewright";
    private const string Downloads = Repo + "/releases/download/";
    private const string Api = "https://api.github.com/repos/rennerdo30/corewright/releases/latest";
    private static readonly string CachePath = Path.Combine(AppPaths.UserDataDirectory, "update-check.json");
    private readonly CancellationTokenSource _closed = new();
    private Cache _cache = ReadCache();
    private bool _busy;
    private string _phase = "idle", _message = "";
    internal static string CurrentVersion => typeof(DashboardUpdater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    internal sealed record Release(string Version, string Url, string SetupUrl, string SumsUrl);
    private sealed record Cache(DateTimeOffset? CheckedAt, Release? Release, string Message);
    // Camel-case names are the WebView message contract, independent of service JSON settings.
    internal sealed record Status(string type, string currentVersion, string? latestVersion, DateTimeOffset? lastChecked,
        bool available, bool canInstall, bool busy, string phase, string message, string releaseUrl);

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
            release?.Url is { } url && ValidReleaseUrl(url) ? url : Repo + "/releases"));
    }

    internal async Task HandleAsync(string action, bool automatic)
    {
        if (_closed.IsCancellationRequested) return;
        try
        {
            if (action == "update-status" || _busy) { Post(); return; }
            _busy = true;
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
        var release = await FetchReleaseAsync(timeout.Token);
        _cache = _cache with { Release = release, Message = release is null ? "No published release is available yet." :
            SemVersion.TryParse(release.Version, out var latest) && SemVersion.TryParse(CurrentVersion, out var current) && latest.CompareTo(current) > 0
                ? "An update is available." : "You’re up to date." };
        SaveCache();
        Progress("idle", _cache.Message);
    }

    // Kept separate so a unit-free metadata smoke can call the real API without
    // opening a window, touching the user's cache, or downloading executables.
    internal static async Task<Release?> FetchReleaseAsync(CancellationToken token)
    {
        using var client = Client();
        using var response = await client.GetAsync(Api, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await ReadBoundedAsync(response, 1024 * 1024, token));
    }

    internal static Release ParseRelease(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Only published stable releases can be installed.");
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
        string name = $"Corewright-{release.Version}-setup.exe";
        string folder = Path.Combine(AppPaths.UserDataDirectory, "updates", release.Version, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string setup = Path.Combine(folder, name);
        using var client = Client();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        Progress("downloading", "Downloading update checksums…");
        using var sumsResponse = await client.GetAsync(release.SumsUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        sumsResponse.EnsureSuccessStatusCode();
        byte[] sums = await ReadBoundedAsync(sumsResponse, 65536, timeout.Token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "SHA256SUMS.txt"), sums, timeout.Token);
        string[] hashes = System.Text.Encoding.UTF8.GetString(sums).Split('\n')
            .Select(line => Regex.Match(line.TrimEnd('\r'), @"^([0-9a-fA-F]{64}) [ *](.+)$"))
            .Where(match => match.Success && match.Groups[2].Value == name).Select(match => match.Groups[1].Value).ToArray();
        if (hashes.Length != 1) throw new InvalidDataException("The setup checksum is missing or duplicated.");
        Progress("downloading", "Downloading Corewright setup…");
        using (var response = await client.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(setup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            byte[] bytes = new byte[65536];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(bytes, timeout.Token)) > 0)
            {
                total += count;
                if (total > 256 * 1024 * 1024) throw new InvalidDataException("The setup exceeds the 256 MB download limit.");
                await output.WriteAsync(bytes.AsMemory(0, count), timeout.Token);
            }
        }
        Progress("verifying", "Verifying SHA-256…");
        // Deny writes and deletion between the final hash and process creation.
        await using var verified = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(verified, timeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(hashes[0])))
            throw new InvalidDataException("SHA-256 mismatch. The downloaded setup was not run. Check again or download from GitHub.");
        timeout.Token.ThrowIfCancellationRequested();
        Progress("installing", "Approve the Windows administrator prompt. Setup will close Corewright and restart the tray.");
        using var process = await Task.Run(() => Process.Start(new ProcessStartInfo(setup, "/S") { UseShellExecute = true, Verb = "runas" }), timeout.Token)
            ?? throw new IOException("Windows could not start the verified setup.");
        Debug.WriteLine($"Corewright setup started: PID {process.Id}");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(180));
        await process.WaitForExitAsync(wait.Token);
        if (process.ExitCode != 0) throw new IOException($"Setup failed (exit {process.ExitCode}). See the installer log.");
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
