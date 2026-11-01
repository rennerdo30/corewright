using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Corewright.Core.Autopilot;
using Corewright.Core.Engine;
using Corewright.Core.Import;
using Corewright.Core.Rules;
using Corewright.Service.Windows;

namespace Corewright.Service;

public sealed record StatusDto(string Version, string Mode, bool Elevated, bool Paused, int IntervalMs, int ProcessCount,
    int RulesVersion, int ActiveRules, int ForegroundPid, bool Autopilot, GameDto? Game, string DataDirectory);

public sealed record CoresDto(int LogicalProcessors, bool Hybrid, string PerformanceCores, string EfficiencyCores);

public sealed record SystemDto(double Cpu, ulong MemoryTotal, ulong MemoryUsed, double IoBytesPerSec, int ProcessCount,
    CoresDto Cores, long[] HistoryTime, float[] HistoryCpu, float[] HistoryMemory, float[] HistoryIo,
    double Gpu, long GpuMemory, GpuAdapterDto[] GpuAdapters, double NetReceive, double NetSend,
    float[] HistoryGpu, float[] HistoryNetReceive, float[] HistoryNetSend, float[] ThreadUsage);

/// <summary>Optional per-process values; only the groups a client asks for are filled in.</summary>
public sealed record ProcessExtDto(
    long? Started, long? CpuTimeMs, long? UserTimeMs, long? KernelTimeMs, long? Cycles,
    long? PrivateWorkingSet, long? SharedWorkingSet, long? PeakWorkingSet, long? PeakPrivateBytes, long? VirtualSize, long? PeakVirtualSize,
    long? PagedPool, long? NonPagedPool, long? PageFaults, long? HardFaults,
    long? ReadOperations, long? WriteOperations, long? OtherOperations, long? ReadBytes, long? WriteBytes, long? OtherBytes,
    int? BasePriority, double? Gpu,
    string? Affinity, string? IoPriority, string? GpuPriority, string? MemoryPriority, string? EfficiencyMode, long? WorkingSetLimitMb, long? MemoryLimitMb,
    int? GdiObjects, int? UserObjects, string? User, bool? Elevated, int? Bits, string? CommandLine);

public sealed record AppliedSlotDto(string Slot, string Value, string RuleId, string RuleName);

public sealed record ProcessDto(int Pid, int? ParentPid, string Name, string? Path, double Cpu, long WorkingSet,
    long PrivateBytes, double IoBytesPerSec, int Threads, int Handles, string Priority, int Session, bool Protected,
    bool Foreground, AppliedSlotDto[]? Applied, ProcessExtDto? Ext);

public sealed record ExplainDto(int Pid, string Name, ExplainEntry[] Rules);

public sealed record RuleErrorDto(string RuleId, string RuleName, string Message);

public sealed record RulesResultDto(bool Ok, RuleErrorDto[] Errors, int Version);

public sealed record EventsDto(long Last, EventDto[] Events);

public sealed record FactValueDto(string? Value);

public sealed record ForegroundDto(int Pid, bool Fullscreen = false);

public sealed record GameDto(int Pid, string Name, string? Reason);

public sealed record SettingsResultDto(bool Ok, RuleErrorDto[] Errors, AppSettings Settings);

/// <summary>IniText null = read Process Lasso's config from its default locations on this PC.</summary>
public sealed record ImportRequestDto(string? IniText, bool Apply);

public sealed record ImportResponseDto(bool Applied, string? Source, Rule[] Rules, ImportNote[] Notes, int Replaced, RuleErrorDto[] Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(SystemDto))]
[JsonSerializable(typeof(ProcessDto[]))]
[JsonSerializable(typeof(TopologyDto))]
[JsonSerializable(typeof(ExplainDto))]
[JsonSerializable(typeof(RuleSet))]
[JsonSerializable(typeof(RulesResultDto))]
[JsonSerializable(typeof(EventsDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(FactValueDto))]
[JsonSerializable(typeof(ForegroundDto))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SettingsResultDto))]
[JsonSerializable(typeof(ImportRequestDto))]
[JsonSerializable(typeof(ImportResponseDto))]
internal sealed partial class ApiJson : JsonSerializerContext;

internal static class Api
{
    public static void Map(WebApplication app, Monitor m)
    {
        var api = app.MapGroup("/api/v1");
        string version = typeof(Api).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        api.MapGet("/status", () =>
        {
            lock (m.Sync)
                return TypedResults.Ok(new StatusDto(version, m.IsWindowsService ? "service" : "console",
                    Environment.IsPrivilegedProcess, m.Engine.Paused, m.IntervalMs, m.Sampler.Processes.Count,
                    m.Engine.Version, m.Engine.Rules.Rules.Count(r => !r.Id.StartsWith(AutopilotRules.IdPrefix, StringComparison.Ordinal)),
                    m.Context.ForegroundPid, m.Settings.Autopilot.Enabled,
                    m.Game.Game is { } g ? new GameDto(g.Pid, m.Game.GameName ?? "", m.Game.Reason) : null, m.DataDirectory));
        });

        api.MapGet("/system", () =>
        {
            var (t, c, mem, io, gpu, rx, tx) = m.History();
            var topo = m.Topology;
            lock (m.Sync)
                return TypedResults.Ok(new SystemDto(m.Sampler.SystemCpuPercent, m.Sampler.MemoryTotal, m.Sampler.MemoryUsed,
                    m.Sampler.TotalIoBytesPerSec, m.Sampler.Processes.Count,
                    new CoresDto(topo.LogicalCount, topo.IsHybrid, AffinityParser.Format(topo.PerformanceMask),
                        AffinityParser.Format(topo.EfficiencyMask)),
                    t, c, mem, io,
                    m.Gpu?.Utilization ?? 0, m.Gpu?.DedicatedUsed ?? 0, m.Gpu?.Adapters ?? [],
                    m.Network.ReceiveBytesPerSec, m.Network.SendBytesPerSec, gpu, rx, tx, [.. m.Cores.Usage]));
        });

        api.MapGet("/topology", IResult () => m.CpuLayout is { } layout ? TypedResults.Ok(layout) : TypedResults.NotFound());

        // include = comma list of: ext (sampler details), gpu, settings, identity, cmdline, gui.
        api.MapGet("/processes", (string? include) =>
        {
            var inc = (include ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groups = DetailGroups.None;
            if (inc.Contains("settings")) groups |= DetailGroups.Settings;
            if (inc.Contains("identity")) groups |= DetailGroups.Identity;
            if (inc.Contains("cmdline")) groups |= DetailGroups.CommandLine;
            if (inc.Contains("gui")) groups |= DetailGroups.Gui;
            if (groups != DetailGroups.None)
            {
                // Handle reads happen outside the engine lock so the tick is never delayed by a client.
                ProcessKey[] keys;
                lock (m.Sync) keys = [.. m.Sampler.Processes.Select(p => p.Key)];
                m.Details.Refresh(keys, groups);
            }
            bool ext = inc.Count > 0;
            lock (m.Sync)
            {
                var list = m.Sampler.Processes;
                var result = new ProcessDto[list.Count];
                for (int i = 0; i < list.Count; i++)
                    result[i] = ToDto(m, list[i], ext, inc.Contains("ext"), inc.Contains("gpu"), groups);
                return TypedResults.Ok(result);
            }
        });

        api.MapGet("/processes/{pid:int}/explain", IResult (int pid) =>
        {
            lock (m.Sync)
            {
                if (!m.Sampler.TryGet(pid, out var p)) return TypedResults.NotFound();
                return TypedResults.Ok(new ExplainDto(pid, p.Name, [.. m.Engine.Explain(p, m.Context)]));
            }
        });

        api.MapGet("/rules", () =>
        {
            lock (m.Sync) return TypedResults.Ok(m.RuleSet);
        });

        api.MapPut("/rules", IResult (RuleSet set) =>
        {
            var errors = m.ReplaceRules(set);
            var dto = new RulesResultDto(errors.Count == 0, [.. errors.Select(ToDto)], m.Engine.Version);
            return errors.Count == 0 ? TypedResults.Ok(dto) : TypedResults.BadRequest(dto);
        });

        api.MapPost("/rules/validate", (RuleSet set) =>
        {
            var errors = new List<RuleError>();
            RuleCompiler.Compile(set, m.Topology, errors);
            return TypedResults.Ok(new RulesResultDto(errors.Count == 0, [.. errors.Select(ToDto)], m.Engine.Version));
        });

        // Console mode only: the tray's "Exit Corewright". An installed service is controlled by Windows, not by clients.
        api.MapPost("/app/shutdown", IResult () =>
        {
            if (m.IsWindowsService) return TypedResults.Conflict();
            _ = Task.Delay(200).ContinueWith(_ => app.Lifetime.StopApplication(), TaskScheduler.Default);
            return TypedResults.Accepted((string?)null);
        });

        api.MapPost("/engine/pause", () => { m.Pause(); return TypedResults.NoContent(); });
        api.MapPost("/engine/resume", () => { m.Resume(); return TypedResults.NoContent(); });

        api.MapGet("/facts", () =>
        {
            lock (m.Sync) return TypedResults.Ok(new Dictionary<string, string>(m.Facts));
        });

        api.MapPut("/facts/{key}", IResult (string key, FactValueDto body) =>
        {
            if (key.Length is 0 or > 128) return TypedResults.BadRequest();
            m.SetFact(key, body.Value);
            return TypedResults.NoContent();
        });

        api.MapDelete("/facts/{key}", (string key) => { m.SetFact(key, null); return TypedResults.NoContent(); });

        api.MapPut("/session/foreground", (ForegroundDto body) =>
        {
            m.ReportForeground(body.Pid, body.Fullscreen);
            return TypedResults.NoContent();
        });

        api.MapPost("/import/process-lasso", IResult (ImportRequestDto body) =>
        {
            string? text = body.IniText;
            string? source = text is null ? null : "uploaded file";
            if (text is null)
            {
                source = ProcessLassoImporter.DefaultConfigPaths().FirstOrDefault(File.Exists);
                if (source is null)
                    return TypedResults.NotFound();
                text = ProcessLassoImporter.ReadConfig(source);
            }

            var result = ProcessLassoImporter.Convert(text, m.Topology, source);
            if (!body.Apply)
                return TypedResults.Ok(new ImportResponseDto(false, source, [.. result.Rules], [.. result.Notes], 0, []));

            // Re-importing replaces the previous import; the user's own rules stay untouched.
            RuleSet current;
            lock (m.Sync) current = m.RuleSet;
            int replaced = current.Rules.Count(r => r.Id.StartsWith(ProcessLassoImporter.IdPrefix, StringComparison.Ordinal));
            var next = new RuleSet
            {
                Version = current.Version,
                Rules = [.. current.Rules.Where(r => !r.Id.StartsWith(ProcessLassoImporter.IdPrefix, StringComparison.Ordinal)), .. result.Rules],
            };
            var errors = m.ReplaceRules(next);
            var dto = new ImportResponseDto(errors.Count == 0, source, [.. result.Rules], [.. result.Notes], replaced, [.. errors.Select(ToDto)]);
            return errors.Count == 0 ? TypedResults.Ok(dto) : TypedResults.BadRequest(dto);
        });

        api.MapGet("/settings", () =>
        {
            lock (m.Sync) return TypedResults.Ok(m.Settings);
        });

        api.MapPut("/settings", IResult (AppSettings settings) =>
        {
            var errors = m.ReplaceSettings(settings);
            var dto = new SettingsResultDto(errors.Count == 0, [.. errors.Select(ToDto)], m.Settings);
            return errors.Count == 0 ? TypedResults.Ok(dto) : TypedResults.BadRequest(dto);
        });

        api.MapGet("/events", (long? after) =>
        {
            var (last, events) = m.Events.Since(after ?? 0);
            return TypedResults.Ok(new EventsDto(last, events));
        });
    }

    private static ProcessDto ToDto(Monitor m, ProcessInfo p, bool ext = false, bool sampler = false, bool gpu = false,
        DetailGroups groups = DetailGroups.None)
    {
        AppliedSlotDto[]? applied = null;
        if (m.Engine.HasApplied(p.Key))
            applied = [.. m.Engine.GetApplied(p.Key).Select(a =>
                new AppliedSlotDto(Slots.Name(a.Slot), Slots.FormatValue(a.Slot, a.Value), a.RuleId, a.RuleName))];

        int? parent = p.Parent is { } pk && m.Sampler.IsAlive(pk) ? pk.Pid : null;
        return new ProcessDto(p.Key.Pid, parent, p.Name, p.Path, Math.Round(p.CpuPercent, 2), p.WorkingSet, p.PrivateBytes,
            Math.Round(p.IoBytesPerSec), p.Threads, p.Handles, Slots.FormatBasePriority(p.BasePriority), p.SessionId,
            p.IsProtected, p.Key.Pid == m.Context.ForegroundPid, applied, ext ? Ext(m, p, sampler, gpu, groups) : null);
    }

    private static ProcessExtDto Ext(Monitor m, ProcessInfo p, bool sampler, bool gpu, DetailGroups groups)
    {
        var d = groups == DetailGroups.None ? null : m.Details.Get(p.Key);
        bool settings = groups.HasFlag(DetailGroups.Settings);
        static string? Fmt(Slot slot, long? v) => v is long x ? Slots.FormatValue(slot, x) : null;
        string? affinity = settings && d?.Affinity is long mask
            ? ((ulong)mask & m.Topology.AllMask) == m.Topology.AllMask ? "all" : AffinityParser.Format((ulong)mask)
            : null;
        long? memLimit = null;
        if (settings)
        {
            memLimit = 0;
            foreach (var a in m.Engine.GetApplied(p.Key))
                if (a.Slot == Slot.MemoryLimit) memLimit = a.Value;
        }
        long? Ms(long t) => sampler ? t / 10_000 : null;
        long? S(long v) => sampler ? v : null;
        return new ProcessExtDto(
            sampler && p.Key.CreateTime > 0 ? (p.Key.CreateTime - 116444736000000000L) / 10_000 : null,
            Ms(p.UserTime + p.KernelTime), Ms(p.UserTime), Ms(p.KernelTime), S(p.CycleTime),
            S(p.PrivateWorkingSet), S(Math.Max(0, p.WorkingSet - p.PrivateWorkingSet)), S(p.PeakWorkingSet),
            S(p.PeakPrivateBytes), S(p.VirtualSize), S(p.PeakVirtualSize),
            S(p.PagedPool), S(p.NonPagedPool), S(p.PageFaults), S(p.HardFaults),
            S(p.ReadOperations), S(p.WriteOperations), S(p.OtherOperations), S(p.ReadBytes), S(p.WriteBytes), S(p.OtherBytes),
            sampler ? p.BasePriority : null,
            gpu ? Math.Round(m.Gpu?.ByProcess.GetValueOrDefault(p.Key.Pid) ?? 0, 1) : null,
            affinity, Fmt(Slot.IoPriority, d?.IoPriority), Fmt(Slot.GpuPriority, d?.GpuPriority), Fmt(Slot.MemoryPriority, d?.MemoryPriority),
            Fmt(Slot.EfficiencyMode, d?.Efficiency), settings ? d?.WorkingSetLimitMb : null, memLimit,
            d?.GdiObjects, d?.UserObjects, d?.UserName, d?.Elevated, d?.Bits, d?.CommandLine);
    }

    private static RuleErrorDto ToDto(RuleError e) => new(e.RuleId, e.RuleName, e.Message);

    /// <summary>Loopback-only host check (DNS rebinding) and bearer-token check for /api.</summary>
    public static Func<HttpContext, Func<Task>, Task> Guard(Monitor m, byte[] token, int port) => async (ctx, next) =>
    {
        var host = ctx.Request.Host;
        if (host.Host is not ("127.0.0.1" or "localhost") || (host.Port is int p && p != port))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            string auth = ctx.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            bool ok = auth.StartsWith(prefix, StringComparison.Ordinal) &&
                      CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[prefix.Length..]), token);
            if (!ok)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            m.TouchClient();
        }
        await next();
    };
}
