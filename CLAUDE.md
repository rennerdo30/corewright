# Corewright: repository rules

Read `E:/Development/AGENTS.md` and `E:/Development/general-agent-rules.md` first; this file adds the local rules. Codex: `AGENTS.md` here points to this file.

- Always write `E:` paths; `D:` is the same drive (details in the shared `AGENTS.md`).
- Branch limit: an active branch has a commit in the last 24 hours; up to 10 active branches normal, 11 to 20 only with a recorded reason, 20 hard maximum.

## What this is

A Windows process manager in the spirit of Process Lasso, with a much stronger rule engine and a local HTTP API that other applications integrate with. A SYSTEM Windows service applies per-process CPU priority, affinity, I/O priority, GPU priority, memory priority, efficiency mode (EcoQoS) and memory caps from rules; a tray app and a WebView2 dashboard manage it.

## Hard constraints (owner, 2026-11-02)

**The app itself must be high-performance: a very low memory footprint and practically zero CPU usage.** These are hard constraints, not goals. Every change is judged against them; a feature that cannot meet them is redesigned or dropped.

Working budgets (proposed 2026-11-02 to make the constraint checkable; the owner may tighten them):

| Process | When | CPU | Memory (private working set) |
|---|---|---|---|
| Service | idle, dashboard closed | ≤ 0.1 % of total CPU averaged over 60 s; at most one wake-up per 2 s | ≤ 30 MB (NativeAOT publish) |
| Service | dashboard open | ≤ 0.5 % of total CPU | ≤ 40 MB |
| Tray | always | 0 % idle (purely event-driven, no timers, no polling) | ≤ 10 MB (NativeAOT publish) |
| Dashboard | only while its window is open | suspended and polling stopped when minimized | exits completely on close |

Design rules that follow from this:

- **No polling where an event exists.** The tray uses `SetWinEventHook` for foreground changes and has no timers. The service has exactly one loop (the sampler tick): 1 s while an API client is active, 2 s otherwise. ETW process start events (next phase) replace the 2 s new-process detection, not add to it.
- **No steady-state allocations per tick.** Reuse buffers, lists and dictionaries; allocate only for new processes, events and API responses. Process names and paths are read once per process lifetime.
- **Cheap rule evaluation.** Rules that only use static fields (name, path, parent, pid, session) are evaluated once per process and cached; only dynamic rules run every tick, and a rule whose slots are already claimed is not evaluated at all.
- **NativeAOT-compatible code** in Core, Service and Tray: System.Text.Json source generation only (no reflection serialization), `LibraryImport` instead of `DllImport`, no `Expression.Compile`, no runtime code generation. Workstation, non-concurrent GC with `ConserveMemory`; invariant globalization.
- **No heavy dependencies.** Each new package (NuGet or npm) needs a reason that outweighs its memory, CPU and bundle cost. The UI has three runtime dependencies (React, TanStack Virtual, uPlot); keep it that way unless there is a clear need.
- **The UI only costs while visible:** it stops polling when `document.hidden`, the dashboard suspends WebView2 when minimized, and closing the window ends the process.
- Measure before claiming a budget is met: record private working set and CPU time of the published binaries over a fixed idle window, and state the build and the numbers. Source review is not a performance measurement.

## Tests (owner, 2026-11-02)

**No unit-test project for now.** "Don't fall into the unit test trap; we do that later, focused. Not 100,000s of tests for nothing." Verify by compiling and by a short manual smoke run of the service and API. When tests come, they are few and target the rule engine's real invariants (slot resolution, revert, PID reuse); each must be shown failing on broken code first.

## Game mode and CPU topology (owner, 2026-11-02)

- **Game detection** is a built-in system signal. Rules can say "while a game runs, every other process goes to low priority", "the game gets high priority", or "everything else moves to CCD1 and the game stays on CCD0". Signals, cheapest first, evaluated only on foreground change: a user list of games; Windows' own game list (`HKCU\System\GameConfigStore\Children`, `MatchedExeFullPath`); launcher library paths (Steam `steamapps\common`, Epic, GOG, `XboxGames`); full-screen or borderless foreground window (`SHQueryUserNotificationState`, window rect equals monitor rect). The service exposes it as rule fields (`isGame`) and built-in facts (`game.active`, `game.name`, `game.pid`).
- **CCD awareness:** CCDs come from the L3 cache groups (`GetLogicalProcessorInformationEx`, `RelationCache` level 3); the larger L3 marks the V-Cache CCD on X3D parts. Affinity tokens: `ccd0`, `ccd1`, `vcache-ccd`, `frequency-ccd`, `game-ccd`, `non-game-ccd`.
- **Strongest cores like Ryzen Master:** rank logical processors by the CPPC "Maximum performance percentage" in Kernel-Processor-Power event 55 (read once at start; confirmed on the owner's Ryzen 9 9950X3D2: values 131 to 135), with the hybrid `EfficiencyClass` as fallback. Affinity tokens: `best-cores:<n>`, `best-ccd`.

## Integration API (owner, 2026-11-02)

**A normal local HTTP API only: no command-line interface, no named pipes.** Kestrel listens on `127.0.0.1` only (default port 47830, `appsettings.json`). Every `/api` call needs `Authorization: Bearer <token>`; the service generates a new token at every start and writes it with the URL to `endpoint.json` in its data directory (console mode: `%LOCALAPPDATA%\Corewright`, service mode: `%ProgramData%\Corewright`). Requests whose `Host` is not loopback are rejected (DNS rebinding). The dashboard UI uses the same API as third-party connectors; never add a private side channel for it. Never print or log the token.

Endpoints (all under `/api/v1`): `status`, `system` (with history), `processes`, `processes/{pid}/explain`, `rules` (GET/PUT, PUT validates and rejects on any error), `rules/validate`, `engine/pause`, `engine/resume`, `facts` and `facts/{key}` (connectors push context such as `obs.recording=true`; rules read it with the `fact` field), `session/foreground` (the tray reports the foreground process because a service in session 0 cannot see it), `events?after=<seq>`.

## Controlled resources

| Slot | Mechanism | Notes |
|---|---|---|
| `cpuPriority` | `SetPriorityClass` | Realtime only with the rule's `allowRealtime`. Re-asserted up to 3 times if the process changes it back, then left alone with a drift event. |
| `affinity` | `SetProcessAffinityMask` | `all`, `p-cores`, `e-cores` (from CPU Sets `EfficiencyClass`), or a list such as `0-7,16`. Processor group 0 only (≤ 64 logical processors); CPU Sets are the next step. |
| `ioPriority` | `NtSetInformationProcess(ProcessIoPriority)` | veryLow, low, normal, high. |
| `gpuPriority` | `D3DKMTSetProcessSchedulingPriorityClass` (gdi32) | Effect depends on the driver and hardware-accelerated GPU scheduling. Realtime only with `allowRealtime`. |
| `memoryPriority` | `SetProcessInformation(ProcessMemoryPriority)` | veryLow to normal. |
| `efficiencyMode` | `SetProcessInformation(ProcessPowerThrottling)` | EcoQoS on or off; restore gives control back to Windows. |
| `memoryLimitMb` | Job object `JOB_OBJECT_LIMIT_PROCESS_MEMORY` (owner requirement, 2026-11-02) | **Hard** commit cap: allocations above it fail and the app may crash. Child processes started later join the job and get the same per-process cap. A process cannot leave a job, so revert removes the limit and keeps the empty job. |
| `workingSetLimitMb` | `SetProcessWorkingSetSizeEx` with `QUOTA_LIMITS_HARDWS_MAX_ENABLE` | Soft physical-RAM cap: Windows pages out instead of failing allocations. |
| `networkBlocked` | Dynamic WFP session, ALE connect and receive/accept IPv4/IPv6 | Blocks uploads and downloads by exe path; existing connections may survive. Filters disappear on stop or crash. |
| `uploadLimitMbps` | WMI COM `MSFT_NetQosPolicySettingData`, `ActiveStore` | Upload only; 0 = none. One combined throttle/DSCP policy per exe path. |
| `networkPriority` | Same ActiveStore policy, `DSCPAction` | DSCP 0–63; router support required. Original = default (no Corewright policy). |

Network enforcement needs administrator rights. Claims are per process identity; Windows applies them by executable path: any block, lowest nonzero upload cap, highest DSCP number. The last claim's release removes the policy/filter. Startup removes leftover `Corewright-*` ActiveStore policies after a crash; other policies are untouched. Download shaping needs a kernel driver and remains out of scope without the owner.

Per-process `netSend`/`netReceive` (bytes/s) are requested with `/processes?include=net`. Direct kernel-network ETW runs for network clients (60 s grace) OR enabled compiled `netMBps` conditions (owner, 2026-11-02); rule demand is cached at compile time. Missing data disables the entire dependent rule, including negated conditions, and Explain reports why. ETW events aggregate into a preallocated PID dictionary without per-event allocations. Elevated ETW cost still needs owner measurement.

## Rule engine invariants

- A rule is `when` (a condition tree of `group` nodes with `and`/`or`/`negate` and `cond` leaves `field op value`) plus `actions` (one value per slot). An empty group matches everything.
- **Per-slot resolution:** rules are ordered by `priority` (higher first, then file order); each slot goes to the highest matching rule that sets it. Different rules may own different slots of one process.
- **Revert:** the first time the engine writes a slot it reads and keeps the original value; when no rule claims the slot any more, it writes the original back. Pausing, rule changes and service shutdown revert everything the engine applied. Never leave a process modified after the engine stops owning it.
- `sustainSeconds` (condition must hold this long before the rule activates) and `releaseSeconds` (must be false this long before it deactivates) prevent flapping.
- Process identity is `(pid, creation time)`; every Win32 handle is checked against the creation time before writing, so PID reuse can never hit the wrong process.
- A failed write (access denied, protected process) is reported once and not retried until the rules change or the process restarts.
- Never touch critical processes (pid 0 and 4, `csrss`, `smss`, `wininit`, `winlogon`, `services`, `lsass`, `LsaIso`, `Registry`, `Memory Compression`, `Secure System`, `dwm`, `fontdrvhost`) or the service itself; the list lives in `ProtectedProcesses.cs`.
- `rules.json` is user data: written atomically, the previous version kept as `rules.json.bak`, an unreadable file is never overwritten.

- Quick rules: the process context menu saves "always" settings into one rule per process name (`quick-<name>`, priority 60, above imports and examples).

## Autopilot (owner, 2026-11-02)

"The game mode itself should be part of a mode … that automatically schedules Windows processes more smartly … Just like the ProBalance of Process Lasso does, only better with our own rule system in the backend." Working name **Autopilot** (owner may rename).

- Autopilot never writes to processes itself: it generates built-in rules (`autopilot-…`, priorities 4–9, below every user rule) from `settings.json`; Explain and the Activity log show them like any rule.
- **Off by default** until the owner confirms the default (owner rule on user-visible defaults).
- Game detection runs only on foreground or full-screen changes: games list > "never games" list > game library paths (Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot, Battle.net, …) > full screen (tray-reported: exclusive D3D or a window covering its monitor) with Direct3D 9/11/12 or Vulkan loaded. A game stays active until its process exits. Facts `game.active`, `game.name`; rule field `isGame`.
- Game placement "auto": the V-Cache CCD on single V-Cache parts, otherwise the fastest CCD by CPPC (on the owner's dual V-Cache 9950X3D2: CCD 0), all cores on single-CCD CPUs. Games are never moved to the "back" cores.
- Balancing (ProBalance-like): processes outside the app in front (rule field `isForegroundApp`), services included, above the CPU threshold for the sustain time get lower priority until they stay calm for the release time. Latency-critical processes (`latencyCritical`: audio, input, display drivers, network core) are never touched.
- Build tools (owner, 2026-11-02: "high load but not latency critical"): an editable list of compilers, linkers, build drivers and shader compilers runs below normal with low I/O priority and automatic placement; no efficiency mode (it would slow builds).
- Background placement (PM-12; version 2, owner 2026-11-02: "is our background rule really that easy??"): decides per **program**, never per exe name. Identity (`ProgramIdentity`, rule field `program`) is the exe path, or for hosts what they run, read once from the command line: `python.exe (train.py)`, `java.exe (server.jar)`, `node.exe (vite.js)`, `svchost.exe (wuauserv)`. "In front" means the whole app: every process with the same app root (topmost ancestor below explorer/services) as the foreground process; anything in front in the last 30 min is interactive. Statistics age with a 3-day half-life (`background-stats-v2.json`; the v1 file is kept); a load profile gives the 95th percentile, so bursts count. Services are included (owner: "saying services are left alone is wrong"), latency-critical ones never. On by default (owner, 2026-11-02; `settings.json` version 2 migrates older files once).
- **Dynamic placement** (owner, 2026-11-02): affinity tokens `auto-background` / `auto-game` are resolved by `AffinityPlacer` with the whole picture: CCDs, physical cores and SMT threads, V-Cache, P/E cores, CPPC strength, live per-thread load, reservations of already placed processes, and the process's demand (measured cores × 1.5, at least 2 threads, whole cores). Hard limits: never the strongest cores, never the game CCD while a game runs.
- **Sticky for cache locality** (owner: "if it's placed on a thread it should stay there more or less until it's finished"): placed once on the cheapest CCD, then only grows within that CCD (at most every 30 s); moves only when a game claims its CCD or its cores stay > 95 % busy for 60 s while another CCD is < 40 %, at most every 2 min. Current placements and reasons: `GET /api/v1/autopilot/placements`, Settings → Live placements.

## Settings page and autostart

- `GET`/`PUT /api/v1/settings` (validated by compiling the resulting rules; invalid values are rejected). The page saves automatically, Windows Settings style.
- "Start Corewright when I sign in" writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Corewright` = quoted tray path, from the dashboard window (it runs as the user); the installer writes the same value.

## Monitoring data and its cost (owner requests, 2026-11-02)

- Sampler values (Task Manager "Details" memory, CPU time, I/O counters, …) come from the same `NtQuerySystemInformation` call and cost nothing extra.
- Per-process values that need a handle (current affinity and priorities, command line, user, GDI objects) are read only for columns the dashboard shows (`/processes?include=`), at most every 3 s, outside the engine lock; the cache is dropped 30 s after the last request.
- GPU (PDH "GPU Engine" / "GPU Adapter Memory") and per-thread load are sampled only while a client is active; the PDH query is closed 60 s after the dashboard goes away. Network totals (`GetIfTable2`) are always sampled.
- CPU topology (CCDs from L3 groups, cores, SMT threads, V-Cache by L3 size) and the CPPC core ranking (Kernel-Processor-Power event 55, `MaximumPerformancePercent`; matches Ryzen Master's ★/• on the owner's 9950X3D2, tied cores shown as tied) are read once at start. Affinity tokens: `ccd0`…, `vcache-ccd`, `frequency-ccd`, `best-ccd`, `best-cores:N`.
- Every setting a rule can change is visible as a process column (owner: "pro tool", always shown); all Task Manager columns are available through the column chooser.

## Native Windows look (owner, 2026-11-02)

"This needs to look more native windows!" and "tray icon context menu also needs to be modern! not windows xp!" Every UI surface follows Windows 11 Fluent:

- Dashboard: system light/dark theme (`prefers-color-scheme`), the user's accent palette (the host injects `AccentPalette`; dark uses Light 2, light uses Dark 1), Windows 11 base colours with a matching title bar (`DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWA_CAPTION_COLOR`), Segoe UI Variable type ramp, Segoe Fluent Icons, WinUI-style controls (NavigationView pane, buttons, ToggleSwitch, MenuFlyout context menus, ContentDialog). No custom brand palette in the chrome; the logo is the only brand colour.
- Tray: Common Controls v6 manifest, PerMonitorV2, theme-following dark menus (`SetPreferredAppMode`), the real icon at the system small-icon size.
- Icons: `assets/corewright.ico` (16 to 256 px; 16 to 24 px from the simplified `corewright-mark-small.svg`), rebuilt with `scripts/build-icon.ps1`.

## Process Lasso import (owner, 2026-11-02)

"So I can just write import everything." Rules → Import… previews and imports `prolasso.ini` from `C:\ProgramData\ProcessLasso\config` (or a chosen file) in one click. One rule per Lasso process pattern (id `lasso-…`, priority 30 for names, 25 for wildcard patterns); re-importing replaces the previous import and keeps the user's own rules. Wildcards match name, path and, when Lasso's `MatchOnCommandLine` is on, the command line (`commandLine` condition field, read on demand and never stored). Anything Lasso does that Corewright cannot express yet is listed as a note, never dropped silently.

## Layout and stack

- `src/Corewright.Core`: rule model, compiler, engine, slot formats; no Win32 calls, fully unit-testable.
- `src/Corewright.Service`: Kestrel minimal API, sampler (`NtQuerySystemInformation`), Win32 actuator, Windows service host. Runs as a console app for development or as the `Corewright` service.
- `src/Corewright.Tray`: NativeAOT Win32 tray (`Shell_NotifyIcon`), foreground reporting, opens the dashboard.
- `src/Corewright.Dashboard`: WinForms + WebView2 host; injects the token and loads the UI.
- `ui/`: React + TypeScript + Vite; builds into `src/Corewright.Service/wwwroot` (ignored by Git).
- No test project yet (see Tests).
- .NET 10 SDK, Node 26. Each project builds into its own `bin/` (a shared output folder broke incremental builds: MSBuild's incremental clean deleted `Corewright.Core.dll`); `scripts/publish.ps1` puts everything together in `artifacts/publish/`.

## Development loop

- Owner start: `start.bat` rebuilds (through `build.bat --no-start`) when `scripts\is-stale.ps1` finds sources newer than the published service, then starts the service elevated in a minimized console, the tray and the dashboard. `build.bat` = `scripts\stop.ps1` → `scripts\publish.ps1` → start (owner, 2026-11-02). `stop.ps1` uses each component's own exit path (dashboard event, tray `WM_CLOSE`, service API) and never kills by name; agents do not run it against the owner's instance. Tray → "Exit Corewright" stops a console-mode service through `POST /api/v1/app/shutdown` (refused for an installed service) and closes the dashboard.
- An agent's test service never uses the owner's port 47830 or data folder: set `Corewright__Port` and `Corewright__DataDirectory` for the process, and `COREWRIGHT_ENDPOINT_FILE` for the Vite dev proxy.

- Service: `dotnet watch --project src/Corewright.Service` (console mode, not elevated).
- UI: `npm run dev` in `ui/` and open `http://localhost:5173`; the Vite proxy reads `endpoint.json` and adds the token. Or start the dashboard with `COREWRIGHT_UI_URL=http://localhost:5173` for the real WebView2 window.
- Tray and dashboard: `dotnet run --project src/Corewright.Tray`.
- Smoke check: build, start the service in console mode with a timeout, call `/api/v1/status` and `/api/v1/processes`, stop the recorded PID.
- Agents never run the service elevated or install it. Non-elevated console mode can only change the agent's own processes, which is enough to test the engine end to end on a process the agent started itself (record its PID, stop only that PID). Installing the service (`scripts/install-service.ps1`, elevated) is the owner's step.
- **No load generators on the owner's PC:** proving that GPU, I/O or CPU priority changes contention needs competing load and runs on a CI runner or a spare machine, never here.

## Releases

Any release, installer or publication needs the owner's yes. `scripts/publish.ps1` builds the UI and the NativeAOT binaries locally; it does not install or publish anything.

## Open items

- ETW (`Microsoft-Windows-Kernel-Process`) for instant process start detection, consumed directly without TraceEvent to stay AOT-friendly.
- CPU Sets for affinity on hybrid CPUs and machines with more than 64 logical processors.
- Network elevated acceptance: WFP/QoS/ETW behavior, crash cleanup and active ETW idle cost. Download shaping needs an owner decision about a kernel driver.
- Per-process GPU usage as a rule field (`D3DKMTQueryStatistics`, not PDH).
- Token file ACL in service mode: currently readable by all local users through the default `ProgramData` inheritance; tighten to per-user tokens before a public release.
- Logo: `design/corewright-logo.svg` (owner pick "C-frame", 2026-11-02); app and tray use the tile-less mark in `assets/`.
- Product name: **Corewright** (owner, 2026-11-02; "Arbiter" dropped because an existing PC gaming optimizer uses it). Formal trademark search before a public release.
