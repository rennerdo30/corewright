import { useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api'
import { Dot, PageHeader, joinAnd, plural } from '../components/PageHeader'
import { levelLabel } from '../host'
import { netPriorityLabel } from '../networkPriority'
import { usePageVisible } from '../hooks'
import { OWNER_LABELS, ownerOfName } from '../roles'
import type { EngineEvent } from '../types'

type Kind = EngineEvent['kind']
type Filter = 'all' | 'changes' | 'failed' | 'drift' | 'info'

const SLOT_LABELS: Record<string, string> = {
  cpuPriority: 'CPU priority',
  affinity: 'CPU cores',
  ioPriority: 'I/O priority',
  gpuPriority: 'GPU priority',
  memoryPriority: 'Memory priority',
  efficiencyMode: 'efficiency mode',
  memoryLimitMb: 'memory cap',
  workingSetLimitMb: 'RAM cap',
  networkBlocked: 'network access',
  uploadLimitMbps: 'upload limit',
  networkPriority: 'network priority',
}

const FILTERS: { id: Filter; label: string; kinds: Kind[] }[] = [
  { id: 'all', label: 'Everything', kinds: ['applied', 'reverted', 'failed', 'drift', 'info'] },
  { id: 'changes', label: 'Changes', kinds: ['applied', 'reverted'] },
  { id: 'failed', label: 'Failed', kinds: ['failed'] },
  { id: 'drift', label: 'Pushed back', kinds: ['drift'] },
  { id: 'info', label: 'Service', kinds: ['info'] },
]

const KIND_ROLE: Record<Kind, string> = { applied: 'applied', reverted: 'reverted', failed: 'failed', drift: 'drift', info: 'info' }

const value = (e: EngineEvent) => {
  if (!e.value) return ''
  if (e.slot === 'memoryLimitMb' || e.slot === 'workingSetLimitMb') return e.value === '0' ? 'off' : `${e.value} MB`
  if (e.slot === 'affinity') return `threads ${e.value}`
  if (e.slot === 'networkBlocked') return e.value === 'blocked' ? 'blocked' : 'allowed'
  if (e.slot === 'uploadLimitMbps') return e.value === 'none' ? 'no limit' : e.value
  if (e.slot === 'networkPriority') return e.value === 'default' ? 'Default' : netPriorityLabel(Number(e.value.replace('DSCP ', '')))
  if (e.slot === 'efficiencyMode') return e.value === 'on' || e.value === 'true' ? 'on' : e.value === 'off' || e.value === 'false' ? 'off' : e.value
  return levelLabel(e.value) || e.value
}

/** One event as a sentence: "Set CPU priority to Below normal", "Put I/O priority back to Normal". */
function Sentence({ e }: { e: EngineEvent }) {
  const what = e.slot ? SLOT_LABELS[e.slot] ?? e.slot : ''
  const v = value(e)
  switch (e.kind) {
    case 'applied':
      return (
        <>
          Set {what} to <strong>{v}</strong>
        </>
      )
    case 'reverted':
      return (
        <>
          Put {what} back to <strong>{v}</strong>
        </>
      )
    case 'failed':
      return (
        <>
          Could not {e.message?.startsWith('revert') ? 'restore' : 'set'} {what}
          {v ? (
            <>
              {' '}
              to <strong>{v}</strong>
            </>
          ) : null}
          {e.message ? <span className="act-why">: {e.message.replace(/^revert failed: /, '')}</span> : null}
        </>
      )
    case 'drift':
      return (
        <>
          The process changed its {what}; set it to <strong>{v}</strong> again
          {e.message ? <span className="act-why"> ({e.message.replace(/^process changed its priority; /, '')})</span> : null}
        </>
      )
    default:
      return <>{e.message ? e.message.charAt(0).toUpperCase() + e.message.slice(1) : 'Service event'}</>
  }
}

/** Several changes of one rule on one process at the same moment, as one sentence. */
function Batch({ items }: { items: EngineEvent[] }) {
  const back = items[0].kind === 'reverted'
  const parts = items.map((e) => (
    <span key={e.seq}>
      {e.slot ? SLOT_LABELS[e.slot] ?? e.slot : ''} {back ? 'back ' : ''}to <strong>{value(e)}</strong>
    </span>
  ))
  return (
    <>
      {back ? 'Put ' : 'Set '}
      {joinAnd(parts)}
    </>
  )
}

const clock = (ms: number) => new Date(ms).toLocaleTimeString(undefined, { hour12: false, hour: '2-digit', minute: '2-digit' })
const seconds = (ms: number) => new Date(ms).toLocaleTimeString(undefined, { hour12: false })

export function Activity() {
  const [events, setEvents] = useState<EngineEvent[]>([])
  const [filter, setFilter] = useState<Filter>('all')
  const [search, setSearch] = useState('')
  const [now, setNow] = useState(() => Date.now())
  const last = useRef(0)
  const visible = usePageVisible()

  useEffect(() => {
    if (!visible) return
    let stopped = false
    let timer: number | undefined
    const run = async () => {
      try {
        const r = await api.events(last.current)
        if (!stopped && r.events.length) {
          last.current = r.last
          setEvents((prev) => [...r.events.reverse(), ...prev].slice(0, 1000))
        }
      } catch {
        /* keep polling */
      }
      if (!stopped) {
        setNow(Date.now())
        timer = window.setTimeout(run, 1500)
      }
    }
    run()
    return () => {
      stopped = true
      window.clearTimeout(timer)
    }
  }, [visible])

  const counts = useMemo(() => {
    const c: Record<Filter, number> = { all: events.length, changes: 0, failed: 0, drift: 0, info: 0 }
    for (const e of events) for (const f of FILTERS) if (f.id !== 'all' && f.kinds.includes(e.kind)) c[f.id]++
    return c
  }, [events])

  // The headline: the last ten minutes, in words.
  const recent = useMemo(() => {
    const since = now - 10 * 60_000
    const r = events.filter((e) => e.time >= since)
    return {
      applied: r.filter((e) => e.kind === 'applied').length,
      reverted: r.filter((e) => e.kind === 'reverted').length,
      failed: r.filter((e) => e.kind === 'failed').length,
      processes: new Set(r.filter((e) => e.pid).map((e) => e.pid)).size,
    }
  }, [events, now])

  const q = search.trim().toLowerCase()
  const kinds = FILTERS.find((f) => f.id === filter)!.kinds
  const shown = events.filter(
    (e) => kinds.includes(e.kind) && (!q || e.process.toLowerCase().includes(q) || (e.rule ?? '').toLowerCase().includes(q) || String(e.pid) === q),
  )

  // Group by minute so the list reads like a timeline.
  const groups = useMemo(() => {
    const out: { minute: string; items: EngineEvent[][] }[] = []
    for (const e of shown) {
      const m = clock(e.time)
      if (out.length === 0 || out[out.length - 1].minute !== m) out.push({ minute: m, items: [] })
      const rows = out[out.length - 1].items
      const prev = rows[rows.length - 1]
      const mergeable = (e.kind === 'applied' || e.kind === 'reverted') && prev && prev[0].kind === e.kind && prev[0].pid === e.pid && prev[0].rule === e.rule && Math.abs(prev[0].time - e.time) < 1000
      if (mergeable) prev.push(e)
      else rows.push([e])
    }
    return out
  }, [shown])

  const parts: React.ReactNode[] = []
  if (recent.applied) parts.push(<Dot key="a" role="applied">{plural(recent.applied, 'setting')} changed</Dot>)
  if (recent.reverted) parts.push(<Dot key="r" role="reverted">{recent.reverted} put back</Dot>)
  if (recent.failed) parts.push(<Dot key="f" role="failed">{recent.failed} failed</Dot>)

  return (
    <div className="page dense activity">
      <PageHeader title="Activity">
        {events.length === 0 ? (
          <>Every change Corewright makes to a process shows up here, with the rule that caused it.</>
        ) : parts.length === 0 ? (
          <>Quiet for the last ten minutes. Settings stay where the rules put them.</>
        ) : (
          <>
            In the last ten minutes, on {plural(recent.processes, 'process', 'processes')}: {joinAnd(parts)}.
          </>
        )}
      </PageHeader>

      <div className="toolbar">
        <div className="seg">
          {FILTERS.map((f) => (
            <button key={f.id} className={filter === f.id ? 'on' : ''} onClick={() => setFilter(f.id)}>
              {f.label}
              {counts[f.id] > 0 && <span className="seg-count">{counts[f.id]}</span>}
            </button>
          ))}
        </div>
        <div className="search-box">
          <span className="glyph">{''}</span>
          <input placeholder="Filter by process, rule or PID" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>

      {shown.length === 0 ? (
        <div className="card act-empty">
          <span className="glyph">{''}</span>
          <div>
            <div className="setting-title">{events.length === 0 ? 'Nothing has happened yet' : 'Nothing matches this filter'}</div>
            <div className="setting-desc">
              {events.length === 0
                ? 'When a rule or Autopilot changes a process, the change and its reason appear here. Changes are put back as soon as no rule wants them.'
                : 'Pick another filter or clear the search.'}
            </div>
          </div>
        </div>
      ) : (
        <div className="card flush act-list">
          {groups.map((g) => (
            <section key={g.minute + g.items[0][0].seq}>
              <div className="act-minute">{g.minute}</div>
              {g.items.map((batch) => {
                const e = batch[0]
                const owner = ownerOfName(e.rule)
                return (
                  <div key={e.seq} className={`act-row kind-${e.kind}`}>
                    <span className="act-time">{seconds(e.time)}</span>
                    <span className={`act-kind role-${KIND_ROLE[e.kind]}`} title={e.kind}>
                      <i />
                    </span>
                    <span className="act-proc" title={e.pid ? `${e.process} (PID ${e.pid})` : undefined}>
                      {e.process || 'Corewright'}
                      {e.pid ? <span className="secondary tiny"> {e.pid}</span> : null}
                    </span>
                    <span className="act-what">{batch.length > 1 ? <Batch items={[...batch].reverse()} /> : <Sentence e={e} />}</span>
                    {e.rule ? (
                      <span className="act-rule" title={`${OWNER_LABELS[owner]}: ${e.rule}`}>
                        <Dot role={owner}>{e.rule.replace(/^(Autopilot|Quick|Process Lasso): /, '')}</Dot>
                      </span>
                    ) : (
                      <span />
                    )}
                  </div>
                )
              })}
            </section>
          ))}
        </div>
      )}
    </div>
  )
}
