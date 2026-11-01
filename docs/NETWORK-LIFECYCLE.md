# Network ownership and recovery

Network enforcement and per-process ETW share one machine-wide lease, held by an
exclusive `FileStream` at `%ProgramData%/Corewright/Network/network-owner.lock`.
Windows sharing rules exclude other processes and interactive/service sessions;
process exit releases the handle. The lock file is never deleted or replaced.
Unlike a mutex, a file handle can be closed by an async continuation without an
additional parked ownership thread. An instance that cannot acquire the lease
does not enforce network slots or stop the owner's ETW session. Slot failures
and monitoring-attempt failures explain the reason through Activity.

The lease is acquired only for demand or recovery. A clean start with no QoS
journal never initializes COM/WMI. The current owner holds its lease through
shutdown cleanup. Failed ETW/WFP session closes retain the handle and lease
until confirmed closure or process exit.

## ETW

The single session is named `Corewright-Network`. Under the lease, a session left
by a crash is stopped before `StartTrace`. A failed stop prevents a new start.
Stop/consumer-close results are checked; handles are discarded only on success
or confirmed absence. A failed cleanup is retried at most once per minute on
the existing sampler. No extra timer or polling loop is added.

An attempt failure suppresses further attempts for that demand/version. A new
demand transition or compiled-rule version permits a new attempt, with one
failure event for that attempt. Enabled network-usage rules retain demand even
without a dashboard; paused rules do not. Client demand keeps its 60-second grace.
Process counters allocate only for identities seen during active monitoring;
inactive fields are cleared once when monitoring stops.

The native control-buffer contract follows [ControlTraceW](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-controltracew).

## QoS and failed releases

ActiveStore policy names contain hashes of the machine-wide journal directory
and executable path. The journal records exact names before attempting a write,
flushes its temporary file to disk, and atomically replaces the previous file.
It removes an entry only after Windows confirms deletion or absence. No prefix
sweep is performed. Invalid or unreadable journals are preserved and block QoS.
Any installation can recover the previous owner's journal after acquiring the
machine lease, including when its configured service data directory differs.

Desired per-process claims and confirmed per-path enforcement are separate.
Failed applies restore the previous desired claim; failed releases keep the
desired release and confirmed enforcement. Ambiguous WMI failures invalidate
the confirmed backend cache and retain journal ownership. Process exit and
`Forget` transfer cleanup to the per-path record instead of losing it.

The existing sampler retries pending cleanup at most once per minute, including
while paused. The original failure is reported once. Each backend updates its
confirmed state only on success; unrelated successful slot writes cannot claim
that a failed backend changed. Shutdown attempts final cleanup, retains failed
QoS journal entries, and reports pending cleanup rather than claiming success.

## Upgrade and acceptance boundaries

Older builds using `Corewright-Network-<pid>` and unjournaled `Corewright-<hash>`
policies do not participate in this lease protocol. Stop those builds before
the first elevated test. Inspect and remove only verified-abandoned legacy
resources manually; this version deliberately does not sweep other owners.

Elevated WFP/QoS writes, crash recovery, concurrent-instance exclusion, active
ETW traffic and its CPU/memory cost require owner verification. Non-elevated
demand measures only the denied attempt. The external PM-4 report records exact
builds, matched idle windows, private working set, CPU time and the manual plan.
