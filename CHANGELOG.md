# Changelog

Release notes for every Corewright version, newest first. The release workflow publishes the section of a version
as its GitHub release notes.

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
