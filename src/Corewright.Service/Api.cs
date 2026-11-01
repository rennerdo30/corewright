using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Corewright.Core.Engine;
using Corewright.Core.Import;
using Corewright.Core.Rules;

namespace Corewright.Service;

public sealed record StatusDto(string Version, string Mode, bool Elevated, bool Paused, int IntervalMs, int ProcessCount,
    int RulesVersion, int ActiveRules, int ForegroundPid);

public sealed record CoresDto(int LogicalProcessors, bool Hybrid, string PerformanceCores, string EfficiencyCores);

public sealed record SystemDto(double Cpu, ulong MemoryTotal, ulong MemoryUsed, double IoBytesPerSec, int ProcessCount,
    CoresDto Cores, long[] HistoryTime, float[] HistoryCpu, float[] HistoryMemory, float[] HistoryIo);

public sealed record AppliedSlotDto(string Slot, string Value, string RuleId, string RuleName);

public sealed record ProcessDto(int Pid, int? ParentPid, string Name, string? Path, double Cpu, long WorkingSet,
    long PrivateBytes, double IoBytesPerSec, int Threads, int Handles, string Priority, int Session, bool Protected,
    bool Foreground, AppliedSlotDto[]? Applied);

public sealed record ExplainDto(int Pid, string Name, ExplainEntry[] Rules);

public sealed record RuleErrorDto(string RuleId, string RuleName, string Message);

public sealed record RulesResultDto(bool Ok, RuleErrorDto[] Errors, int Version);

public sealed record EventsDto(long Last, EventDto[] Events);

public sealed record FactValueDto(string? Value);

public sealed record ForegroundDto(int Pid);

/// <summary>IniText null = read Process Lasso's config from its default locations on this PC.</summary>
public sealed record ImportRequestDto(string? IniText, bool Apply);

public sealed record ImportResponseDto(bool Applied, string? Source, Rule[] Rules, ImportNote[] Notes, int Replaced, RuleErrorDto[] Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(SystemDto))]
[JsonSerializable(typeof(ProcessDto[]))]
[JsonSerializable(typeof(ExplainDto))]
[JsonSerializable(typeof(RuleSet))]
[JsonSerializable(typeof(RulesResultDto))]
[JsonSerializable(typeof(EventsDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(FactValueDto))]
[JsonSerializable(typeof(ForegroundDto))]
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
                    m.Engine.Version, m.Engine.Rules.Rules.Length, m.Context.ForegroundPid));
        });

        api.MapGet("/system", () =>
        {
            var (t, c, mem, io) = m.History();
            var topo = m.Topology;
            lock (m.Sync)
                return TypedResults.Ok(new SystemDto(m.Sampler.SystemCpuPercent, m.Sampler.MemoryTotal, m.Sampler.MemoryUsed,
                    m.Sampler.TotalIoBytesPerSec, m.Sampler.Processes.Count,
                    new CoresDto(topo.LogicalCount, topo.IsHybrid, AffinityParser.Format(topo.PerformanceMask),
                        AffinityParser.Format(topo.EfficiencyMask)),
                    t, c, mem, io));
        });

        api.MapGet("/processes", () =>
        {
            lock (m.Sync)
            {
                var list = m.Sampler.Processes;
                var result = new ProcessDto[list.Count];
                for (int i = 0; i < list.Count; i++) result[i] = ToDto(m, list[i]);
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
            m.ReportForeground(body.Pid);
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

        api.MapGet("/events", (long? after) =>
        {
            var (last, events) = m.Events.Since(after ?? 0);
            return TypedResults.Ok(new EventsDto(last, events));
        });
    }

    private static ProcessDto ToDto(Monitor m, ProcessInfo p)
    {
        AppliedSlotDto[]? applied = null;
        if (m.Engine.HasApplied(p.Key))
            applied = [.. m.Engine.GetApplied(p.Key).Select(a =>
                new AppliedSlotDto(Slots.Name(a.Slot), Slots.FormatValue(a.Slot, a.Value), a.RuleId, a.RuleName))];

        int? parent = p.Parent is { } pk && m.Sampler.IsAlive(pk) ? pk.Pid : null;
        return new ProcessDto(p.Key.Pid, parent, p.Name, p.Path, Math.Round(p.CpuPercent, 2), p.WorkingSet, p.PrivateBytes,
            Math.Round(p.IoBytesPerSec), p.Threads, p.Handles, Slots.FormatBasePriority(p.BasePriority), p.SessionId,
            p.IsProtected, p.Key.Pid == m.Context.ForegroundPid, applied);
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
