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
It writes the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
value named `Corewright`, containing the quoted full path to
`Corewright.Tray.exe`. Clearing the checkbox removes this value. The settings
page uses this same value. This starts the tray at sign-in; the service starts
independently. Other users can enable their own tray autostart in settings.
Run setup under your own account using normal UAC consent: supplying a different
administrator account applies HKCU and console-rule migration to that account.

The finish page's **Start Corewright** checkbox opens the tray through
`explorer.exe`, which delegates to the existing interactive desktop shell so the
tray starts with the user's normal, unelevated token. Silent setup starts only
the service; it never launches a user interface.

## Upgrade and console-mode rules

Run the newer setup over the existing installation. Setup stops the service and
waits up to 30 seconds for its graceful shutdown. A console-mode service must be
exited with the tray's **Exit Corewright** command; setup offers Retry/Cancel.
Silent setup aborts if one is still running. The tray receives `WM_CLOSE` at its
`CorewrightTray` window; the dashboard receives the
`Local\Corewright.Dashboard.Exit` event. Setup waits up to 15 seconds for these
applications to exit. Close Corewright in other signed-in sessions too. Setup
never kills applications to release files.

Rules for the service live in `%ProgramData%\Corewright`. If `rules.json` is
absent there, setup copies the installing user's
`%LOCALAPPDATA%\Corewright\rules.json`, leaving the original in place. Existing
machine rules are never overwritten. Upgrading defaults the sign-in checkbox to
on again; clear it if you want tray autostart disabled.

## Uninstall

Use Windows **Settings → Apps → Installed apps → Corewright → Uninstall**.
Uninstall closes the tray and dashboard, stops the service (reverting its process
changes), deletes the service, installed payload, Start menu shortcut, current
user's Run value and Installed apps entry. Files you added to the installation
folder are kept. Other users' Run values belong to their profiles and are not
removed; they can disable them in Windows Startup apps.

By default `%ProgramData%\Corewright` and `%LOCALAPPDATA%\Corewright` stay,
including rules, backups and settings. Select **Also remove my rules and
settings** to delete both the machine data and uninstalling user's console data.
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
