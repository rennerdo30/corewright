export interface Status {
  version: string
  mode: 'console' | 'service'
  elevated: boolean
  paused: boolean
  intervalMs: number
  processCount: number
  rulesVersion: number
  activeRules: number
  foregroundPid: number
  autopilot: boolean
  autopilotPlaced?: number
  autopilotCapped?: boolean
  game?: { pid: number; name: string; reason?: string } | null
  dataDirectory: string
  /** PM-49: live "isolate:N" rules. threads = where the program runs; reserved = whole cores nobody else uses. */
  isolations?: Isolation[]
  /** PM-49: Autopilot keeps physical core 0 for Windows. */
  windowsCore?: { threads: string; keptOff: number; refused: number } | null
}

export interface Isolation {
  ruleId: string
  rule: string
  count: number
  threads: string
  reserved: string
  processes: number
  keptOff: number
  refused: number
}

export interface Device {
  id: string; kind: 'disk' | 'gpu' | 'network'; name: string; index?: number; bus?: string; size?: number; volumes?: string[]
  dedicatedMemory?: number; sharedMemory?: number; alias?: string; type?: string; receiveLinkSpeed?: number; sendLinkSpeed?: number; connected?: boolean; hidden: boolean
  values: { read?: number; write?: number; active?: number; load?: number; dedicatedUsed?: number; sharedUsed?: number; sampledAt?: number }
  health: { temperature?: number; fanRpm?: number; powerPercent?: number; engineClockMhz?: number; wearPercent?: number; sparePercent?: number; criticalWarning?: number; mediaErrors?: string; powerOnHours?: string; unsafeShutdowns?: string; dataUnitsRead?: string; dataUnitsWritten?: string; sampledAt?: number }
  history?: { time: number[]; primary: (number | null)[]; secondary: (number | null)[] }
}
export interface DevicesInfo { inventoryAt: number; sampling: boolean; devices: Device[] }

export interface UsageProgram {
  program: string
  name: string
  cpuSeconds: number
  /** Share of all CPU time the processors had in the range. */
  cpuPercent: number
  /** Share of the CPU time the processors were busy. */
  busyPercent: number
  averageCores: number
  ioBytes: number
  averageMemory: number
  peakMemory: number
  runningSeconds: number
}

export interface UsageInfo {
  enabled: boolean
  from: number
  to: number
  measuredSeconds: number
  logicalProcessors: number
  availableCpuSeconds: number
  busyCpuSeconds: number
  unlistedCpuSeconds: number
  program: string | null
  programs: UsageProgram[]
  hours: { time: number; seconds: number; cpuPercent: number; programPercent: number | null }[]
  storage: { bytes: number; capBytes: number; oldestTime: number | null; files: number; problem: string | null }
}

export interface AppSettings {
  version: number
  warnings?: string[]
  usage?: { enabled: boolean; maxStorageMb: number }
  autopilot: {
    enabled: boolean
    mode?: 'off' | 'observe' | 'on'
    placementLimit?: number
    /** PM-49: every process except the shell and protected ones leaves physical core 0. */
    keepCoreZeroFree?: boolean
    launchers?: string[]
    gameMode: {
      enabled: boolean
      gamePriority: string
      gameGpuPriority: string
      placement: string
      moveBackground: boolean
      backgroundPriority: string
      games: string[]
      notGames: string[]
      keepResponsive: string[]
    }
    balance: {
      enabled: boolean
      cpuCores: number
      sustainSeconds: number
      releaseSeconds: number
      priority: string
      exclusions: string[]
    }
    buildTools: {
      enabled: boolean
      priority: string
      ioPriority: string
      programs: string[]
    }
    background: {
      enabled: boolean
      minObservedMinutes: number
      includeServices: boolean
      maxForegroundPercent: number
      singleCoreLoad: number
      keepBestCoresFree: number
      priority: string
      exclusions: string[]
    }
  }
}

export interface BackgroundRow {
  /** What the user sees: "python.exe (train.py)". */
  name: string
  /** Identity key (rule field "program"). */
  program: string
  p95Cores: number
  observedMinutes: number
  foregroundPercent: number
  averageCores: number
  peakCores: number
  service: boolean
  verdict: string
  affinity?: string
  reason: string
}

export interface SystemInfo {
  cpu: number
  memoryTotal: number
  memoryUsed: number
  ioBytesPerSec: number
  processCount: number
  cores: { logicalProcessors: number; hybrid: boolean; performanceCores: string; efficiencyCores: string }
  historyTime: number[]
  historyCpu: number[]
  historyMemory: number[]
  historyIo: number[]
  gpu: number
  gpuMemory: number
  gpuAdapters: { id: string; utilization: number; dedicatedUsed: number }[]
  netReceive: number
  netSend: number
  historyGpu: number[]
  historyNetReceive: number[]
  historyNetSend: number[]
  threadUsage: number[]
}

export interface CoreInfo {
  id: number
  threads: number[]
  performance?: number
  rank: number
  ccdRank: number
  tied: boolean
  efficiencyClass: number
}

export interface Topology {
  name: string
  logical: number
  physical: number
  hybrid: boolean
  rankingAvailable: boolean
  ccds: { id: number; l3Bytes: number; vCache: boolean; cores: CoreInfo[] }[]
}

/** Optional per-process values, filled only for the groups requested with ?include=. */
export interface ProcessExt {
  started?: number
  cpuTimeMs?: number
  userTimeMs?: number
  kernelTimeMs?: number
  cycles?: number
  privateWorkingSet?: number
  sharedWorkingSet?: number
  peakWorkingSet?: number
  peakPrivateBytes?: number
  virtualSize?: number
  peakVirtualSize?: number
  pagedPool?: number
  nonPagedPool?: number
  pageFaults?: number
  hardFaults?: number
  readOperations?: number
  writeOperations?: number
  otherOperations?: number
  readBytes?: number
  writeBytes?: number
  otherBytes?: number
  basePriority?: number
  gpu?: number
  affinity?: string
  ioPriority?: string
  gpuPriority?: string
  memoryPriority?: string
  efficiencyMode?: string
  workingSetLimitMb?: number
  memoryLimitMb?: number
  gdiObjects?: number
  userObjects?: number
  user?: string
  elevated?: boolean
  bits?: number
  commandLine?: string
  netSend?: number
  netReceive?: number
  networkError?: string
  networkBlocked?: boolean
  uploadLimitMbps?: number
  networkPriority?: number
}

export interface AppliedSlot {
  slot: string
  value: string
  ruleId: string
  ruleName: string
}

export interface ServiceInfo {
  /** Key name, e.g. "wuauserv". */
  name: string
  /** Display name, e.g. "Windows Update". */
  display: string
}

export interface ProcessRow {
  pid: number
  parentPid?: number
  name: string
  path?: string
  cpu: number
  workingSet: number
  privateBytes: number
  ioBytesPerSec: number
  threads: number
  handles: number
  priority: string
  session: number
  protected: boolean
  foreground: boolean
  applied?: AppliedSlot[]
  ext?: ProcessExt
  /** Windows services running in this process (PM-46); absent for ordinary processes. */
  services?: ServiceInfo[] | null
}

export interface ExplainRule {
  reason?: string
  unavailable?: string
  ruleId: string
  ruleName: string
  priority: number
  matches: boolean
  active: boolean
  won: string[]
  lost: string[]
}

export interface Explain {
  pid: number
  name: string
  rules: ExplainRule[]
}

export interface Condition {
  type: 'group' | 'cond'
  combinator?: 'and' | 'or'
  negate?: boolean
  children?: Condition[]
  field?: string
  key?: string
  op?: string
  value?: string
}

export interface RuleActions {
  cpuPriority?: string | null
  affinity?: string | null
  ioPriority?: string | null
  gpuPriority?: string | null
  memoryPriority?: string | null
  efficiencyMode?: boolean | null
  memoryLimitMb?: number | null
  workingSetLimitMb?: number | null
  networkBlocked?: boolean | null
  uploadLimitMbps?: number | null
  networkPriority?: number | null
}

export interface Rule {
  id: string
  name: string
  enabled: boolean
  priority: number
  sustainSeconds: number
  releaseSeconds: number
  allowRealtime: boolean
  when: Condition
  actions: RuleActions
}

export interface RuleSet {
  version: number
  rules: Rule[]
}

export interface RuleError {
  ruleId: string
  ruleName: string
  message: string
}

export interface RulesResult {
  ok: boolean
  errors: RuleError[]
  version: number
}

export interface EngineEvent {
  seq: number
  time: number
  kind: 'applied' | 'reverted' | 'failed' | 'drift' | 'info'
  pid: number
  process: string
  rule?: string
  slot?: string
  value?: string
  message?: string
}

export interface ImportNote {
  kind: 'info' | 'skipped' | 'unsupported'
  message: string
}

export interface ImportResponse {
  applied: boolean
  source?: string
  rules: Rule[]
  notes: ImportNote[]
  replaced: number
  errors: RuleError[]
}

export interface Placement {
  pid: number
  name: string
  cores: string
  ccd: number
  demandThreads: number
  placedSecondsAgo: number
  why: string
}

/** One time a rule changed a process (GET /rules/{id}/history). */
export interface RuleHit {
  time: number
  pid: number
  process: string
  kind: 'applied' | 'failed'
  set: Record<string, string>
  message?: string
}

/** One API call that changed something or was rejected (GET /api-calls, newest first). */
export interface ApiCall {
  time: number
  method: string
  path: string
  status: number
  ms: number
  pid?: number
  client?: string
}

/** One installed memory module (SMBIOS type 17). */
export interface MemoryModule {
  slot: string
  sizeMb: number
  type: string
  ratedMts: number
  configuredMts: number
  manufacturer: string
  part: string
}
