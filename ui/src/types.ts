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
  game?: { pid: number; name: string; reason?: string } | null
  dataDirectory: string
}

export interface AppSettings {
  version: number
  autopilot: {
    enabled: boolean
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
      cpuPercent: number
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
}

export interface AppliedSlot {
  slot: string
  value: string
  ruleId: string
  ruleName: string
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
}

export interface ExplainRule {
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
