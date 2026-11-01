using Corewright.Core.Engine;
using Corewright.Core.Rules;

namespace Corewright.Service.Windows;

/// <summary>Desired claims and confirmed enforcement have separate lifetimes. Failed releases
/// retain pending cleanup ownership, including after process exit, pause and shutdown.</summary>
internal sealed class NetworkPolicies : IDisposable
{
    private const int Block = 1, Qos = 2;
    private sealed class Claim(string path) { public readonly string Path = path; public bool Block; public long Upload; public long Dscp = -1; }
    private sealed class Enforcement
    {
        public bool Blocked;
        public long Upload, Priority = -1, RetryAfter;
        public int Pending, Reported;
        public string? BlockError, QosError;
    }
    public NetworkOwnership Ownership { get; } = new();
    private readonly Dictionary<ProcessKey, Claim> _claims = [];
    private readonly Dictionary<string, Enforcement> _effective = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _finished = [];
    private readonly List<ProcessKey> _released = [];
    private readonly NetworkBlocker _blocker = new();
    private QosPolicies? _qos;
    private string _directory = "";
    private IEngineEventSink? _events;
    private string? _qosStartupError;
    private long _startupRetry, _nextRetry = long.MaxValue;
    public static bool IsSlot(Slot slot) => slot is Slot.NetworkBlocked or Slot.UploadLimit or Slot.NetworkPriority;
    public static long Original(Slot slot) => slot == Slot.NetworkPriority ? -1 : 0;
    public void Start(IEngineEventSink events)
    {
        _events = events; _directory = NetworkOwnership.StateDirectory;
        // Machine-wide journal: a different installation/data-folder can recover the last owner's crash.
        if (!Environment.IsPrivilegedProcess || !QosPolicies.HasJournal(_directory)) return;
        if (!CleanupStartup()) Report(_qosStartupError!);
    }
    private bool CleanupStartup()
    {
        try
        {
            if (!Ownership.TryAcquire(out var error)) throw new InvalidOperationException(error);
            (_qos ??= new(_directory)).Cleanup();
            _qosStartupError = null;
            return true;
        }
        catch (Exception e)
        {
            _qosStartupError = "QoS startup cleanup failed: " + e.Message;
            _startupRetry = Environment.TickCount64 + 60_000;
            _nextRetry = Math.Min(_nextRetry, _startupRetry);
            return false;
        }
    }
    public bool Has(ProcessKey key) => _claims.ContainsKey(key);
    public (bool Blocked, double Upload, long Priority) Effective(string? path) =>
        path is not null && _effective.TryGetValue(path, out var v)
            ? (v.Blocked, v.Upload / 1_000_000.0, v.Priority) : (false, 0, -1);
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
        if (value != Original(slot) && !Ownership.TryAcquire(out error)) return false;
        if (slot != Slot.NetworkBlocked && _qosStartupError is not null) { error = _qosStartupError; return false; }
        if (!_claims.TryGetValue(key, out var claim))
        {
            if (value == Original(slot)) return true;
            if (string.IsNullOrEmpty(path)) { error = "executable path unavailable"; return false; }
            _claims.Add(key, claim = new Claim(path));
        }
        if (!_effective.TryGetValue(claim.Path, out var state)) _effective.Add(claim.Path, state = new());
        long old = slot == Slot.NetworkBlocked ? (claim.Block ? 1 : 0) : slot == Slot.UploadLimit ? claim.Upload : claim.Dscp;
        Assign(claim, slot, value);
        int backend = slot == Slot.NetworkBlocked ? Block : Qos;
        bool ok;
        if ((state.Pending & backend) != 0 && Environment.TickCount64 < state.RetryAfter)
        { error = backend == Block ? state.BlockError : state.QosError; ok = false; }
        else ok = Sync(claim.Path, state, backend, out error);
        if (!ok)
        {
            // Failed applies are not desired. Failed releases MUST remain desired until confirmed.
            if (value != Original(slot)) Assign(claim, slot, old);
            state.Reported |= backend; // The engine reports this write; retries stay silent.
        }
        Prune(claim.Path, state);
        return ok;
    }
    private static void Assign(Claim c, Slot slot, long value)
    { if (slot == Slot.NetworkBlocked) c.Block = value != 0; else if (slot == Slot.UploadLimit) c.Upload = value; else c.Dscp = value; }
    private bool Sync(string path, Enforcement state, int backends, out string? error)
    {
        error = null;
        var want = Compute(path);
        if ((backends & Block) != 0)
        {
            try { _blocker.Set(path, want.Blocked); state.Blocked = want.Blocked; state.Pending &= ~Block; state.Reported &= ~Block; }
            catch (Exception e) { state.Pending |= Block; error = state.BlockError = e.Message; }
        }
        if ((backends & Qos) != 0)
        {
            try
            {
                if (_qos is not null || want.Upload > 0 || want.Priority >= 0)
                    (_qos ??= new(_directory)).Set(path, want.Upload, want.Priority);
                state.Upload = want.Upload; state.Priority = want.Priority; state.Pending &= ~Qos; state.Reported &= ~Qos;
            }
            catch (Exception e)
            {
                state.Pending |= Qos;
                state.QosError = "QoS operation failed; cleanup remains owned and journaled: " + e.Message;
                error ??= state.QosError;
            }
        }
        if ((state.Pending & backends) != 0)
        {
            state.RetryAfter = Environment.TickCount64 + 60_000;
            _nextRetry = Math.Min(_nextRetry, state.RetryAfter);
        }
        return (state.Pending & backends) == 0;
    }
    public void Forget(ProcessKey key)
    {
        if (!_claims.Remove(key, out var c)) return;
        if (!_effective.TryGetValue(c.Path, out var state)) return;
        int backends = Block | Qos;
        // Sibling exits and pause must not bypass a failed backend's retry deadline.
        if (Environment.TickCount64 < state.RetryAfter) backends &= ~state.Pending;
        Sync(c.Path, state, backends, out _);
        ReportPending(state);
        Prune(c.Path, state);
    }
    private void ReportPending(Enforcement state)
    {
        if ((state.Pending & Block & ~state.Reported) != 0) Report("block release pending: " + state.BlockError);
        if ((state.Pending & Qos & ~state.Reported) != 0) Report("QoS release pending: " + state.QosError);
        state.Reported |= state.Pending;
    }
    public void RetryPending(long now)
    {
        if (now < _nextRetry) return;
        _nextRetry = long.MaxValue;
        if (_qosStartupError is not null)
        {
            if (now >= _startupRetry) CleanupStartup();
            else _nextRetry = _startupRetry;
        }
        foreach (var (path, state) in _effective)
        {
            if (state.Pending == 0) continue;
            if (now >= state.RetryAfter)
            {
                Sync(path, state, state.Pending, out _);
                ReportPending(state);
                if (state.Pending == 0) _finished.Add(path);
            }
            else _nextRetry = Math.Min(_nextRetry, state.RetryAfter);
        }
        foreach (string path in _finished) Prune(path, _effective[path]);
        _finished.Clear();
    }
    private void Prune(string path, Enforcement state)
    {
        if (state.Pending != 0) return;
        foreach (var (key, c) in _claims)
            if (string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase) && !c.Block && c.Upload == 0 && c.Dscp < 0) _released.Add(key);
        foreach (var key in _released) _claims.Remove(key);
        _released.Clear();
        if (!state.Blocked && state.Upload == 0 && state.Priority < 0) _effective.Remove(path);
    }
    private void Report(string message) => _events?.Emit(new(EngineEventKind.Failed, 0, "", null, "network", null, message));
    public bool SessionStopped => _blocker.Closed;
    public void Dispose()
    {
        _claims.Clear(); // Empty desired state; confirmed state survives failed cleanup.
        try
        {
            _blocker.Dispose();
            foreach (var state in _effective.Values) { state.Blocked = false; state.Pending &= ~Block; }
        }
        catch (Exception e) { Report("WFP shutdown cleanup failed; session retained until process exit: " + e.Message); }
        try
        {
            _qos?.RemoveOwned();
            foreach (var state in _effective.Values) { state.Upload = 0; state.Priority = -1; state.Pending &= ~Qos; }
        }
        catch (Exception e)
        {
            bool reported = _qosStartupError is not null;
            foreach (var state in _effective.Values) reported |= (state.Reported & Qos) != 0;
            if (!reported) Report("QoS shutdown cleanup pending in journal: " + e.Message);
        }
    }
}
