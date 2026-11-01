# Changelog

Release notes for every Corewright version, newest first. The release workflow publishes the section of a version
as its GitHub release notes.

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
