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
