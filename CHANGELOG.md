# Changelog

Release notes for every Corewright version, newest first. The release workflow publishes the section of a version
as its GitHub release notes.

## 0.1.0-beta.12

### New

- **History page: which programs use your PC over hours and days.** For every program: its CPU time, its share of all
  the CPU time your processors had, its share of what your PC actually did, average cores, how long it ran, disk
  activity and average and peak memory. Pick Today, 24 hours, 7 days, 30 days or everything; click a program to see
  its hours next to the whole PC's in the chart. A virus scanner that keeps a core busy all day now stands out.
- **The history has a size limit** (64 MB by default, about half a year of hours on a typical PC; 8 MB to 1 GB). When it is full,
  the oldest hours are deleted first. Recording can be turned off, and the history cleared, on the same page.
- Recording reuses the measurements Corewright already takes every two seconds; it adds no system calls and no work
  per tick beyond adding up numbers. The current hour is saved every five minutes and at shutdown.
- API: `GET /api/v1/usage` and `DELETE /api/v1/usage`; settings gain `usage.enabled` and `usage.maxStorageMb`.

## 0.1.0-beta.11

- Game mode: remote desktop and virtual machine viewers are never games; graphics API labels corrected.

## 0.1.0-beta.10

Autopilot, version 3. Three independent reviews found that automatic core placement could slow builds and leave
programs confined; this release rebuilds it to be careful first.

### New

- **Autopilot has three modes: Off, Observe and On.** Observe shows what Autopilot would change, and why, without
  touching any process. New installations start in Observe; an Autopilot you had turned on stays on.
- **A limit on how many processes Autopilot places at once** (40 by default). Status shows how many are placed.
- **Crash recovery:** Corewright records what it changes before it changes it. If the service ever stops
  unexpectedly, the next start puts the affected programs back.

### Changed

- **Builds keep all their CPU cores.** Compilers and build tools only get lower CPU and disk priority, so builds no
  longer start on two threads.
- **Background placement is simpler:** a placed program may use every core except your strongest few (only while
  you are actively using something) and except the game's CCD while a game runs. No more per-program core packing.
- **Programs that start other programs** (terminals, editors, AI agents, build tools, launchers) are never placed
  by any Autopilot rule, so the programs they start are never confined.
- **No information, no placement:** when the tray is not running, the PC is locked or you have been away for 10
  minutes, Autopilot stops placing and stops learning until you are back.
- **Balancing** acts only when the PC is actually busy, measures load per core, and never raises a priority.
- **While gaming**, busy background work moves to the other CCD as a whole, only after it is 15 s old and busy for 5 s.
- **Several games can run at once**; game mode ends when the last one closes. Game launchers (Steam, Battle.net, EA,
  Epic, …) are no longer detected as games.
- **Your rules always beat Autopilot**, whatever their priority. Rule ids starting with `autopilot-` or `group-` are
  reserved.
- **Learning is more precise:** scripts, projects and Windows services are told apart, statistics keep their peaks
  and age while the PC is off, and programs not seen for 30 days are forgotten.
- Explain says when pause, a group, a freeze or the placement limit decides, and why a program counts as background.

### Fixed

- Programs started through a shell by a placed program, or by a parent that already exited, are put back too.
- A balancing threshold you had changed is converted to the new per-core setting instead of being reset.

### For integrations

- `/api/v1/settings`: `autopilot.mode` (`off`, `observe`, `on`) replaces `enabled` (still read, written as
  `mode == on`); `balance.cpuCores` replaces `balance.cpuPercent`. `/api/v1/status` adds `autopilotMode`,
  `autopilotPlaced`, `autopilotCapped` and `journalProblem`. New: `GET /api/v1/autopilot/preview`.

## 0.1.0-beta.9

### Fixed

- **Updates failed for standard users who approve the administrator prompt with another account.** The setup runs
  as that administrator and could not close the Corewright window, so it stopped. The window now lets administrators
  close it for an update. Because the window that is open during an update comes from your installed version, the
  first update to beta 9 may still fail: close Corewright (tray icon → Exit Corewright), then run the beta 9 setup from
  the website once.
- **Update errors showed the wrong line** of the setup log ("does not match the interactive session user"). The setup
  wrote its error lines over the start of its log; it now appends them, and the update message shows the actual error.

## 0.1.0-beta.8

### Fixed

- **Corewright did not reopen after an update when another account approved the administrator prompt** (a standard
  user with an administrator's password). Corewright Updater now reopens Corewright as the signed-in user. This works
  from the next update after installing beta 8.
- **The window said "The Corewright service is not running" right after an update** and told you to use start.bat,
  which only exists in a development copy. It now waits up to a minute for the service, and if it really is not
  running it explains how to start or repair the installed service and shows the last update error.

## 0.1.0-beta.7

### Fixed

- **The Corewright window crashed when the keyboard layout changed** ("Argument_CultureNotSupportedInInvariantMode,
  1031" with a German layout). The window now knows every Windows language.

### Changed

- **Processes:** the summary line above the list reads as one even sentence; only the numbers stand out.

## 0.1.0-beta.6

### New

- **Integrations → Recent API calls:** the last 100 calls that changed something (facts, rules, settings, groups)
  and every rejected call, with the program that made it, its result and how long it took. Reads are not listed.
  Also available as `GET /api/v1/api-calls`.

## 0.1.0-beta.5

### Fixed

- **In-app updates failed with "Corewright files are still in use"** and left Corewright closed. Corewright Updater
  ran from the install folder, and the setup refused to replace its files. The setup now moves the running updater
  aside. Updating from beta 3 or 4 works with this setup.

### Changed

- **Devices uses the width better:** processor and memory sit side by side, values no longer wrap into several
  lines, and volumes without a drive letter are counted instead of listed as long volume paths.

## 0.1.0-beta.4

### Fixed

- **Background placement no longer confines programs that start other programs**, such as AI agent hosts,
  terminals, editors and launchers. Their work runs in the programs they start, and those inherited the
  confinement for their first second, so short commands ran on two threads. Learned → "Left alone: starts other
  programs".

## 0.1.0-beta.3

### New

- **Devices shows the processor and memory:** CCDs with L3 cache and V-Cache, how busy each CCD is, the fastest
  cores; memory in use and free, and every installed module with slot, size, type and speed. A hint appears when a
  memory kit runs below its rated speed (EXPO or XMP off in the BIOS).
- **Overview cards open their section on Devices** when you click the card title.

### Changed

- **Updates ask for administrator rights as "Corewright Updater"** instead of Windows PowerShell, and **Corewright
  reopens after an update** instead of staying closed.
- **Devices comes after Processes** in the navigation.
- **A failed update says why** (for example another Corewright still running) instead of "exit 1".

### Fixed

- **The window showed the previous version's interface after an update** until it was reloaded. The start page is
  now always checked for changes.
- **Virtual network adapters** (VPN, Hyper-V, WSL) no longer fill the Devices page and the Overview network picker;
  a switch shows them.

## 0.1.0-beta.2

The first public beta.

### New

- **Network control per program:** block the network (both ways), limit upload speed in Mbps, and set network
  priority (Low, Normal, High, Highest). From a rule or with a right-click in the process list (Network). Blocks and
  limits disappear when Corewright stops, even after a crash. Download limits are not possible without a driver.
- **Network usage per process** as a column and as a rule condition.
- **Devices page:** every disk, graphics card and network adapter with live values, a health verdict, temperatures
  and SMART data (NVMe wear, spare, media errors, power-on hours). The Overview's Disk, GPU and Network cards can show
  one device or all, and the GPU card shows load and video memory of the total.
- **Process groups API** (`/api/v1/groups`) for tools such as build queues: exact process trees, the usual rule
  settings, `exclusive:N` cores and freezing Autopilot during measurements.
- **Rules show their recent history:** the last processes each rule changed, and which it still controls.
- **Mark a process as a game** (right-click, Game): always a game, never a game, or let Autopilot decide.
- **Appearance:** light, dark or the Windows theme, and an accent colour (Windows, ten presets or your own).
- **Window settings:** start minimized, close button keeps Corewright in the tray or exits it, minimize to the tray.
- **Update channels:** Stable or Beta in Settings, Updates. A beta install starts on Beta.

### Fixed

- **Programs started by a changed process no longer keep its cores or low priority.** Windows passes them on; they
  now get the original back. Under beta 1 this confined agent shells and their tools to two threads.
- **Background placement no longer treats your own apps as background** when the tray is not running; it only learns
  while it knows which window is in front, and never places processes younger than 15 seconds.
- **The tray always comes with the window,** whichever way Corewright was opened.
- **A setting that cannot be put back is retried** once a minute and reported once, instead of being forgotten.
- **Game detection:** screenshot tools such as ShareX are no longer taken for games.

### Installer

- The Corewright logo on the setup and in its pages.
- The setup says what it installs and asks before installing the service.
- A firewall rule blocks network access to the Corewright service from other machines.
- Uninstall keeps your rules and settings.

## 0.1.0-beta.1

Internal test build (not published).
