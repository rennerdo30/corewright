import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { time } from '../format'
import { usePageVisible } from '../hooks'
import type { EngineEvent } from '../types'

const KINDS: EngineEvent['kind'][] = ['applied', 'reverted', 'failed', 'drift', 'info']

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
    <div className="page">
      <div className="toolbar">
        <h1>Activity</h1>
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
          <table className="log">
            <thead>
              <tr>
                <th>Time</th>
                <th>Event</th>
                <th>Process</th>
                <th>Setting</th>
                <th>Rule</th>
                <th>Detail</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((e) => (
                <tr key={e.seq}>
                  <td className="mono muted">{time(e.time)}</td>
                  <td>
                    <span className={`tag ${e.kind}`}>{e.kind}</span>
                  </td>
                  <td>
                    {e.process} {e.pid ? <span className="muted tiny">{e.pid}</span> : null}
                  </td>
                  <td className="mono">{e.slot ? `${e.slot} = ${e.value}` : ''}</td>
                  <td className="muted">{e.rule}</td>
                  <td className="muted">{e.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
