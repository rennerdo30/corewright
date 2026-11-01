import { useMemo } from 'react'
import { api } from '../api'
import type { Page } from '../App'
import { Chart } from '../components/Chart'
import { bytes, percent, rate } from '../format'
import { usePoll } from '../hooks'

export function Overview({ onOpen }: { onOpen: (p: Page) => void }) {
  const sys = usePoll(api.system, 1000)
  const procs = usePoll(api.processes, 2000)
  const s = sys.data

  const top = useMemo(
    () => (procs.data ?? []).filter((p) => p.pid !== 0).sort((a, b) => b.cpu - a.cpu).slice(0, 8),
    [procs.data],
  )
  const managed = useMemo(() => (procs.data ?? []).filter((p) => p.applied?.length), [procs.data])

  if (sys.error) return <div className="empty">{sys.error}</div>
  if (!s) return <div className="empty">Loading…</div>

  return (
    <div className="page">
      <h1>Overview</h1>
      <div className="grid3">
        <section className="card">
          <div className="card-head">
            <span>CPU</span>
            <strong>{percent(s.cpu)}</strong>
          </div>
          <Chart time={s.historyTime} values={s.historyCpu} colorVar="--chart-1" max={100} format={(v) => `${v.toFixed(0)}%`} />
        </section>
        <section className="card">
          <div className="card-head">
            <span>Memory</span>
            <strong>
              {bytes(s.memoryUsed)} <span className="muted">/ {bytes(s.memoryTotal, 0)}</span>
            </strong>
          </div>
          <Chart time={s.historyTime} values={s.historyMemory} colorVar="--chart-2" max={100} format={(v) => `${v.toFixed(0)}%`} />
        </section>
        <section className="card">
          <div className="card-head">
            <span>I/O</span>
            <strong>{rate(s.ioBytesPerSec)}</strong>
          </div>
          <Chart time={s.historyTime} values={s.historyIo} colorVar="--chart-3" format={(v) => bytes(v, 0)} />
        </section>
      </div>

      <div className="grid2">
        <section className="card">
          <div className="card-head">
            <span>Top processes by CPU</span>
            <button className="link" onClick={() => onOpen('processes')}>
              All {s.processCount} processes →
            </button>
          </div>
          <table className="mini">
            <tbody>
              {top.map((p) => (
                <tr key={p.pid}>
                  <td className="name">
                    {p.name}
                    {p.foreground && <span className="tag teal">foreground</span>}
                  </td>
                  <td className="num">{percent(p.cpu)}</td>
                  <td className="num muted">{bytes(p.privateBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
        <section className="card">
          <div className="card-head">
            <span>CPU topology</span>
          </div>
          <dl className="facts">
            <dt>Logical processors</dt>
            <dd>{s.cores.logicalProcessors}</dd>
            <dt>Performance cores</dt>
            <dd className="mono">{s.cores.performanceCores}</dd>
            <dt>Efficiency cores</dt>
            <dd className="mono">{s.cores.hybrid ? s.cores.efficiencyCores : 'none (not a hybrid CPU)'}</dd>
            <dt>Managed processes</dt>
            <dd>
              {managed.length}{' '}
              <button className="link" onClick={() => onOpen('rules')}>
                Edit rules →
              </button>
            </dd>
          </dl>
        </section>
      </div>
    </div>
  )
}
