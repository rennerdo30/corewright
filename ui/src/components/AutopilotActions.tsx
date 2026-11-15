import { useState } from 'react'
import { levelLabel } from '../host'
import { ownerOf } from '../roles'
import type { AutopilotAction, ProcessRow } from '../types'

/**
 * Everything Autopilot does right now (PM-56, owner 2026-11-15: "should show everything the autopilot does as an
 * action"): one group per Autopilot rule, busiest processes first, each with what changed from what to what.
 */

const RULES: { id: string; title: string; what: string }[] = [
  { id: 'autopilot-game', title: 'Game', what: 'The game and everything it started get a higher priority.' },
  { id: 'autopilot-voice', title: 'Voice and streaming', what: 'Kept responsive while you play.' },
  { id: 'autopilot-game-background', title: 'Out of the game’s way', what: 'Busy programs get a lower priority, and leave the game’s CCD, while you play.' },
  { id: 'autopilot-game-builds', title: 'Builds while gaming', what: 'Build tools move off the game’s CCD.' },
  { id: 'autopilot-build-tools', title: 'Compilers and build tools', what: 'Run below normal with low I/O priority, so your apps stay responsive.' },
  { id: 'autopilot-balance', title: 'Held back for hogging the CPU', what: 'Background programs using a lot of CPU while the PC is busy get a lower priority until they calm down.' },
  { id: 'autopilot-background-learned', title: 'Learned background programs', what: 'Programs that are never in front stay off your strongest cores.' },
  { id: 'autopilot-windows-core', title: 'Kept off core 0', what: 'Core 0 stays free for Windows: interrupts, input, audio and the desktop.' },
]

const SLOT_LABELS: Record<string, string> = {
  cpuPriority: 'CPU priority',
  affinity: 'Cores',
  ioPriority: 'I/O priority',
  gpuPriority: 'GPU priority',
  memoryPriority: 'Memory priority',
  efficiencyMode: 'Efficiency mode',
  memoryLimitMb: 'Memory cap',
  workingSetLimitMb: 'RAM cap',
  networkBlocked: 'Network',
  uploadLimitMbps: 'Upload limit',
  networkPriority: 'Network priority',
}

const VISIBLE = 5

const value = (slot: string, v: string) =>
  slot === 'affinity' ? v : slot === 'efficiencyMode' ? ({ on: 'On', off: 'Off', auto: 'Windows decides' }[v] ?? v) : levelLabel(v) || v

function since(ms: number): string {
  if (!ms) return ''
  const s = Math.max(0, (Date.now() - ms) / 1000)
  if (s < 90) return `${Math.round(s)} s`
  if (s < 5400) return `${Math.round(s / 60)} min`
  if (s < 172_800) return `${Math.round(s / 3600)} h`
  return `${Math.round(s / 86_400)} d`
}

interface Row {
  pid: number
  process: string
  changes: AutopilotAction[]
  sinceMs: number
  reason?: string | null
  cpu: number
}

interface Group {
  ruleId: string
  title: string
  what: string
  rows: Row[]
  /** Changes every row shares, shown once in the header ("CPU priority Normal → Below normal"); rows list only the rest. */
  common: { slot: string; text: string }[]
}

export function groupActions(actions: AutopilotAction[], procs: ProcessRow[]): Group[] {
  const cpu = new Map(procs.map((p) => [p.pid, p.cpu]))
  const byRule = new Map<string, Map<number, Row>>()
  const names = new Map<string, string>()
  for (const a of actions) {
    names.set(a.ruleId, a.rule)
    let rows = byRule.get(a.ruleId)
    if (!rows) byRule.set(a.ruleId, (rows = new Map()))
    let row = rows.get(a.pid)
    if (!row) rows.set(a.pid, (row = { pid: a.pid, process: a.process, changes: [], sinceMs: a.sinceMs, reason: a.reason, cpu: cpu.get(a.pid) ?? 0 }))
    row.changes.push(a)
    if (a.sinceMs && (!row.sinceMs || a.sinceMs < row.sinceMs)) row.sinceMs = a.sinceMs
  }
  const order = (id: string) => {
    const i = RULES.findIndex((r) => r.id === id)
    return i < 0 ? RULES.length : i
  }
  return [...byRule.entries()]
    .sort(([a], [b]) => order(a) - order(b) || a.localeCompare(b))
    .map(([ruleId, map]) => {
      // Busiest first in whole-percent steps, so rows do not reshuffle on every poll.
      const rows = [...map.values()].sort((a, b) => Math.round(b.cpu) - Math.round(a.cpu) || a.process.localeCompare(b.process) || a.pid - b.pid)
      const meta = RULES.find((r) => r.id === ruleId)
      // A change is common when every row has it with the same target; its "from" is named only when that is shared too.
      const common: { slot: string; text: string }[] = []
      for (const c of rows[0].changes) {
        const all = rows.map((r) => r.changes.find((x) => x.slot === c.slot))
        if (rows.length < 2 || all.some((x) => !x || x.to !== c.to)) continue
        const label = SLOT_LABELS[c.slot] ?? c.slot
        const same = all.every((x) => x!.from === c.from)
        common.push({ slot: c.slot, text: same && c.from === c.to ? `${label} ${value(c.slot, c.to)} (already)` : `${label} ${same ? `${value(c.slot, c.from)} ` : ''}→ ${value(c.slot, c.to)}` })
      }
      return { ruleId, title: meta?.title ?? names.get(ruleId) ?? ruleId, what: meta?.what ?? '', rows, common }
    })
}

export function AutopilotActions({ actions, procs, mode }: { actions?: AutopilotAction[]; procs: ProcessRow[]; mode: 'off' | 'observe' | 'on' }) {
  if (!actions) return <div className="ap-empty">Loading…</div>
  const groups = groupActions(actions, procs)
  if (groups.length === 0)
    return (
      <div className="ap-empty">
        {mode === 'off'
          ? 'Autopilot is off, so it changes nothing.'
          : mode === 'observe'
            ? 'Observe mode changes nothing; “What Autopilot would do” above lists what it would change.'
            : 'Autopilot is not changing any process right now. Everything runs with the settings Windows and your own rules gave it.'}
      </div>
    )
  return (
    <div className="aa-list">
      {groups.map((g) => (
        <ActionGroup key={g.ruleId} group={g} />
      ))}
    </div>
  )
}

function ActionGroup({ group: g }: { group: Group }) {
  const [all, setAll] = useState(false)
  const shown = all ? g.rows : g.rows.slice(0, VISIBLE)
  const hidden = g.rows.length - shown.length
  const role = ownerOf(g.ruleId)
  return (
    <section className={`aa-group role-${role}`}>
      <header className="aa-head">
        <span className="aa-bar" aria-hidden />
        <div className="aa-title">
          <strong>{g.title}</strong>
          <span className="aa-n">{g.rows.length} {g.rows.length === 1 ? 'process' : 'processes'}</span>
          {g.common.map((c) => (
            <span key={c.slot} className="aa-chip">{c.text}</span>
          ))}
        </div>
        {g.what && <p className="secondary tiny aa-what">{g.what}</p>}
      </header>
      <table className="ap-table aa-table">
        <tbody>
          {shown.map((r) => (
            <tr key={r.pid}>
              <td className="aa-proc">
                {r.process}
                <span className="secondary tiny"> {r.pid}</span>
              </td>
              <td className="num aa-cpu" title="CPU use now, share of the whole machine">
                {r.cpu >= 0.05 ? `${r.cpu.toFixed(1)} %` : <span className="secondary">0 %</span>}
              </td>
              <td className="aa-changes">
                {r.changes.filter((c) => !g.common.some((x) => x.slot === c.slot)).map((c) => (
                  <span key={c.slot} className="aa-change">
                    <span className="secondary">{SLOT_LABELS[c.slot] ?? c.slot}</span>{' '}
                    {c.from === c.to ? (
                      <>
                        <strong>{value(c.slot, c.to)}</strong> <span className="secondary">(already)</span>
                      </>
                    ) : (
                      <>
                        {value(c.slot, c.from)} <span className="aa-arrow">→</span> <strong>{value(c.slot, c.to)}</strong>
                      </>
                    )}
                  </span>
                ))}
                {r.reason && <div className="secondary tiny aa-why" title={r.reason}>{r.reason}</div>}
              </td>
              <td className="num secondary aa-since" title="Changed this long ago">
                {since(r.sinceMs)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {g.rows.length > VISIBLE && (
        <button className="btn subtle aa-more" onClick={() => setAll(!all)}>
          {all ? 'Show fewer' : `Show all ${g.rows.length} (${hidden} more)`}
        </button>
      )}
    </section>
  )
}
