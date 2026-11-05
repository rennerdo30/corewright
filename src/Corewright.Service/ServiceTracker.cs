using Corewright.Core.Engine;
using Corewright.Service.Windows;

namespace Corewright.Service;

/// <summary>
/// Keeps <see cref="ProcessInfo.Services"/> current (PM-46). A service reports its PID to the service manager shortly
/// after its process starts, so a service process found without services is looked up again on the next few ticks
/// (the lookup itself takes at most one snapshot a second). Every new snapshot, which new service processes cause anyway,
/// also refreshes the services of all known service processes, so a service that joins or leaves a shared svchost is
/// noticed the next time any service starts. No polling: without new service processes nothing is enumerated.
/// Cost per tick without work: one pass over the process list reading two fields.
/// </summary>
internal sealed class ServiceTracker
{
    private const int MaxChecks = 5;
    private int _seenVersion = -1;

    public void Reconcile(IReadOnlyList<ProcessInfo> processes)
    {
        bool retry = false;
        for (int i = 0; i < processes.Count && !retry; i++)
        {
            var p = processes[i];
            retry = p.ServiceCandidate && p.Services is null && p.ServiceChecks < MaxChecks;
        }
        if (!retry && IdentityProbe.ServicesVersion == _seenVersion) return;
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (!p.ServiceCandidate) continue;
            if (p.Services is null && p.ServiceChecks >= MaxChecks && IdentityProbe.ServicesVersion == _seenVersion) continue;
            var found = IdentityProbe.Services(p.Key, out bool known);
            if (!known) continue;
            if (found is null) p.ServiceChecks++;
            if (!Same(found, p.Services)) { p.Services = found; p.ServicesVersion++; }
        }
        _seenVersion = IdentityProbe.ServicesVersion;
    }

    public static bool Same(ServiceInfo[]? a, ServiceInfo[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
