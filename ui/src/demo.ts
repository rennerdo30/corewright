/**
 * Demo data for screenshots (README, website): a made-up PC with a game, a build and some background work, so no
 * real machine's processes, paths or user names are ever published. Only built with VITE_DEMO=1; the normal
 * build drops this file (the import in api.ts is behind a compile-time constant).
 */
import type { AppSettings, BackgroundRow, EngineEvent, Placement, ProcessRow, Rule, RuleSet, Status, SystemInfo, Topology } from './types'

const now = Date.now()
const wave = (n: number, base: number, amp: number, seed: number) =>
  Array.from({ length: n }, (_, i) => Math.max(0, base + amp * Math.sin(i / 7 + seed) + amp * 0.45 * Math.sin(i / 2.3 + seed * 3)))

// A 16-core part with one V-Cache CCD (CCD 0) and a faster CCD 1, like a Ryzen 9 9950X3D.
const PERF = [138, 134, 130, 136, 128, 132, 126, 124, 166, 174, 158, 162, 150, 154, 146, 142]
const order = [...PERF.keys()].sort((a, b) => PERF[b] - PERF[a])
const topology: Topology = {
  name: 'AMD Ryzen 9 9950X3D 16-Core Processor',
  logical: 32,
  physical: 16,
  hybrid: false,
  rankingAvailable: true,
  ccds: [0, 1].map((ccd) => {
    const ids = Array.from({ length: 8 }, (_, i) => ccd * 8 + i)
    const inCcd = [...ids].sort((a, b) => PERF[b] - PERF[a])
    return {
      id: ccd,
      l3Bytes: (ccd === 0 ? 96 : 32) * 1024 * 1024,
      vCache: ccd === 0,
      cores: ids.map((id) => ({
        id,
        threads: [id * 2, id * 2 + 1],
        performance: PERF[id],
        rank: order.indexOf(id) + 1,
        ccdRank: inCcd.indexOf(id) + 1,
        tied: false,
        efficiencyClass: 0,
      })),
    }
  }),
}

// Game on CCD 0 (V-Cache), build on cores 12-15, background on 9-11; strongest cores 8 and 9 kept free.
const threadUsage = Array.from({ length: 32 }, (_, lp) => {
  const core = lp >> 1
  const smt = lp & 1
  if (core < 8) return Math.round((smt ? 38 : 71) + ((lp * 37) % 19))
  if (core >= 12) return Math.round((smt ? 88 : 96) - ((lp * 13) % 7))
  if (core >= 10) return Math.round(smt ? 9 : 24 + ((lp * 11) % 9))
  return Math.round(smt ? 2 : 5)
})

const HIST = 300
const system: SystemInfo = {
  cpu: 58.4,
  memoryTotal: 64 * 1024 ** 3,
  memoryUsed: 27.6 * 1024 ** 3,
  ioBytesPerSec: 48.2 * 1024 ** 2,
  processCount: 212,
  cores: { logicalProcessors: 32, hybrid: false, performanceCores: '0-31', efficiencyCores: '' },
  historyTime: Array.from({ length: HIST }, (_, i) => now - (HIST - 1 - i) * 1000),
  historyCpu: wave(HIST, 46, 12, 1).map((v, i) => (i > 200 ? v + 12 : v)),
  historyMemory: wave(HIST, 42, 1.5, 2),
  historyIo: wave(HIST, 30e6, 22e6, 3),
  gpu: 92.3,
  gpuMemory: 11.4 * 1024 ** 3,
  gpuAdapters: [{ id: 'gpu0', utilization: 92.3, dedicatedUsed: 11.4 * 1024 ** 3 }],
  netReceive: 3.1e6,
  netSend: 0.42e6,
  historyGpu: wave(HIST, 80, 9, 4).map((v) => Math.min(99, v)),
  historyNetReceive: wave(HIST, 2.5e6, 1.6e6, 5),
  historyNetSend: wave(HIST, 0.4e6, 0.25e6, 6),
  threadUsage,
}

const GAME = 'StarfallOdyssey.exe'
const GAME_PATH = 'D:\\SteamLibrary\\steamapps\\common\\Starfall Odyssey\\StarfallOdyssey.exe'

type Owner = [ruleId: string, ruleName: string]
const AP_GAME: Owner = ['autopilot-game', 'Autopilot: game']
const AP_VOICE: Owner = ['autopilot-voice', 'Autopilot: keep voice and streaming responsive']
const AP_BUILD: Owner = ['autopilot-build-tools', 'Autopilot: compilers and build tools']
const AP_BG: Owner = ['autopilot-game-background', 'Autopilot: background while gaming']
const AP_BAL: Owner = ['autopilot-balance', 'Autopilot: balance CPU hogs']
const QUICK_OBS: Owner = ['quick-obs64-exe', 'Quick: obs64.exe']
const BROWSER_CAP: Owner = ['browser-cap', 'Browser tabs: 4 GB cap']
const LASSO_SPOTIFY: Owner = ['lasso-spotify', 'Process Lasso: spotify.exe']
const DOWNLOADS: Owner = ['downloads-low-io', 'Downloads stay out of the way']

let pid = 4100
interface Spec {
  name: string
  path?: string
  cpu: number
  mem: number // MB
  io?: number // MB/s
  threads?: number
  priority?: string
  session?: number
  protected?: boolean
  foreground?: boolean
  parent?: string
  set?: { owner: Owner; slots: Record<string, string> }[]
  ext?: Partial<NonNullable<ProcessRow['ext']>>
}

const specs: Spec[] = [
  { name: 'System Idle Process', cpu: 41.6, mem: 0.01, session: 0, protected: true },
  { name: 'System', cpu: 0.6, mem: 0.2, threads: 290, session: 0, protected: true },
  {
    name: GAME,
    path: GAME_PATH,
    cpu: 27.8,
    mem: 9820,
    io: 31.5,
    threads: 148,
    priority: 'high',
    foreground: true,
    set: [{ owner: AP_GAME, slots: { cpuPriority: 'high', affinity: '0-15', gpuPriority: 'high' } }],
    ext: { gpu: 88.6, affinity: '0-15', gpuPriority: 'high' },
  },
  { name: 'Discord.exe', path: 'C:\\Users\\Alex\\AppData\\Local\\Discord\\app-1.0.9200\\Discord.exe', cpu: 1.2, mem: 412, threads: 62, priority: 'aboveNormal', set: [{ owner: AP_VOICE, slots: { cpuPriority: 'aboveNormal' } }] },
  { name: 'obs64.exe', path: 'C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe', cpu: 3.4, mem: 690, io: 6.2, threads: 74, priority: 'aboveNormal', set: [{ owner: QUICK_OBS, slots: { cpuPriority: 'aboveNormal', gpuPriority: 'aboveNormal' } }], ext: { gpu: 3.1, gpuPriority: 'aboveNormal' } },
  { name: 'MSBuild.exe', path: 'C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\MSBuild\\Current\\Bin\\amd64\\MSBuild.exe', cpu: 0.8, mem: 380, threads: 24, priority: 'belowNormal', set: [{ owner: AP_BUILD, slots: { cpuPriority: 'belowNormal', ioPriority: 'low', affinity: '24-31' } }], ext: { affinity: '24-31', ioPriority: 'low' } },
  ...[0, 1, 2, 3].map((i) => ({
    name: 'cl.exe',
    path: 'C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\VC\\Tools\\MSVC\\14.50.35717\\bin\\Hostx64\\x64\\cl.exe',
    cpu: 5.6 - i * 0.4,
    mem: 540 + i * 60,
    io: 1.2,
    threads: 6,
    priority: 'belowNormal',
    parent: 'MSBuild.exe',
    set: [{ owner: AP_BUILD, slots: { cpuPriority: 'belowNormal', ioPriority: 'low', affinity: '24-31' } }],
    ext: { affinity: '24-31', ioPriority: 'low' },
  })),
  { name: 'link.exe', path: 'C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\VC\\Tools\\MSVC\\14.50.35717\\bin\\Hostx64\\x64\\link.exe', cpu: 2.9, mem: 1240, io: 8.4, threads: 9, priority: 'belowNormal', parent: 'MSBuild.exe', set: [{ owner: AP_BUILD, slots: { cpuPriority: 'belowNormal', ioPriority: 'low', affinity: '24-31' } }], ext: { affinity: '24-31', ioPriority: 'low' } },
  { name: 'OneDrive.exe', path: 'C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe', cpu: 1.9, mem: 210, io: 2.1, threads: 41, priority: 'belowNormal', set: [{ owner: AP_BG, slots: { cpuPriority: 'belowNormal', ioPriority: 'low', affinity: '20-23' } }], ext: { affinity: '20-23', ioPriority: 'low' } },
  { name: 'CloudBackup.exe', path: 'C:\\Program Files\\CloudBackup\\CloudBackup.exe', cpu: 3.8, mem: 330, io: 12.6, threads: 28, priority: 'idle', set: [{ owner: AP_BAL, slots: { cpuPriority: 'idle' } }, { owner: AP_BG, slots: { ioPriority: 'low', affinity: '18-19' } }], ext: { affinity: '18-19', ioPriority: 'low' } },
  { name: 'qbittorrent.exe', path: 'C:\\Program Files\\qBittorrent\\qbittorrent.exe', cpu: 0.7, mem: 160, io: 9.8, threads: 33, priority: 'belowNormal', set: [{ owner: DOWNLOADS, slots: { cpuPriority: 'belowNormal', ioPriority: 'veryLow' } }], ext: { ioPriority: 'veryLow' } },
  ...[0, 1, 2].map((i) => ({
    name: 'firefox.exe',
    path: 'C:\\Program Files\\Mozilla Firefox\\firefox.exe',
    cpu: [0.9, 0.4, 0.2][i],
    mem: [880, 610, 240][i],
    threads: [92, 31, 24][i],
    set: [{ owner: BROWSER_CAP, slots: { memoryLimitMb: '4096' } }],
    ext: { memoryLimitMb: 4096 },
  })),
  { name: 'Spotify.exe', path: 'C:\\Users\\Alex\\AppData\\Roaming\\Spotify\\Spotify.exe', cpu: 0.3, mem: 190, threads: 44, set: [{ owner: LASSO_SPOTIFY, slots: { efficiencyMode: 'on' } }], ext: { efficiencyMode: 'on' } },
  { name: 'steam.exe', path: 'C:\\Program Files (x86)\\Steam\\steam.exe', cpu: 0.4, mem: 240, threads: 88, priority: 'aboveNormal', set: [{ owner: AP_VOICE, slots: { cpuPriority: 'aboveNormal' } }] },
  { name: 'explorer.exe', path: 'C:\\Windows\\explorer.exe', cpu: 0.3, mem: 180, threads: 120 },
  { name: 'Corewright.Dashboard.exe', path: 'C:\\Program Files\\Corewright\\Corewright.Dashboard.exe', cpu: 0.2, mem: 58, threads: 30 },
  { name: 'Corewright.Tray.exe', path: 'C:\\Program Files\\Corewright\\Corewright.Tray.exe', cpu: 0, mem: 5, threads: 3 },
  { name: 'Corewright.Service.exe', path: 'C:\\Program Files\\Corewright\\Corewright.Service.exe', cpu: 0.01, mem: 17, threads: 18, session: 0, protected: true },
  { name: 'dwm.exe', path: 'C:\\Windows\\System32\\dwm.exe', cpu: 1.1, mem: 120, threads: 22, priority: 'high', protected: true },
  { name: 'audiodg.exe', path: 'C:\\Windows\\System32\\audiodg.exe', cpu: 0.4, mem: 24, threads: 9, priority: 'high', session: 0 },
  { name: 'MsMpEng.exe', path: 'C:\\ProgramData\\Microsoft\\Windows Defender\\Platform\\4.18\\MsMpEng.exe', cpu: 0.6, mem: 310, io: 1.1, threads: 70, session: 0, protected: true },
  ...['svchost.exe', 'svchost.exe', 'svchost.exe', 'svchost.exe', 'RuntimeBroker.exe', 'SearchHost.exe', 'ctfmon.exe', 'sihost.exe', 'taskhostw.exe', 'nvcontainer.exe', 'WmiPrvSE.exe', 'csrss.exe', 'lsass.exe', 'services.exe', 'wininit.exe', 'winlogon.exe'].map(
    (name, i): Spec => ({
      name,
      path: `C:\\Windows\\System32\\${name}`,
      cpu: [0.3, 0.1, 0.1, 0, 0.1, 0.1, 0, 0, 0, 0.2, 0.1, 0.1, 0, 0, 0, 0][i],
      mem: [42, 18, 12, 9, 26, 140, 14, 22, 16, 38, 11, 6, 21, 9, 7, 8][i],
      threads: [24, 9, 7, 5, 11, 46, 12, 15, 10, 20, 8, 13, 10, 7, 3, 5][i],
      session: ['csrss.exe', 'lsass.exe', 'services.exe', 'wininit.exe', 'svchost.exe', 'WmiPrvSE.exe'].includes(name) ? 0 : 1,
      protected: ['csrss.exe', 'lsass.exe', 'services.exe', 'wininit.exe', 'winlogon.exe'].includes(name),
    }),
  ),
]

const pids = new Map<string, number>()
const processes: ProcessRow[] = specs.map((s) => {
  const id = s.name === 'System Idle Process' ? 0 : s.name === 'System' ? 4 : (pid += 4 * (1 + ((pid * 7) % 97)))
  if (!pids.has(s.name)) pids.set(s.name, id)
  const applied = (s.set ?? []).flatMap((x) => Object.entries(x.slots).map(([slot, value]) => ({ slot, value, ruleId: x.owner[0], ruleName: x.owner[1] })))
  return {
    pid: id,
    parentPid: s.parent ? pids.get(s.parent) : undefined,
    name: s.name,
    path: s.path,
    cpu: s.cpu,
    workingSet: s.mem * 1024 * 1024,
    privateBytes: s.mem * 0.8 * 1024 * 1024,
    ioBytesPerSec: (s.io ?? 0) * 1024 * 1024,
    threads: s.threads ?? 8,
    handles: (s.threads ?? 8) * 23,
    priority: s.priority ?? 'normal',
    session: s.session ?? 1,
    protected: !!s.protected,
    foreground: !!s.foreground,
    applied: applied.length ? applied : undefined,
    ext: {
      affinity: 'all',
      ioPriority: 'normal',
      gpuPriority: 'normal',
      memoryPriority: 'normal',
      efficiencyMode: 'auto',
      gpu: 0,
      user: s.session === 0 ? 'SYSTEM' : 'Alex',
      bits: 64,
      ...s.ext,
    },
  }
})

const gamePid = pids.get(GAME)!
const status: Status = {
  version: '0.1.0',
  mode: 'service',
  elevated: true,
  paused: false,
  intervalMs: 1000,
  processCount: processes.length,
  rulesVersion: 4,
  activeRules: 5,
  foregroundPid: gamePid,
  autopilot: true,
  game: { pid: gamePid, name: GAME, reason: 'installed in a game library (steamapps\\common)' },
  dataDirectory: 'C:\\ProgramData\\Corewright',
}

const name = (v: string): Rule['when'] => ({ type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'eq', value: v }] })
const rules: RuleSet = {
  version: 4,
  rules: [
    {
      id: 'downloads-low-io',
      name: 'Downloads stay out of the way',
      enabled: true,
      priority: 50,
      sustainSeconds: 0,
      releaseSeconds: 0,
      allowRealtime: false,
      when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'in', value: 'qbittorrent.exe, steamservice.exe' }, { type: 'cond', field: 'isForeground', op: 'isFalse' }] },
      actions: { cpuPriority: 'belowNormal', ioPriority: 'veryLow' },
    },
    { id: 'quick-obs64-exe', name: 'Quick: obs64.exe', enabled: true, priority: 60, sustainSeconds: 0, releaseSeconds: 0, allowRealtime: false, when: name('obs64.exe'), actions: { cpuPriority: 'aboveNormal', gpuPriority: 'aboveNormal' } },
    {
      id: 'browser-cap',
      name: 'Browser tabs: 4 GB cap',
      enabled: true,
      priority: 50,
      sustainSeconds: 0,
      releaseSeconds: 0,
      allowRealtime: false,
      when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'in', value: 'firefox.exe, chrome.exe, msedge.exe' }] },
      actions: { memoryLimitMb: 4096 },
    },
    {
      id: 'recording-boost',
      name: 'While recording, keep the encoder fast',
      enabled: true,
      priority: 55,
      sustainSeconds: 0,
      releaseSeconds: 5,
      allowRealtime: false,
      when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'fact', key: 'obs.recording', op: 'eq', value: 'true' }, { type: 'cond', field: 'name', op: 'eq', value: 'obs64.exe' }] },
      actions: { memoryPriority: 'normal', efficiencyMode: false },
    },
    { id: 'lasso-spotify', name: 'Process Lasso: spotify.exe', enabled: true, priority: 30, sustainSeconds: 0, releaseSeconds: 0, allowRealtime: false, when: name('spotify.exe'), actions: { efficiencyMode: true } },
    { id: 'lasso-searchindexer', name: 'Process Lasso: searchindexer.exe', enabled: true, priority: 30, sustainSeconds: 0, releaseSeconds: 0, allowRealtime: false, when: name('searchindexer.exe'), actions: { cpuPriority: 'idle', ioPriority: 'veryLow' } },
    { id: 'example-calm-compilers', name: 'Example: calm down compilers in the background', enabled: false, priority: 10, sustainSeconds: 0, releaseSeconds: 0, allowRealtime: false, when: name('cl.exe'), actions: { cpuPriority: 'belowNormal', ioPriority: 'low', efficiencyMode: true } },
  ],
}

const settings: AppSettings = {
  version: 3,
  autopilot: {
    enabled: true,
    gameMode: {
      enabled: true,
      gamePriority: 'high',
      gameGpuPriority: 'high',
      placement: 'auto',
      moveBackground: true,
      backgroundPriority: 'belowNormal',
      games: [],
      notGames: ['chrome.exe', 'msedge.exe', 'firefox.exe', 'vlc.exe', 'Code.exe', 'obs64.exe', 'ShareX.exe', 'SnippingTool.exe'],
      keepResponsive: ['Discord.exe', 'TeamSpeak.exe', 'obs64.exe', 'audiodg.exe', 'steam.exe'],
    },
    balance: { enabled: true, cpuPercent: 8, sustainSeconds: 2, releaseSeconds: 6, priority: 'belowNormal', exclusions: [] },
    buildTools: { enabled: true, priority: 'belowNormal', ioPriority: 'low', programs: ['cl.exe', 'link.exe', 'MSBuild.exe', 'clang.exe', 'rustc.exe', 'cargo.exe', 'ninja.exe', 'dxc.exe'] },
    background: { enabled: true, minObservedMinutes: 60, includeServices: true, maxForegroundPercent: 1, singleCoreLoad: 1.2, keepBestCoresFree: 4, priority: 'belowNormal', exclusions: [] },
  },
}

const placements: Placement[] = [
  ...processes
    .filter((p) => p.name === 'cl.exe' || p.name === 'link.exe' || p.name === 'MSBuild.exe')
    .map((p, i) => ({ pid: p.pid, name: p.name, cores: '24-31', ccd: 1, demandThreads: [8, 8, 8, 8, 4, 2][i] ?? 2, placedSecondsAgo: 140 - i * 9, why: i === 0 ? 'Grew to 8 threads on CCD 1 because it needed more' : 'Placed on CCD 1, the least busy place for 8 threads, away from the game, off your strongest cores.' })),
  { pid: pids.get('OneDrive.exe')!, name: 'OneDrive.exe', cores: '20-23', ccd: 1, demandThreads: 2, placedSecondsAgo: 1820, why: 'Placed on CCD 1, the least busy place for 2 threads, away from the game, off your strongest cores.' },
  { pid: pids.get('CloudBackup.exe')!, name: 'CloudBackup.exe', cores: '18-19', ccd: 1, demandThreads: 2, placedSecondsAgo: 610, why: 'Placed on CCD 1, the least busy place for 2 threads, away from the game, off your strongest cores.' },
]

const background: BackgroundRow[] = [
  { name: 'OneDrive.exe', program: 'c:\\program files\\microsoft onedrive\\onedrive.exe', observedMinutes: 2210, foregroundPercent: 0, averageCores: 0.4, peakCores: 2.1, p95Cores: 0.8, service: false, verdict: 'Background: placed', affinity: 'auto', reason: 'never in front in 37 h' },
  { name: 'python.exe (render_batch.py)', program: 'python.exe (render_batch.py)', observedMinutes: 380, foregroundPercent: 0, averageCores: 5.2, peakCores: 7.8, p95Cores: 6, service: false, verdict: 'Background: placed, lower priority', affinity: 'auto', reason: 'never in front, bursts to 6 cores' },
  { name: 'svchost.exe (wuauserv)', program: 'svchost.exe (wuauserv)', observedMinutes: 1460, foregroundPercent: 0, averageCores: 0.6, peakCores: 3.9, p95Cores: 3, service: true, verdict: 'Background: placed, lower priority', affinity: 'auto', reason: 'Windows Update' },
  { name: 'CloudBackup.exe', program: 'c:\\program files\\cloudbackup\\cloudbackup.exe', observedMinutes: 1460, foregroundPercent: 0, averageCores: 1.6, peakCores: 5.8, p95Cores: 4, service: false, verdict: 'Background: placed, lower priority', affinity: 'auto', reason: 'never in front in 24 h' },
  { name: 'node.exe (vite.js)', program: 'node.exe (vite.js)', observedMinutes: 600, foregroundPercent: 18, averageCores: 0.3, peakCores: 2.2, p95Cores: 1.5, service: false, verdict: 'Interactive: in front recently', reason: 'runs in the terminal you use' },
  { name: 'svchost.exe (audiosrv)', program: 'svchost.exe (audiosrv)', observedMinutes: 2400, foregroundPercent: 0, averageCores: 0.1, peakCores: 0.4, p95Cores: 0.15, service: true, verdict: 'Left alone: latency critical', reason: 'audio' },
  { name: 'Discord.exe', program: 'c:\\users\\alex\\appdata\\local\\discord\\app-1.0.9200\\discord.exe', observedMinutes: 2400, foregroundPercent: 4.2, averageCores: 0.3, peakCores: 1.4, p95Cores: 0.8, service: false, verdict: 'Interactive', reason: 'in front 4.2 % of the time' },
  { name: 'firefox.exe', program: 'c:\\program files\\mozilla firefox\\firefox.exe', observedMinutes: 1900, foregroundPercent: 31, averageCores: 0.6, peakCores: 4.2, p95Cores: 2, service: false, verdict: 'Interactive', reason: 'in front 31 % of the time' },
]

let seq = 0
const ev = (secondsAgo: number, kind: EngineEvent['kind'], process: string, rule?: string, slot?: string, value?: string, message?: string): EngineEvent => ({
  seq: ++seq,
  time: now - secondsAgo * 1000,
  kind,
  pid: pids.get(process) ?? 0,
  process,
  rule,
  slot,
  value,
  message,
})
const events: EngineEvent[] = [
  ev(3400, 'applied', 'Spotify.exe', LASSO_SPOTIFY[1], 'efficiencyMode', 'on'),
  ev(1820, 'applied', 'OneDrive.exe', AP_BG[1], 'cpuPriority', 'belowNormal'),
  ev(1820, 'applied', 'OneDrive.exe', AP_BG[1], 'affinity', '20-23'),
  ev(1820, 'applied', 'OneDrive.exe', AP_BG[1], 'ioPriority', 'low'),
  ev(900, 'info', GAME, 'Autopilot', undefined, undefined, 'game detected (installed in a game library (steamapps\\common))'),
  ev(900, 'applied', GAME, AP_GAME[1], 'cpuPriority', 'high'),
  ev(900, 'applied', GAME, AP_GAME[1], 'affinity', '0-15'),
  ev(900, 'applied', GAME, AP_GAME[1], 'gpuPriority', 'high'),
  ev(899, 'applied', 'Discord.exe', AP_VOICE[1], 'cpuPriority', 'aboveNormal'),
  ev(640, 'applied', 'CloudBackup.exe', AP_BAL[1], 'cpuPriority', 'idle'),
  ev(610, 'applied', 'CloudBackup.exe', AP_BG[1], 'affinity', '18-19'),
  ev(430, 'drift', 'CloudBackup.exe', AP_BAL[1], 'cpuPriority', 'idle', 'process changed its priority; re-applied (1/3)'),
  ev(140, 'applied', 'MSBuild.exe', AP_BUILD[1], 'cpuPriority', 'belowNormal'),
  ev(140, 'applied', 'MSBuild.exe', AP_BUILD[1], 'affinity', '24-31'),
  ev(140, 'applied', 'MSBuild.exe', AP_BUILD[1], 'ioPriority', 'low'),
  ev(131, 'applied', 'cl.exe', AP_BUILD[1], 'cpuPriority', 'belowNormal'),
  ev(131, 'applied', 'cl.exe', AP_BUILD[1], 'affinity', '24-31'),
  ev(122, 'applied', 'link.exe', AP_BUILD[1], 'cpuPriority', 'belowNormal'),
  ev(95, 'failed', 'MsMpEng.exe', AP_BAL[1], 'cpuPriority', 'belowNormal', 'access denied (protected process)'),
  ev(64, 'reverted', 'qbittorrent.exe', DOWNLOADS[1], 'cpuPriority', 'normal'),
  ev(64, 'reverted', 'qbittorrent.exe', DOWNLOADS[1], 'ioPriority', 'normal'),
  ev(12, 'applied', 'qbittorrent.exe', DOWNLOADS[1], 'cpuPriority', 'belowNormal'),
  ev(12, 'applied', 'qbittorrent.exe', DOWNLOADS[1], 'ioPriority', 'veryLow'),
]

const json = <T>(v: T): T => structuredClone(v)

export async function demoRequest(method: string, path: string, body?: unknown): Promise<unknown> {
  const p = path.split('?')[0]
  if (method !== 'GET') {
    if (p === '/rules' || p === '/rules/validate') return { ok: true, errors: [], version: rules.version }
    if (p === '/settings') {
      Object.assign(settings, body as AppSettings)
      return { ok: true }
    }
    return undefined
  }
  switch (p) {
    case '/status':
      return json(status)
    case '/system':
      return json(system)
    case '/topology':
      return json(topology)
    case '/processes':
      return json(processes)
    case '/settings':
      return json(settings)
    case '/rules':
      return json(rules)
    case '/autopilot/placements':
      return json(placements)
    case '/autopilot/background':
      return json(background)
    case '/facts':
      return { 'obs.recording': 'true', 'discord.call': 'true' }
    case '/events': {
      const after = Number(new URLSearchParams(path.split('?')[1]).get('after') ?? 0)
      return { last: seq, events: events.filter((e) => e.seq > after) }
    }
  }
  const history = p.match(/^\/rules\/([^/]+)\/history$/)
  if (history) {
    const rule = rules.rules.find((r) => r.id === decodeURIComponent(history[1]))
    const hits: { time: number; pid: number; process: string; kind: string; set: Record<string, string> }[] = []
    for (const e of [...events].reverse()) {
      if (!rule || e.rule !== rule.name || !e.slot || (e.kind !== 'applied' && e.kind !== 'failed')) continue
      const last = hits[hits.length - 1]
      if (last && last.pid === e.pid && Math.abs(last.time - e.time) < 1000) last.set[e.slot] = e.value ?? ''
      else if (hits.length < 10) hits.push({ time: e.time, pid: e.pid, process: e.process, kind: e.kind, set: { [e.slot]: e.value ?? '' } })
    }
    return hits
  }
  const explain = p.match(/^\/processes\/(\d+)\/explain$/)
  if (explain) {
    const row = processes.find((x) => x.pid === Number(explain[1]))
    const ids = new Map((row?.applied ?? []).map((a) => [a.ruleId, a.ruleName]))
    return {
      pid: row?.pid ?? 0,
      name: row?.name ?? '',
      rules: [...ids].map(([ruleId, ruleName]) => ({ ruleId, ruleName, priority: 9, matches: true, active: true, won: (row?.applied ?? []).filter((a) => a.ruleId === ruleId).map((a) => a.slot), lost: [] })),
    }
  }
  return undefined
}
