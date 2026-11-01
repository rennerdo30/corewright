import type { ProcessRow, Rule, RuleActions, Topology } from '../types'
import type { MenuEntry } from './ContextMenu'

interface Handlers {
  setAlways: (patch: Partial<Record<keyof RuleActions, unknown>>) => void
  createRule: () => void
  showDetails: () => void
  reveal?: () => void
  copy: (text: string) => void
  /** Marks the program as a game, never a game, or lets Autopilot decide; undefined while settings load. */
  setGame?: (kind: 'always' | 'never' | 'auto') => void
  gameKind?: 'always' | 'never' | 'auto'
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

/** Affinity presets from the real CPU layout: all cores, each CCD (with V-Cache / fastest marked), the best cores by CPPC. */
function affinityPresets(topo: Topology | undefined, hybrid: boolean): [string, string][] {
  const out: [string, string][] = [['all', 'All cores']]
  if (hybrid) out.push(['p-cores', 'Performance cores'], ['e-cores', 'Efficiency cores'])
  if (!topo) return out
  const top = topo.ccds.flatMap((c) => c.cores.map((k) => ({ ccd: c.id, k }))).sort((a, b) => a.k.rank - b.k.rank)[0]
  if (topo.ccds.length > 1)
    for (const c of topo.ccds) {
      const lps = c.cores.flatMap((k) => k.threads)
      const tags = [c.vCache && topo.ccds.some((o) => !o.vCache) ? 'V-Cache' : '', topo.rankingAvailable && top?.ccd === c.id ? 'fastest' : '']
        .filter(Boolean)
        .join(', ')
      out.push([`ccd${c.id}`, `CCD ${c.id} (threads ${Math.min(...lps)}–${Math.max(...lps)}${tags ? `, ${tags}` : ''})`])
    }
  if (topo.rankingAvailable) for (const n of [2, 4, 8]) if (n < topo.physical) out.push([`best-cores:${n}`, `Best ${n} cores`])
  return out
}

/** Context menu for one process. "Always" settings are stored in that process's quick rule. */
export function buildProcessMenu(p: ProcessRow, quick: Rule | undefined, topo: Topology | undefined, hybrid: boolean, h: Handlers): MenuEntry[] {
  const a = quick?.actions ?? {}
  const locked = p.protected
  const affinity = affinityPresets(topo, hybrid)

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
    {
      type: 'item',
      label: 'Game',
      glyph: '\uE7FC',
      disabled: locked || !h.setGame,
      submenu: [
        { type: 'item', label: 'Always a game', checked: h.gameKind === 'always', onClick: () => h.setGame?.('always') },
        { type: 'item', label: 'Never a game', checked: h.gameKind === 'never', onClick: () => h.setGame?.('never') },
        { type: 'sep' },
        { type: 'item', label: 'Let Autopilot detect it', checked: h.gameKind === 'auto', onClick: () => h.setGame?.('auto') },
      ],
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
