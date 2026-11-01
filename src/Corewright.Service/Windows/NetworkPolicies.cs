using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service.Windows;

/// <summary>Claims are per process identity; Windows enforcement is per executable path.</summary>
internal sealed class NetworkPolicies : IDisposable
{
    private sealed class Claim(string path) { public readonly string Path = path; public bool Block; public long Upload; public long Dscp = -1; }
    private readonly Dictionary<ProcessKey, Claim> _claims = [];
    private readonly Dictionary<string, (bool Blocked, long Upload, long Priority)> _effective = new(StringComparer.OrdinalIgnoreCase);
    private readonly NetworkBlocker _blocker = new();
    private readonly QosPolicies _qos = new();
    private IEngineEventSink? _events;
    public static bool IsSlot(Slot slot) => slot is Slot.NetworkBlocked or Slot.UploadLimit or Slot.NetworkPriority;
    public static long Original(Slot slot) => slot == Slot.NetworkPriority ? -1 : 0;
    public void Start(IEngineEventSink events)
    {
        _events = events;
        if (!Environment.IsPrivilegedProcess) return;
        try { _qos.Cleanup(); }
        catch (Exception e) { Report("QoS startup cleanup failed: " + e.Message); }
    }
    public bool Has(ProcessKey key) => _claims.ContainsKey(key);
    public (bool Blocked, double Upload, long Priority) Effective(string? path)
    { var v = path is null ? (false, 0L, -1L) : _effective.GetValueOrDefault(path, (false, 0L, -1L)); return (v.Item1, v.Item2 / 1_000_000.0, v.Item3); }
    private (bool Blocked, long Upload, long Priority) Compute(string path)
    {
        bool block = false; long upload = 0, dscp = -1;
        foreach (var c in _claims.Values)
            if (string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                block |= c.Block;
                if (c.Upload > 0) upload = upload == 0 ? c.Upload : Math.Min(upload, c.Upload);
                dscp = Math.Max(dscp, c.Dscp);
            }
        return (block, upload, dscp);
    }
    public bool Write(ProcessKey key, string? path, Slot slot, long value, out string? error)
    {
        error = null;
        if (!_claims.TryGetValue(key, out var claim))
        {
            if (value == Original(slot)) return true;
            if (!Environment.IsPrivilegedProcess) { error = "needs administrator (needs the installed service)"; return false; }
            if (string.IsNullOrEmpty(path)) { error = "executable path unavailable"; return false; }
            claim = new Claim(path);
            _claims.Add(key, claim);
        }
        long old = slot == Slot.NetworkBlocked ? (claim.Block ? 1 : 0) : slot == Slot.UploadLimit ? claim.Upload : claim.Dscp;
        void Assign(long v) { if (slot == Slot.NetworkBlocked) claim.Block = v != 0; else if (slot == Slot.UploadLimit) claim.Upload = v; else claim.Dscp = v; }
        Assign(value);
        try
        {
            var effective = Compute(claim.Path);
            if (slot == Slot.NetworkBlocked) _blocker.Set(claim.Path, effective.Blocked);
            else _qos.Set(claim.Path, effective.Upload, effective.Priority);
            _effective[claim.Path] = effective;
            return true;
        }
        catch (Exception e) { Assign(old); error = e.Message; return false; }
        finally { if (!claim.Block && claim.Upload == 0 && claim.Dscp < 0) _claims.Remove(key); }
    }
    public void Forget(ProcessKey key)
    {
        if (!_claims.Remove(key, out var c)) return;
        var effective = Compute(c.Path);
        try { _blocker.Set(c.Path, effective.Blocked); }
        catch (Exception e) { Report("block release failed: " + e.Message); }
        try { _qos.Set(c.Path, effective.Upload, effective.Priority); }
        catch (Exception e) { Report("QoS release failed: " + e.Message); }
        if (effective == (false, 0L, -1L)) _effective.Remove(c.Path); else _effective[c.Path] = effective;
    }
    private void Report(string message) => _events?.Emit(new(EngineEventKind.Failed, 0, "", null, null, null, message));
    public void Dispose()
    {
        _blocker.Dispose();
        try { _qos.RemoveOwned(); } catch (Exception e) { Report("QoS shutdown cleanup failed: " + e.Message); }
        _claims.Clear();
        _effective.Clear();
    }
}
