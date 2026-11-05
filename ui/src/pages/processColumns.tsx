import { netPriorityLabel } from '../networkPriority'
import { bytes, percent } from '../format'
import { levelLabel } from '../host'
import type { ProcessRow, SystemInfo } from '../types'

/** Detail groups the service reads on demand; requested only when a visible column needs them. */
export type Need = 'ext' | 'gpu' | 'net' | 'settings' | 'identity' | 'cmdline' | 'gui'

export interface ColumnDef {
  id: string
  label: string
  group: string
  width: number
  num?: boolean
  needs?: Need
  visible?: boolean // shown by default
  /** Raw value for one process (sorting, aggregation, display). */
  value: (p: ProcessRow) => number | string | boolean | undefined
  /** How an app group (several processes) combines values. Default: same value or "Mixed". */
  agg?: 'sum' | 'max'
  format?: (v: number | string | boolean | undefined) => string
  /** Heat-map level 0..1 like Task Manager. */
  heat?: (v: number, sys?: SystemInfo) => number
  total?: (sys: SystemInfo) => string
  /** Corewright slot shown in this column; highlighted when a rule sets it. */
  slot?: string
  /** The Windows default for this setting; shown dimmed so real changes stand out. */
  defaultValue?: string | number | boolean
}

const num = (v: unknown) => (typeof v === 'number' ? v : undefined)
const count = (v: unknown) => (typeof v === 'number' ? v.toLocaleString() : '')
const mem = (v: unknown) => (typeof v === 'number' ? bytes(v) : '')
const rate = (v: unknown) => {
  const n = num(v) ?? 0
  return n < 1024 ? '0 MB/s' : `${(n / 1048576).toFixed(n < 10 * 1048576 ? 1 : 0)} MB/s`
}
const duration = (v: unknown) => {
  const ms = num(v)
  if (ms === undefined) return ''
  const s = Math.floor(ms / 1000)
  const h = Math.floor(s / 3600)
  return `${h}:${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
}
const level = (v: unknown) => (typeof v === 'string' ? levelLabel(v) || v : '')
const mb = (v: unknown) => (typeof v === 'number' ? (v === 0 ? 'None' : v >= 1024 && v % 1024 === 0 ? `${v / 1024} GB` : `${v} MB`) : '')
const text = (v: unknown) => (v === undefined || v === null ? '' : String(v))

export const COLUMNS: ColumnDef[] = [
  // General
  { id: 'name', label: 'Name', group: 'General', width: 300, visible: true, value: (p) => p.name },
  { id: 'pid', label: 'PID', group: 'General', width: 72, num: true, visible: true, value: (p) => p.pid, format: text },
  { id: 'image', label: 'Image name', group: 'General', width: 160, value: (p) => p.name },
  { id: 'path', label: 'Image path', group: 'General', width: 320, value: (p) => p.path },
  { id: 'services', label: 'Services', group: 'General', width: 220, value: (p) => p.services?.map((s) => s.name).join(', ') },
  { id: 'cmdline', label: 'Command line', group: 'General', width: 420, needs: 'cmdline', value: (p) => p.ext?.commandLine },
  { id: 'user', label: 'User name', group: 'General', width: 120, needs: 'identity', value: (p) => p.ext?.user },
  { id: 'session', label: 'Session', group: 'General', width: 72, num: true, value: (p) => p.session, format: count },
  { id: 'elevated', label: 'Elevated', group: 'General', width: 80, needs: 'identity', value: (p) => p.ext?.elevated, format: (v) => (v === undefined ? '' : v ? 'Yes' : 'No') },
  { id: 'bits', label: 'Platform', group: 'General', width: 80, needs: 'identity', value: (p) => p.ext?.bits, format: (v) => (v ? `${v}-bit` : '') },
  { id: 'started', label: 'Started', group: 'General', width: 150, needs: 'ext', value: (p) => p.ext?.started, format: (v) => (typeof v === 'number' ? new Date(v).toLocaleString() : '') },

  // CPU
  { id: 'priority', defaultValue: 'normal',  label: 'Priority', group: 'CPU', width: 112, visible: true, slot: 'cpuPriority', value: (p) => p.priority, format: level },
  { id: 'cpu', label: 'CPU', group: 'CPU', width: 84, num: true, visible: true, agg: 'sum', value: (p) => p.cpu, format: (v) => percent(num(v) ?? 0), heat: (v) => v / 25, total: (s) => percent(s.cpu, 0) },
  { id: 'cputime', label: 'CPU time', group: 'CPU', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.cpuTimeMs, format: duration },
  { id: 'usertime', label: 'User time', group: 'CPU', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.userTimeMs, format: duration },
  { id: 'kerneltime', label: 'Kernel time', group: 'CPU', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.kernelTimeMs, format: duration },
  { id: 'cycles', label: 'Cycles', group: 'CPU', width: 130, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.cycles, format: count },
  { id: 'basepriority', label: 'Base priority', group: 'CPU', width: 96, num: true, needs: 'ext', value: (p) => p.ext?.basePriority, format: count },
  { id: 'threads', label: 'Threads', group: 'CPU', width: 76, num: true, visible: true, agg: 'sum', value: (p) => p.threads, format: count },

  // GPU
  { id: 'gpu', label: 'GPU', group: 'GPU', width: 76, num: true, visible: true, needs: 'gpu', agg: 'sum', value: (p) => p.ext?.gpu, format: (v) => percent(num(v) ?? 0), heat: (v) => v / 40, total: (s) => percent(s.gpu, 0) },

  // Memory
  { id: 'private', label: 'Memory', group: 'Memory', width: 100, num: true, visible: true, agg: 'sum', value: (p) => p.privateBytes, format: mem, heat: (v, s) => (s ? (v / s.memoryTotal) * 12 : 0), total: (s) => percent((100 * s.memoryUsed) / s.memoryTotal, 0) },
  { id: 'privatews', label: 'Private working set', group: 'Memory', width: 120, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.privateWorkingSet, format: mem },
  { id: 'sharedws', label: 'Shared working set', group: 'Memory', width: 120, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.sharedWorkingSet, format: mem },
  { id: 'ws', label: 'Working set', group: 'Memory', width: 100, num: true, agg: 'sum', value: (p) => p.workingSet, format: mem },
  { id: 'peakws', label: 'Peak working set', group: 'Memory', width: 116, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.peakWorkingSet, format: mem },
  { id: 'peakcommit', label: 'Peak commit', group: 'Memory', width: 100, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.peakPrivateBytes, format: mem },
  { id: 'virtual', label: 'Virtual size', group: 'Memory', width: 100, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.virtualSize, format: mem },
  { id: 'paged', label: 'Paged pool', group: 'Memory', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.pagedPool, format: mem },
  { id: 'nonpaged', label: 'Non-paged pool', group: 'Memory', width: 110, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.nonPagedPool, format: mem },
  { id: 'pagefaults', label: 'Page faults', group: 'Memory', width: 104, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.pageFaults, format: count },
  { id: 'hardfaults', label: 'Hard faults', group: 'Memory', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.hardFaults, format: count },

  // Disk
  { id: 'network', label: 'Network', group: 'Network', width: 130, needs: 'net', num: true, agg: 'sum', value: (p) => p.ext?.networkError ?? (p.ext?.netSend === undefined ? undefined : p.ext.netSend + (p.ext.netReceive ?? 0)), format: (v) => typeof v === 'string' ? v : v === undefined ? '' : rate(v) },
  { id: 'io', label: 'Disk', group: 'Disk', width: 92, num: true, visible: true, agg: 'sum', value: (p) => p.ioBytesPerSec, format: rate, heat: (v) => v / (50 * 1048576), total: (s) => rate(s.ioBytesPerSec) },
  { id: 'readbytes', label: 'I/O read bytes', group: 'Disk', width: 110, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.readBytes, format: mem },
  { id: 'writebytes', label: 'I/O write bytes', group: 'Disk', width: 110, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.writeBytes, format: mem },
  { id: 'otherbytes', label: 'I/O other bytes', group: 'Disk', width: 110, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.otherBytes, format: mem },
  { id: 'readops', label: 'I/O reads', group: 'Disk', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.readOperations, format: count },
  { id: 'writeops', label: 'I/O writes', group: 'Disk', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.writeOperations, format: count },
  { id: 'otherops', label: 'I/O other', group: 'Disk', width: 96, num: true, needs: 'ext', agg: 'sum', value: (p) => p.ext?.otherOperations, format: count },

  // Handles
  { id: 'handles', label: 'Handles', group: 'Handles', width: 84, num: true, agg: 'sum', value: (p) => p.handles, format: count },
  { id: 'gdi', label: 'GDI objects', group: 'Handles', width: 96, num: true, needs: 'gui', agg: 'sum', value: (p) => p.ext?.gdiObjects, format: count },
  { id: 'userobj', label: 'USER objects', group: 'Handles', width: 100, num: true, needs: 'gui', agg: 'sum', value: (p) => p.ext?.userObjects, format: count },

  // Everything a Corewright rule can set (owner: always shown in this pro tool).
  { id: 'affinity', defaultValue: 'all',  label: 'Affinity', group: 'Corewright', width: 96, visible: true, needs: 'settings', slot: 'affinity', value: (p) => p.ext?.affinity, format: (v) => (v === 'all' ? 'All' : text(v)) },
  { id: 'iopriority', defaultValue: 'normal',  label: 'I/O priority', group: 'Corewright', width: 96, visible: true, needs: 'settings', slot: 'ioPriority', value: (p) => p.ext?.ioPriority, format: level },
  { id: 'gpupriority', defaultValue: 'normal',  label: 'GPU priority', group: 'Corewright', width: 104, visible: true, needs: 'settings', slot: 'gpuPriority', value: (p) => p.ext?.gpuPriority, format: level },
  { id: 'mempriority', defaultValue: 'normal',  label: 'Memory priority', group: 'Corewright', width: 112, visible: true, needs: 'settings', slot: 'memoryPriority', value: (p) => p.ext?.memoryPriority, format: level },
  { id: 'efficiency', defaultValue: 'auto',  label: 'Efficiency mode', group: 'Corewright', width: 108, visible: true, needs: 'settings', slot: 'efficiencyMode', value: (p) => p.ext?.efficiencyMode, format: (v) => (v === 'on' ? 'On' : v === 'off' ? 'Off' : v === 'auto' ? 'Auto' : '') },
  { id: 'memcap', defaultValue: 0,  label: 'Memory cap', group: 'Corewright', width: 96, visible: true, needs: 'settings', slot: 'memoryLimitMb', value: (p) => p.ext?.memoryLimitMb, format: mb },
  { id: 'ramcap', defaultValue: 0,  label: 'RAM cap', group: 'Corewright', width: 88, visible: true, needs: 'settings', slot: 'workingSetLimitMb', value: (p) => p.ext?.workingSetLimitMb, format: mb },
  { id: 'networkblocked', label: 'Network access', group: 'Corewright', width: 110, visible: true, needs: 'settings', slot: 'networkBlocked', defaultValue: false, value: (p) => p.ext?.networkBlocked, format: (v) => v === undefined ? '' : v ? 'Blocked' : 'Allowed' },
  { id: 'uploadlimit', label: 'Upload limit', group: 'Corewright', width: 110, visible: true, needs: 'settings', slot: 'uploadLimitMbps', defaultValue: 0, value: (p) => p.ext?.uploadLimitMbps, format: (v) => v === 0 ? 'No limit' : v === undefined ? '' : `${v} Mbps` },
  { id: 'netpriority', label: 'Network priority', group: 'Corewright', width: 120, visible: true, needs: 'settings', slot: 'networkPriority', defaultValue: -1, value: (p) => p.ext?.networkPriority, format: (v) => v === undefined ? '' : netPriorityLabel(v as number) },
  { id: 'rules', label: 'Rules', group: 'Corewright', width: 160, value: (p) => [...new Set((p.applied ?? []).map((a) => a.ruleName))].join(', ') },
]

export const COLUMN_GROUPS = [...new Set(COLUMNS.map((c) => c.group))]
