using System.Text.RegularExpressions;
using Corewright.Service.Windows;
using Corewright.Core.Engine;

namespace Corewright.Service;

/// <summary>
/// What a process really is (owner, 2026-11-02: "for things like python.exe or java.exe [keying by exe name] is a
/// brutal mistake"). Normal programs are identified by their exe path; generic hosts by what they run, read once from
/// the command line: "python.exe (train.py)", "java.exe (server.jar)", "svchost.exe (wuauserv)".
/// </summary>
internal static partial class ProgramIdentity
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "node.exe", "bun.exe", "deno.exe", "java.exe", "javaw.exe",
        "dotnet.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "dllhost.exe",
        "svchost.exe", "ruby.exe", "perl.exe", "php.exe", "Rscript.exe", "lua.exe", "julia.exe", "electron.exe", "mshta.exe",
        "WmiPrvSE.exe", "msiexec.exe", "taskhostw.exe", "WUDFHost.exe",
    };

    /// <summary>Shell and session processes: an app's root is the topmost ancestor below one of these.</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "userinit.exe", "sihost.exe", "smss.exe",
        "csrss.exe", "RuntimeBroker.exe", "taskhostw.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "Corewright.Tray.exe", "Corewright.Service.exe", "ApplicationFrameHost.exe",
    };

    /// <summary>
    /// Processes Autopilot never slows down: audio, input, display drivers, the network core. Generic hosts are
    /// matched by what they run (the service name for svchost).
    /// </summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg.exe", "WUDFHost.exe", "dwm.exe", "ctfmon.exe", "TextInputHost.exe", "fontdrvhost.exe", "conhost.exe",
        "nvcontainer.exe", "NVDisplay.Container.exe", "atiesrxx.exe", "atieclxx.exe", "amdfendrsr.exe", "RadeonSoftware.exe",
        "igfxEM.exe", "IntelGraphicsSoftware.Service.exe",
        "svchost.exe (audiosrv)", "svchost.exe (audioendpointbuilder)", "svchost.exe (mmcss)", "svchost.exe (tabletinputservice)",
        "svchost.exe (hidserv)", "svchost.exe (bthavctpsvc)", "svchost.exe (bthserv)", "svchost.exe (dhcp)", "svchost.exe (dnscache)",
        "svchost.exe (nsi)", "svchost.exe (nlasvc)", "svchost.exe (bfe)", "svchost.exe (mpssvc)", "svchost.exe (dcomlaunch)",
        "svchost.exe (rpcss)", "svchost.exe (rpceptmapper)", "svchost.exe (power)", "svchost.exe (brokerinfrastructure)",
        "svchost.exe (themes)", "svchost.exe (displayenhancementservice)", "svchost.exe (gamebarpresencewriter)",
    };

    public static bool IsHost(string name) => Hosts.Contains(name);

    /// <summary>Latency critical: a listed program, or a host running a listed target (svchost.exe (audiosrv)). Every driver host (WUDFHost) counts.</summary>
    public static bool IsCritical(string exe, string display) =>
        Critical.Contains(display) || (Critical.Contains(exe) && !Hosts.Contains(exe)) || exe.Equals("WUDFHost.exe", StringComparison.OrdinalIgnoreCase);
    public static bool IsShell(string name) => Shell.Contains(name);

    /// <summary>The identity key: lower-case so one program always gets one key.</summary>
    public static (string Key, string Display, bool Critical) Identify(ProcessInfo p)
    {
        p.IdentityAttempts++;
        string? target = null;
        string? cwd = null;
        bool host = Hosts.Contains(p.Name);
        if (host)
        {
            var command = ProcessInfo.CommandLineReader?.Invoke(p.Key);
            if (command is not null) target = Target(p.Name, Tokenize(command));
            if (target is not null && (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))) target = null;
            if (target is not null && !Path.IsPathFullyQualified(target)) cwd = IdentityProbe.WorkingDirectory(p.Key);
        }
        bool critical = false;
        // Services (also per-user service instances in the user's session) are started by services.exe; one cached snapshot
        // answers for every process identified in the same second, so ordinary processes never cause an enumeration.
        if (p.Key.Pid > 4 && (p.ParentName.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)))
        {
            p.ServiceCandidate = true;
            var found = IdentityProbe.Services(p.Key, out _);
            if (!ServiceTracker.Same(found, p.Services)) { p.Services = found; p.ServicesVersion++; }
        }
        if (p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var services = p.Services?.Select(s => s.Name.ToLowerInvariant()).ToArray();
            if (services is not null)
            {
                target = string.Join(';', services);
                foreach (string service in services) critical |= IsCritical(p.Name, $"svchost.exe ({service})");
            }
            else { target = null; critical = true; }
        }
        string display = target is null ? p.Name : $"{p.Name} ({ShortTarget(target)})";
        p.IdentityKnown = p.Path is not null && (!host || target is not null);
        if (host && target is not null && !Path.IsPathFullyQualified(target))
        {
            if (cwd is not null)
                target = target.Contains(' ') || target.StartsWith('-') ? cwd + "|" + target : Path.GetFullPath(target, cwd);
            else if (!p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) p.IdentityKnown = false;
        }
        string key = Versions().Replace((p.Path ?? p.Name).ToLowerInvariant(), "{version}");
        if (!host && !p.IdentityKnown) key += "|?";
        if (host) key += p.IdentityKnown ? "|" + Versions().Replace(target!.ToLowerInvariant(), "{version}") : "|?";
        return (key, display, critical || IsCritical(p.Name, display));
    }

    [GeneratedRegex(@"(?i)(?:app-)?[0-9]+(?:\.[0-9]+){1,4}(?=[\\/_-]|$)")]
    private static partial Regex Versions();
    private static string ShortTarget(string target) => target.Contains(';') ? target : FileName(target);
    public static bool CanClassify(string key, string display) => !key.EndsWith("|?", StringComparison.Ordinal) &&
        !display.EndsWith("(command)", StringComparison.OrdinalIgnoreCase) && !display.EndsWith("(shell)", StringComparison.OrdinalIgnoreCase) &&
        !display.EndsWith("(inline code)", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a host runs, from its arguments; null when nothing specific is found.</summary>
    internal static string? Target(string host, List<string> args)
    {
        string h = host.ToLowerInvariant();
        if (args.Count > 0) args.RemoveAt(0); // the exe itself
        switch (h)
        {
            case "python.exe" or "pythonw.exe" or "py.exe" or "pyw.exe":
                for (int i = 0; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a == "-m" && i + 1 < args.Count) return "-m " + args[i + 1];
                    if (a == "-c") return "inline code";
                    if (a is "-W" or "-X" or "-Q") { i++; continue; }
                    if (!a.StartsWith('-')) return Script(a);
                }
                return null;
            case "node.exe" or "bun.exe" or "deno.exe" or "electron.exe" or "ruby.exe" or "perl.exe" or "php.exe" or "rscript.exe" or "lua.exe" or "julia.exe":
                for (int i = 0; i < args.Count; i++)
                {
                    string a = args[i];
                    if (h == "deno.exe" && a is "run" or "task" or "serve") continue;
                    if (h == "bun.exe" && a is "run" or "x") continue;
                    if (a is "-r" or "--require" or "--import" or "--loader" or "-e" or "--eval") { if (a is "-e" or "--eval") return "inline code"; i++; continue; }
                    if (!a.StartsWith('-')) return Script(a);
                }
                return null;
            case "java.exe" or "javaw.exe":
                for (int i = 0; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a == "-jar" && i + 1 < args.Count) return args[i + 1];
                    if (a is "-cp" or "-classpath" or "--class-path" or "-p" or "--module-path" or "--add-opens" or "--add-exports") { i++; continue; }
                    if ((a == "-m" || a == "--module") && i + 1 < args.Count) return args[i + 1];
                    if (!a.StartsWith('-')) return a; // fully qualified main class
                }
                return null;
            case "dotnet.exe":
                for (int i = 0; i < args.Count; i++)
                    if (args[i] is "--project" or "-p" && i + 1 < args.Count) return args[i + 1];
                foreach (var a in args)
                {
                    if (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return a;
                    if (!a.StartsWith('-')) return "dotnet " + a; // build, watch, test, run ...
                }
                return null;
            case "pwsh.exe" or "powershell.exe":
                for (int i = 0; i < args.Count; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a is "-file" or "-f" && i + 1 < args.Count) return Script(args[i + 1]);
                    if (a is "-command" or "-c" or "-encodedcommand" or "-ec") return "command";
                }
                return args.Count == 0 || args.All(a => a.StartsWith('-')) ? "shell" : null;
            case "cmd.exe":
                for (int i = 0; i < args.Count; i++)
                    if (args[i].Equals("/c", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                        return "command";
                return "shell";
            case "wscript.exe" or "cscript.exe" or "mshta.exe":
                return args.Where(a => !a.StartsWith("//", StringComparison.Ordinal)).Select(Script).FirstOrDefault();
            case "rundll32.exe":
                return args.Count > 0 ? FileName(args[0].Split(',')[0]) : null;
            case "dllhost.exe":
                return args.FirstOrDefault(a => a.StartsWith("/processid:", StringComparison.OrdinalIgnoreCase)) is { } pid ? pid[11..] : null;
            case "svchost.exe":
                for (int i = 0; i < args.Count - 1; i++)
                    if (args[i] == "-s") return args[i + 1].ToLowerInvariant();
                for (int i = 0; i < args.Count - 1; i++)
                    if (args[i] == "-k") return args[i + 1].ToLowerInvariant();
                return null;
            case "wmiprvse.exe" or "taskhostw.exe" or "msiexec.exe" or "wudfhost.exe":
                return null;
        }
        return null;
    }

    /// <summary>A script's file name, with its folder when the name alone says nothing (main.py, index.js, run.ps1, ...).</summary>
    private static string Script(string path) => path;

    private static string FileName(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>Splits a Windows command line like CommandLineToArgvW (quotes, backslashes before quotes).</summary>
    internal static List<string> Tokenize(string commandLine)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) cur.Append('"');
                    else { quoted = !quoted; }
                    any = true;
                }
                else
                {
                    cur.Append('\\', n);
                    i--;
                }
                any = true;
                continue;
            }
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }
}
