using Corewright.Core.Engine;
using Corewright.Service.Windows;

namespace Corewright.Service;

/// <summary>
/// Keeps <see cref="ProcessInfo.Services"/> current (PM-46). A service reports its PID to the service manager shortly
/// after its process starts, so a service process found without services is looked up again in fresh snapshots (at most
/// one a second) until five snapshots have said "none". Every new snapshot, which new service processes cause anyway,
/// also refreshes the services of all known service processes, so a service that joins or leaves a shared svchost is
/// noticed the next time any service starts. No polling: without new service processes nothing is enumerated.
/// A changed service list re-runs the process identity on the next classification (svchost identities and their
/// critical-service protection depend on it); this runs before the classifier in the same tick.
/// Cost per tick without work: one pass over the process list reading a few fields.
/// </summary>
internal sealed class ServiceTracker
{
    private const int MaxChecks = 5;
    private int _seenVersion = -1;

    public void Reconcile(IReadOnlyList<ProcessInfo> processes)
    {
        int version = IdentityProbe.ServicesVersion;
        bool retry = false;
        for (int i = 0; i < processes.Count && !retry; i++)
        {
            var p = processes[i];
            retry = p.ServiceCandidate && p.Services is null && p.ServiceChecks < MaxChecks;
        }
        if (!retry && version == _seenVersion) return;
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (!p.ServiceCandidate) continue;
            bool unresolved = p.Services is null && p.ServiceChecks < MaxChecks;
            if (!unresolved && version == _seenVersion) continue;
            // Unresolved: ask for a fresh snapshot (throttled); a miss counts once per snapshot, not once per tick.
            var found = IdentityProbe.Services(p.Key, out bool known, refresh: unresolved);
            if (!known) continue;
            int seen = IdentityProbe.ServicesVersion;
            if (found is null && seen != p.ServiceSnapshot) p.ServiceChecks++;
            p.ServiceSnapshot = seen;
            if (Same(found, p.Services)) continue;
            p.Services = found;
            p.ServicesVersion++;
            // The identity (svchost key, critical-service protection) is derived from the services: identify again.
            p.IdentityKnown = false;
            p.IdentityAttempts = Math.Min(p.IdentityAttempts, 3);
        }
        // Only the generation this pass started from is acknowledged; a snapshot taken during the pass gets its own pass.
        _seenVersion = version;
    }

    public static bool Same(ServiceInfo[]? a, ServiceInfo[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
