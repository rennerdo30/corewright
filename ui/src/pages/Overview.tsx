import { useMemo } from 'react'
import { api } from '../api'
import type { Page } from '../App'
import { Chart } from '../components/Chart'
import { bytes, percent } from '../format'
import { useDescriptions } from '../host'
import { usePoll } from '../hooks'
import type { SystemInfo, Topology } from '../types'

const rate = (n: number) => (n < 1024 ? `${Math.round(n)} B/s` : `${bytes(n)}/s`)
const bits = (n: number) => {
  const b = n * 8
  return b >= 1e9 ? `${(b / 1e9).toFixed(1)} Gbps` : b >= 1e6 ? `${(b / 1e6).toFixed(1)} Mbps` : `${(b / 1e3).toFixed(0)} Kbps`
}

export function Overview({ onOpen }: { onOpen: (p: Page) => void }) {
  const sys = usePoll(api.system, 1000)
  const procs = usePoll(api.processes, 2000)
  const topo = usePoll(api.topology, 600_000)
  const s = sys.data
  const descriptions = useDescriptions((procs.data ?? []).map((p) => p.path))

  const top = useMemo(() => (procs.data ?? []).filter((p) => p.pid !== 0).sort((a, b) => b.cpu - a.cpu).slice(0, 8), [procs.data])
  const managed = useMemo(() => (procs.data ?? []).filter((p) => p.applied?.length).length, [procs.data])

  if (sys.error) return <div className="empty">{sys.error}</div>
  if (!s) return <div className="empty">Loading…</div>

  return (
    <div className="page dense overview">
      <h2 className="page-title">Overview</h2>
      <div className="metric-grid">
        <Metric title="CPU" value={percent(s.cpu)} sub={topo.data?.name}>
          <Chart time={s.historyTime} values={s.historyCpu} colorVar="--chart-1" max={100} height={90} format={(v) => `${v.toFixed(0)}%`} />
        </Metric>
        <Metric title="Memory" value={bytes(s.memoryUsed)} sub={`of ${bytes(s.memoryTotal, 0)}`}>
          <Chart time={s.historyTime} values={s.historyMemory} colorVar="--chart-2" max={100} height={90} format={(v) => `${v.toFixed(0)}%`} />
        </Metric>
        <Metric title="Disk" value={rate(s.ioBytesPerSec)} sub="all processes">
          <Chart time={s.historyTime} values={s.historyIo} colorVar="--chart-3" height={90} format={(v) => bytes(v, 0)} />
        </Metric>
        <Metric title="GPU" value={percent(s.gpu)} sub={s.gpuMemory ? `${bytes(s.gpuMemory)} dedicated` : 'no data yet'}>
          <Chart time={s.historyTime} values={s.historyGpu} colorVar="--chart-4" max={100} height={90} format={(v) => `${v.toFixed(0)}%`} />
        </Metric>
        <Metric title="Network" value={bits(s.netReceive + s.netSend)} sub={`↓ ${bits(s.netReceive)}  ↑ ${bits(s.netSend)}`}>
          <Chart time={s.historyTime} values={s.historyNetReceive.map((v, i) => v + (s.historyNetSend[i] ?? 0))} colorVar="--chart-5" height={90} format={(v) => bits(v)} />
        </Metric>
      </div>

      {topo.data && <CpuMap topo={topo.data} sys={s} />}

      <section className="card">
        <div className="card-head">
          <span>Top processes by CPU</span>
          <button className="link" onClick={() => onOpen('processes')}>
            All {s.processCount} processes · {managed} managed →
          </button>
        </div>
        <table className="mini">
          <tbody>
            {top.map((p) => (
              <tr key={p.pid}>
                <td>{(p.path && descriptions.get(p.path)) || p.name}</td>
                <td className="secondary">{p.name}</td>
                <td className="num">{percent(p.cpu)}</td>
                <td className="num secondary">{bytes(p.privateBytes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  )
}

function Metric({ title, value, sub, children }: { title: string; value: string; sub?: string; children: React.ReactNode }) {
  return (
    <section className="card metric">
      <div className="metric-head">
        <span className="metric-title">{title}</span>
        <strong>{value}</strong>
      </div>
      <div className="metric-sub secondary" title={sub}>
        {sub}
      </div>
      {children}
    </section>
  )
}

/**
 * CPU → CCD → core → thread, like Ryzen Master: each CCD with its L3, each physical core with its logical
 * processors and their live load. ★ marks the fastest core of a CCD by CPPC ranking, • the second fastest.
 */
function CpuMap({ topo, sys }: { topo: Topology; sys: SystemInfo }) {
  const load = (lp: number) => sys.threadUsage[lp] ?? 0
  return (
    <section className="card cpu-map">
      <div className="card-head">
        <span>
          {topo.name} · {topo.physical} cores · {topo.logical} threads
        </span>
        <span className="secondary tiny">
          {topo.rankingAvailable ? '★ fastest core per CCD   • second fastest (CPPC ranking)' : 'This CPU reports no core ranking.'}
        </span>
      </div>
      <div className="ccd-row">
        {topo.ccds.map((c) => {
          const lps = c.cores.flatMap((k) => k.threads)
          const avg = lps.reduce((sum, lp) => sum + load(lp), 0) / Math.max(1, lps.length)
          return (
            <div key={c.id} className="ccd">
              <div className="ccd-head">
                <strong>CCD {c.id}</strong>
                <span className="secondary">
                  {c.l3Bytes ? `${Math.round(c.l3Bytes / 1048576)} MB L3` : ''}
                  {c.vCache ? ' · 3D V-Cache' : ''}
                </span>
                <span className="ccd-load">{percent(avg, 0)}</span>
              </div>
              <div className="core-grid">
                {c.cores.map((k) => (
                  <div
                    key={k.id}
                    className={`core ${k.ccdRank === 1 ? 'best' : ''}`}
                    title={`Core ${k.id}${k.performance ? ` · CPPC ${k.performance}` : ''}${k.rank ? ` · rank ${k.rank} of ${topo.physical}` : ''}${k.tied ? ' (tied)' : ''}`}
                  >
                    <div className="core-head">
                      <span>Core {k.id}</span>
                      {k.ccdRank === 1 && <span className="star">★{k.tied ? '=' : ''}</span>}
                      {k.ccdRank === 2 && <span className="dot2">•</span>}
                    </div>
                    <div className="threads">
                      {k.threads.map((lp) => (
                        <div key={lp} className="thread" title={`Thread ${lp}: ${percent(load(lp), 0)}`}>
                          <div className="thread-fill" style={{ height: `${Math.max(2, load(lp))}%` }} />
                          <span className="thread-id">{lp}</span>
                          <span className="thread-load">{Math.round(load(lp))}</span>
                        </div>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )
        })}
      </div>
    </section>
  )
}
