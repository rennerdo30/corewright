# Install Corewright on Windows

Use `Corewright-<version>-setup.exe` on an x64 Windows PC. Setup requests
administrator approval and installs to `%ProgramFiles%\Corewright` for all users.
The dashboard needs the **.NET 10 Desktop Runtime (x64)** and **Microsoft Edge
WebView2 Runtime**. Setup does not download or install these prerequisites.

The `Corewright` Windows service runs as LocalSystem, starts automatically with
Windows, and restarts after a failure (5, 15, then 60 seconds). The all-users
**Corewright** Start menu shortcut opens the dashboard. Stopping the service
gracefully reverts every process change it made.

On the options page, **Start Corewright when I sign in** is checked by default.
Setup records this first-run default in `%ProgramData%\Corewright\defaults.json`.
The unelevated tray applies it once in the signed-in user's profile, writing
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Corewright` with the
quoted path to `Corewright.Tray.exe`. It records `AutostartDecided` under
`HKCU\Software\Corewright`; subsequent starts and upgrades preserve that user's
choice. Settings toggles the same Run value and records the decision. Enable the
finish-page launch or start the tray manually once to apply the fresh-install
default; silent installation alone never writes another account's HKCU.
Other users can choose their own tray autostart in Settings. The service starts
independently of the tray.

The finish page's **Start Corewright** checkbox opens the tray through
`explorer.exe`, which delegates to the existing interactive desktop shell so the
tray starts with the user's normal, unelevated token. Silent setup starts only
the service on a fresh install; silent upgrades restart a previously running tray.
This unelevation method depends on an existing, unelevated Explorer shell; an
absent/elevated shell or disabled UAC may prevent the expected launch/token.

## Upgrade and console-mode rules

Run the newer setup over the existing installation, or use **Settings → Updates
→ Check now → Install update**. The dashboard checks published stable GitHub
Releases when it opens, at most once per 24 hours; **Check now** checks immediately.
There is no background polling when the dashboard is closed. Downloads use no
GitHub token. After administrator consent, an in-memory bootstrap embedded in the
installed dashboard independently downloads setup and its checksum from the fixed
Corewright release URL. No user-cache executable, script or checksum is elevated.
It creates a fresh administrator-only `%ProgramFiles%\Corewright-update-<random>`
directory with its ACL applied at creation. It checks every ancestor without
following reparse points, validates administrator control and holds the path and
setup handles against write/delete sharing through hashing and execution. Failed
verification never runs setup. The protected stage is kept for diagnosis; an
administrator can remove it afterwards. Development copies offer a GitHub download link instead.
The last check is stored in `%LOCALAPPDATA%\Corewright\update-check.json`.

Setup performs process and file-lock preflights before stopping the service, then
waits up to 30 seconds for its graceful shutdown. An abort before file replacement
restores a previously running service. A console-mode service must be
exited with the tray's **Exit Corewright** command; setup offers Retry/Cancel.
Silent setup aborts if one is still running. The tray receives `WM_CLOSE` at its
`CorewrightTray` window; the dashboard receives the
`Local\Corewright.Dashboard.Exit` event. Setup waits up to 15 seconds for these
applications to exit. Close Corewright in other signed-in sessions too. Setup
never kills applications to release files.

Rules for the service live in `%ProgramData%\Corewright`. If `rules.json` is
absent there, setup copies the installing user's
`%LOCALAPPDATA%\Corewright\rules.json`, leaving the original in place. Existing
machine rules are never overwritten. Migration happens only when the elevated
token's SID matches the interactive session user obtained through WTS; otherwise
it is skipped and logged, rather than reading the credential-prompt administrator's
profile. Back up and copy rules explicitly under administrator control in that case.
Source files and each ancestor are opened with `FILE_FLAG_OPEN_REPARSE_POINT`,
checked for reparse attributes (and a single link for files), and pinned against
write/delete sharing. Bytes are copied from the validated handle into a new
destination; a link or sharing violation aborts rather than following the link.
On same-user upgrades, the disabled sign-in checkbox reflects the existing Run
value (present means on, absent means off); use Settings to change an existing
user's choice. A different elevation account cannot inspect that choice. Silent
upgrades never change Run values and restart the tray unelevated if it was running.
Fresh installs default to autostart on. Upgrades from an older installation without
defaults conservatively leave unregistered users off.

Before the service starts, setup stops the old service and creates an entirely new
ProgramData tree with protected ACLs at creation: SYSTEM and Administrators full
control, Users read/execute. It copies old data through pinned, non-reparse,
single-link file handles into newly created objects. It preserves the entire old
tree as `%ProgramData%\Corewright-preserved-<random>` and renames the new tree into
place without replacement. Held handles on the old objects cannot rewrite the
new objects' ACLs. No user data is deleted. Failures retain the original and any
fresh copy for recovery; link/ACL/path validation failures abort. A service stopped
before replacement is restored immediately on failure. This runs again on upgrades.
The existing bearer-token policy is
unchanged: local users can read the service token. Per-user tokens and tighter
token ACLs remain a known limitation, as recorded in `CLAUDE.md`.

For development, `build.bat` detects an installed service, packages the current
source, and runs setup silently with one UAC prompt. It updates the installed
copy instead of starting a console service. `start.bat` then starts the installed
tray; without an installed service, both scripts keep their console workflow.
See [RELEASING.md](RELEASING.md) for version tags, CI drafts and owner publication.

## Uninstall

Use Windows **Settings → Apps → Installed apps → Corewright → Uninstall**.
Uninstall closes the tray and dashboard, stops the service (reverting its process
changes), deletes the service, installed payload, Start menu shortcut, current
user's Run value (only after the same-user SID check) and Installed apps entry.
With a different credential-prompt account, per-user removal is skipped and
logged; the signed-in user can remove the startup entry in Windows Startup apps.
Uninstall validates the complete removal manifest before stopping/deleting the
service, and reports nonzero failure if a required removal is incomplete.
Files you added to the installation
folder are kept. Other users' Run values belong to their profiles and are not
removed; they can disable them in Windows Startup apps.

By default `%ProgramData%\Corewright` and `%LOCALAPPDATA%\Corewright` stay,
including rules, backups and settings. Select **Also remove my rules and
settings** to delete machine data and, after the same-user SID check, the
uninstalling user's console data and remembered startup decision.
This deletes rules shared by all users; back them up first. Silent uninstall
always keeps data. Console data in other user profiles is never removed.

## Silent commands and diagnostics

Run from an already elevated terminal:

```powershell
.\Corewright-0.1.0-setup.exe /S
& "$env:ProgramFiles\Corewright\uninstall.exe" /S
```

`/S` is case-sensitive. Installation location is fixed; `/D` is not supported.
Success returns 0; failures return nonzero. Service operations use `sc.exe`
through NSIS `nsExec`, with a 10-second inactivity timeout per command, numeric
result checks and bounded state waits. Details show command output; commands,
exit codes and errors are also appended to
`%TEMP%\Corewright-installer-<version>.log`. An error stops the operation for
diagnosis; setup does not automatically roll back a partially completed install.
Partial file-copy failures have no rollback, and downgrades are unrestricted.
Fix the reported condition and rerun setup or uninstall. The first real install
and service validation belong to the owner.

## Build and CI verification

```powershell
pwsh -NoProfile -File scripts/package.ps1
# Or use another portable NSIS 3 compiler:
pwsh -NoProfile -File scripts/package.ps1 -MakeNsis C:\Tools\NSIS\makensis.exe
```

The script restores missing UI dependencies from the lockfile, runs
`scripts/publish.ps1`, stages its complete output and compiles
`artifacts/installer/Corewright-<version>-setup.exe`. Version comes from
`Directory.Build.props`. Portable NSIS 3.13 is expected at
`E:/Development/_cache/tools/nsis-3.13/`; its official ZIP URL and SHA-256 are
recorded in `scripts/package.ps1`. No system-wide tool installation or persistent
PATH change is needed. Every child build has a timeout and reports its PID.
Stages are retained under `artifacts/installer/stage-*` for inspection. Each
stage also contains the generated exact-payload manifest include needed by
`corewright.nsi`. No installer is run by packaging.

On a **disposable, already elevated Windows x64 CI runner only**, set `CI` and
run `scripts/test-installer.ps1 -Setup <setup.exe> -Version <version>`. It refuses
existing Corewright installations/data, then checks silent install, service
identity/start mode, registration, rule migration, upgrade preservation and
silent uninstall with data retention. Do not run this script on the owner's PC.
