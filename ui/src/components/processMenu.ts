import type { ProcessRow, Rule, RuleActions } from '../types'
import type { MenuEntry } from './ContextMenu'

interface Handlers {
  setAlways: (patch: Partial<Record<keyof RuleActions, unknown>>) => void
  createRule: () => void
  showDetails: () => void
  reveal?: () => void
  copy: (text: string) => void
}

const levels = (current: unknown, values: [string, string][], apply: (v: string | null) => void): MenuEntry[] => [
  ...values.map(([v, label]): MenuEntry => ({ type: 'item', label, checked: current === v, onClick: () => apply(v) })),
  { type: 'sep' },
  { type: 'item', label: 'Not set', checked: current == null, onClick: () => apply(null) },
]

const CPU: [string, string][] = [
  ['realtime', 'Realtime'],
  ['high', 'High'],
  ['aboveNormal', 'Above normal'],
  ['normal', 'Normal'],
  ['belowNormal', 'Below normal'],
  ['idle', 'Idle'],
]
const IO: [string, string][] = [
  ['high', 'High'],
  ['normal', 'Normal'],
  ['low', 'Low'],
  ['veryLow', 'Very low'],
]
const MEMORY: [string, string][] = [
  ['normal', 'Normal'],
  ['belowNormal', 'Below normal'],
  ['medium', 'Medium'],
  ['low', 'Low'],
  ['veryLow', 'Very low'],
]

/** Context menu for one process. "Always" settings are stored in that process's quick rule. */
export function buildProcessMenu(p: ProcessRow, quick: Rule | undefined, logical: number, hybrid: boolean, h: Handlers): MenuEntry[] {
  const a = quick?.actions ?? {}
  const locked = p.protected
  const half = Math.floor(logical / 2)
  const affinity: [string, string][] = [['all', 'All cores']]
  if (hybrid) affinity.push(['p-cores', 'Performance cores'], ['e-cores', 'Efficiency cores'])
  if (logical >= 4) affinity.push([`0-${half - 1}`, `Cores 0–${half - 1}`], [`${half}-${logical - 1}`, `Cores ${half}–${logical - 1}`])

  return [
    { type: 'head', label: `${p.name} · ${p.pid}` },
    { type: 'item', label: 'CPU priority', glyph: '\uE9D9', disabled: locked, submenu: levels(a.cpuPriority, CPU, (v) => h.setAlways({ cpuPriority: v })) },
    { type: 'item', label: 'I/O priority', glyph: '\uEDA2', disabled: locked, submenu: levels(a.ioPriority, IO, (v) => h.setAlways({ ioPriority: v })) },
    { type: 'item', label: 'GPU priority', glyph: '\uE7F4', disabled: locked, submenu: levels(a.gpuPriority, CPU, (v) => h.setAlways({ gpuPriority: v })) },
    { type: 'item', label: 'Memory priority', glyph: '\uE964', disabled: locked, submenu: levels(a.memoryPriority, MEMORY, (v) => h.setAlways({ memoryPriority: v })) },
    { type: 'item', label: 'CPU cores', glyph: '\uE950', disabled: locked, submenu: levels(a.affinity, affinity, (v) => h.setAlways({ affinity: v })) },
    {
      type: 'item',
      label: 'Efficiency mode',
      glyph: '\uEC0A',
      disabled: locked,
      submenu: [
        { type: 'item', label: 'On', checked: a.efficiencyMode === true, onClick: () => h.setAlways({ efficiencyMode: true }) },
        { type: 'item', label: 'Off', checked: a.efficiencyMode === false, onClick: () => h.setAlways({ efficiencyMode: false }) },
        { type: 'sep' },
        { type: 'item', label: 'Not set', checked: a.efficiencyMode == null, onClick: () => h.setAlways({ efficiencyMode: null }) },
      ],
    },
    {
      type: 'item',
      label: 'Memory cap',
      glyph: '\uE8B1',
      disabled: locked,
      submenu: levels(
        a.memoryLimitMb == null ? null : String(a.memoryLimitMb),
        [1024, 2048, 4096, 8192, 16384].map((mb) => [String(mb), `${mb / 1024} GB (hard limit)`]),
        (v) => h.setAlways({ memoryLimitMb: v === null ? null : Number(v) }),
      ),
    },
    {
      type: 'item',
      label: 'Reset all for this process',
      glyph: '\uE777',
      disabled: locked || !quick,
      onClick: () =>
        h.setAlways({
          cpuPriority: null, ioPriority: null, gpuPriority: null, memoryPriority: null,
          affinity: null, efficiencyMode: null, memoryLimitMb: null, workingSetLimitMb: null,
        }),
    },
    { type: 'sep' },
    { type: 'item', label: 'Create rule…', glyph: '\uE710', disabled: locked, onClick: h.createRule },
    { type: 'item', label: 'Details', glyph: '\uE946', onClick: h.showDetails },
    { type: 'sep' },
    ...(h.reveal ? [{ type: 'item', label: 'Open file location', glyph: '\uE838', disabled: !p.path, onClick: h.reveal } as MenuEntry] : []),
    { type: 'item', label: 'Copy name', glyph: '\uE8C8', onClick: () => h.copy(p.name) },
    { type: 'item', label: 'Copy path', glyph: '\uE8C8', disabled: !p.path, onClick: () => h.copy(p.path ?? '') },
    { type: 'item', label: 'Copy PID', glyph: '\uE8C8', onClick: () => h.copy(String(p.pid)) },
  ]
}
