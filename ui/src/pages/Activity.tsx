import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { time } from '../format'
import { levelLabel } from '../host'
import { usePageVisible } from '../hooks'
import type { EngineEvent } from '../types'

const KINDS: EngineEvent['kind'][] = ['applied', 'reverted', 'failed', 'drift', 'info']

const SLOT_LABELS: Record<string, string> = {
  cpuPriority: 'CPU priority',
  affinity: 'CPU cores',
  ioPriority: 'I/O priority',
  gpuPriority: 'GPU priority',
  memoryPriority: 'Memory priority',
  efficiencyMode: 'Efficiency mode',
  memoryLimitMb: 'Memory cap',
  workingSetLimitMb: 'RAM cap',
}

/** "cpuPriority = belowNormal" → "CPU priority → Below normal". */
const change = (e: EngineEvent) => (e.slot ? `${SLOT_LABELS[e.slot] ?? e.slot} → ${levelLabel(e.value) || e.value}` : '')

export function Activity() {
  const [events, setEvents] = useState<EngineEvent[]>([])
  const [filter, setFilter] = useState<Set<string>>(new Set(KINDS))
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
      if (!stopped) timer = window.setTimeout(run, 1500)
    }
    run()
    return () => {
      stopped = true
      window.clearTimeout(timer)
    }
  }, [visible])

  const shown = events.filter((e) => filter.has(e.kind))

  return (
    <div className="page dense">
      <div className="toolbar">
        <h2 className="page-title">Activity</h2>
        <div className="chips">
          {KINDS.map((k) => (
            <button
              key={k}
              className={`chip ${k} ${filter.has(k) ? 'on' : ''}`}
              onClick={() =>
                setFilter((f) => {
                  const n = new Set(f)
                  if (n.has(k)) n.delete(k)
                  else n.add(k)
                  return n
                })
              }
            >
              {k}
            </button>
          ))}
        </div>
      </div>
      {shown.length === 0 ? (
        <div className="empty">No activity yet. Changes Corewright makes to processes appear here.</div>
      ) : (
        <div className="card flush">
          <table className="log compact">
            <colgroup>
              <col style={{ width: 84 }} />
              <col style={{ width: 84 }} />
              <col style={{ width: '22%' }} />
              <col style={{ width: '24%' }} />
              <col style={{ width: '22%' }} />
              <col />
            </colgroup>
            <thead>
              <tr>
                <th>Time</th>
                <th>Event</th>
                <th>Process</th>
                <th>Change</th>
                <th>Rule</th>
                <th>Detail</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((e) => (
                <tr key={e.seq}>
                  <td className="secondary">{time(e.time)}</td>
                  <td>
                    <span className={`tag ${e.kind}`}>{e.kind}</span>
                  </td>
                  <td title={`${e.process} (${e.pid})`}>
                    {e.process} {e.pid ? <span className="secondary tiny">{e.pid}</span> : null}
                  </td>
                  <td title={change(e)}>{change(e)}</td>
                  <td className="secondary" title={e.rule}>
                    {e.rule}
                  </td>
                  <td className="secondary" title={e.message}>
                    {e.message}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
