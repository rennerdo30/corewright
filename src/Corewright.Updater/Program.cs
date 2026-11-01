using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Corewright Updater: started elevated by the Corewright window with "--version <x.y.z[-pre]>".
// Same steps as the former PowerShell bootstrap, as a Corewright program so the admin prompt names Corewright:
// 1. take nothing from the caller but the version (no URL, no checksum, no path);
// 2. create a fresh administrator-only folder under Program Files and keep its ancestors pinned;
// 3. download SHA256SUMS.txt and the setup from the fixed GitHub release URL of that version;
// 4. hash the setup through the pinned handle and compare;
// 5. run it silently with /RESTART (Corewright reopens afterwards) and wait at most 3 minutes.
// A failure leaves a readable reason in %ProgramData%\Corewright\update-last-error.txt for the window.

const string Repo = "https://github.com/rennerdo30/corewright/releases/download/v";
string? version = args.Length == 2 && args[0] == "--version" ? args[1] : null;

try
{
    if (version is null || !Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$"))
        throw new InvalidDataException("Not a valid Corewright version.");

    string parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    using var ancestors = new SecureFiles.PinnedPath(parent, true, 0);
    string stage = Path.Combine(parent, "Corewright-update-" + Guid.NewGuid().ToString("N"));
    SecureFiles.CreateProtectedDirectory(stage, false);
    string name = $"Corewright-{version}-setup.exe";
    string setup = Path.Combine(stage, name);
    string url = Repo + version;

    string expected;
    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Corewright-Updater");
        using (var sums = new MemoryStream())
        {
            Download(client, $"{url}/SHA256SUMS.txt", sums, 65536);
            var hashes = Encoding.UTF8.GetString(sums.ToArray()).Split('\n')
                .Select(l => Regex.Match(l.TrimEnd('\r'), @"^([0-9a-fA-F]{64}) [ *](.+)$"))
                .Where(m => m.Success && m.Groups[2].Value == name)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .ToList();
            if (hashes.Count != 1) throw new InvalidDataException("Setup checksum is missing or duplicated.");
            expected = hashes[0];
        }
        using var created = SecureFiles.CreateProtectedFile(setup);
        using var output = new FileStream(created, FileAccess.Write);
        Download(client, $"{url}/{name}", output, 268_435_456);
    }

    // Hash through a pinned handle and keep it (and every ancestor) pinned while setup runs.
    using var pinned = new SecureFiles.PinnedPath(setup, true, 0x80000000);
    using var file = new FileStream(pinned.Leaf, FileAccess.Read);
    string digest = Convert.ToHexString(SHA256.HashData(file));
    if (digest != expected) throw new InvalidDataException("The downloaded setup does not match its published checksum.");

    using var process = Process.Start(new ProcessStartInfo(setup, "/S /RESTART") { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new IOException("Windows could not start the setup.");
    if (!process.WaitForExit(180_000)) throw new TimeoutException("The setup did not finish within 3 minutes.");
    if (process.ExitCode != 0) throw new IOException($"The setup failed (exit {process.ExitCode}).");
    // The protected stage is kept for diagnosis; no recursive deletion of paths outside our control.
    return 0;
}
catch (Exception e)
{
    try
    {
        string reason = e.Message;
        string log = Path.Combine(Path.GetTempPath(), $"Corewright-installer-{version}.log");
        if (version is not null && File.Exists(log) && File.ReadLines(log).LastOrDefault() is { Length: > 0 } last)
            reason += " Setup said: " + last.Trim();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corewright");
        if (Directory.Exists(dir)) File.WriteAllText(Path.Combine(dir, "update-last-error.txt"), reason, Encoding.UTF8);
    }
    catch (Exception) { }
    return 1;
}

static void Download(HttpClient client, string url, Stream output, long limit)
{
    using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
    response.EnsureSuccessStatusCode();
    using var input = response.Content.ReadAsStream();
    var buffer = new byte[65536];
    long total = 0;
    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (true)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("The download timed out.");
        var read = input.ReadAsync(buffer, 0, buffer.Length);
        if (!read.Wait(30_000)) throw new TimeoutException("The download stalled.");
        int count = read.Result;
        if (count == 0) break;
        total += count;
        if (total > limit) throw new InvalidDataException("The download is larger than allowed.");
        output.Write(buffer, 0, count);
    }
}
