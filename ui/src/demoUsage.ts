/** Usage history for screenshots (VITE_DEMO=1): 24 hours, an antivirus scan in the morning and a build in the afternoon. */
export function demoUsage(now: number, program: string | null) {
  const lp = 32
  const hourMs = 3_600_000
  const start = Math.floor(now / hourMs) * hourMs - 23 * hourMs
  const progs = [
    { program: 'c:\\programdata\\microsoft\\windows defender\\platform\\{version}\\msmpeng.exe', name: 'MsMpEng.exe', base: 1.1, peak: [7, 8, 9, 10], peakCores: 9.5, io: 21e9, mem: 310e6 },
    { program: 'c:\\program files\\microsoft visual studio\\18\\community\\msbuild\\current\\bin\\amd64\\msbuild.exe', name: 'MSBuild.exe', base: 0, peak: [14, 15, 16], peakCores: 6.5, io: 4.8e9, mem: 900e6 },
    { program: 'c:\\program files\\mozilla firefox\\firefox.exe', name: 'firefox.exe', base: 0.6, peak: [], peakCores: 0, io: 1.1e9, mem: 2.4e9 },
    { program: 'c:\\program files\\cloudbackup\\cloudbackup.exe', name: 'CloudBackup.exe', base: 0.25, peak: [3, 4], peakCores: 1.4, io: 9.6e9, mem: 330e6 },
    { program: 'c:\\windows\\system32\\dwm.exe', name: 'dwm.exe', base: 0.22, peak: [], peakCores: 0, io: 0, mem: 120e6 },
    { program: 'c:\\windows\\system32\\svchost.exe|wuauserv', name: 'svchost.exe (wuauserv)', base: 0.05, peak: [2], peakCores: 1.1, io: 1.6e9, mem: 90e6 },
    { program: 'c:\\users\\alex\\appdata\\local\\discord\\app-{version}\\discord.exe', name: 'Discord.exe', base: 0.12, peak: [], peakCores: 0, io: 0.2e9, mem: 410e6 },
    { program: 'c:\\program files\\obs-studio\\bin\\64bit\\obs64.exe', name: 'obs64.exe', base: 0, peak: [20, 21], peakCores: 1.8, io: 6e9, mem: 690e6 },
  ]
  const hours = Array.from({ length: 24 }, (_, i) => {
    const cores = progs.map((p) => (p.peak.includes(i) ? p.peakCores : p.base) * (0.85 + 0.3 * Math.abs(Math.sin(i * 1.7 + p.name.length))))
    return { i, cores, busy: cores.reduce((a, b) => a + b, 0) + 0.4 }
  })
  const avail = 24 * 3600 * lp
  const totals = progs.map((_, j) => hours.reduce((a, h) => a + h.cores[j] * 3600, 0))
  const busy = hours.reduce((a, h) => a + h.busy * 3600, 0)
  const programs = progs
    .map((p, j) => ({
      program: p.program,
      name: p.name,
      cpuSeconds: totals[j],
      cpuPercent: (100 * totals[j]) / avail,
      busyPercent: (100 * totals[j]) / busy,
      averageCores: totals[j] / 86400,
      ioBytes: p.io,
      averageMemory: p.mem,
      peakMemory: p.mem * 1.6,
      runningSeconds: p.base > 0 ? 86400 : p.peak.length * 3600,
    }))
    .sort((a, b) => b.cpuSeconds - a.cpuSeconds)
  const j = progs.findIndex((p) => p.program === program)
  return {
    enabled: true,
    from: start,
    to: now,
    measuredSeconds: 86400,
    logicalProcessors: lp,
    availableCpuSeconds: avail,
    busyCpuSeconds: busy,
    unlistedCpuSeconds: 24 * 3600 * 0.4,
    program,
    programs,
    hours: hours.map((h) => ({ time: start + h.i * hourMs, seconds: 3600, cpuPercent: (100 * h.busy) / lp, programPercent: j >= 0 ? (100 * h.cores[j]) / lp : null })),
    storage: { bytes: 3.4e6, capBytes: 64 * 1048576, oldestTime: now - 12 * 86_400_000, files: 13 },
  }
}
