import { useMemo } from 'react'
import { api } from '../api'
import type { Page } from '../App'
import { Spark, niceCeil } from '../components/Chart'
import { DieMap } from '../components/DieMap'
import { PageHeader, plural } from '../components/PageHeader'
import { bytes, percent } from '../format'
import { useDescriptions } from '../host'
import { usePoll } from '../hooks'

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

  // Scales: percentages and memory have a real maximum; disk and network use a round number above the 5-minute peak.
  const max = (a: number[]) => a.reduce((m, v) => (v > m ? v : m), 0)
  const net = s.historyNetReceive.map((v, i) => v + (s.historyNetSend[i] ?? 0))
  const peakCpu = max(s.historyCpu)
  const peakGpu = max(s.historyGpu)
  const peakIo = max(s.historyIo)
  const peakNet = max(net)
  // Round in the unit the label shows (MB/s, Mbps), so the top line reads 100 MB/s, not 95.4 MB/s.
  const ioTop = niceCeil(Math.max((peakIo * 1.1) / 1048576, 1)) * 1048576
  const netTop = (niceCeil(Math.max((peakNet * 1.1 * 8) / 1e6, 1)) * 1e6) / 8

  return (
    <div className="page dense overview">
      <PageHeader title="Overview">
        {managed ? <>Corewright manages {plural(managed, 'process', 'processes')} right now.</> : <>Corewright is not changing any process right now.</>}
      </PageHeader>
      <div className="metric-grid">
        <Metric title="CPU" value={percent(s.cpu)} sub={topo.data?.name} peak={`peak ${pct(peakCpu)} in 5 min`}>
          <Spark time={s.historyTime} values={s.historyCpu} colorVar="--chart-1" top={100} topLabel="100 %" midLabel="50 %" format={pct} />
        </Metric>
        <Metric
          title="Memory"
          value={bytes(s.memoryUsed)}
          sub={`of ${bytes(s.memoryTotal, 0)}, ${Math.round((100 * s.memoryUsed) / s.memoryTotal)} % in use`}
          peak={`${bytes(s.memoryTotal - s.memoryUsed)} free`}
        >
          <Spark time={s.historyTime} values={s.historyMemory} colorVar="--chart-2" top={100} topLabel={bytes(s.memoryTotal, 0)} midLabel={bytes(s.memoryTotal / 2, 0)} format={(v) => bytes((v / 100) * s.memoryTotal)} />
        </Metric>
        <Metric title="Disk" value={rate(s.ioBytesPerSec)} sub="read and write, all processes" peak={`peak ${rate(peakIo)} in 5 min`}>
          <Spark time={s.historyTime} values={s.historyIo} colorVar="--chart-3" top={ioTop} topLabel={rate(ioTop)} midLabel={rate(ioTop / 2)} format={rate} />
        </Metric>
        <Metric title="GPU" value={percent(s.gpu)} sub={s.gpuMemory ? `${bytes(s.gpuMemory)} video memory in use` : 'no data yet'} peak={`peak ${pct(peakGpu)} in 5 min`}>
          <Spark time={s.historyTime} values={s.historyGpu} colorVar="--chart-4" top={100} topLabel="100 %" midLabel="50 %" format={pct} />
        </Metric>
        <Metric title="Network" value={bits(s.netReceive + s.netSend)} sub={`\u2193 ${bits(s.netReceive)}   \u2191 ${bits(s.netSend)}`} peak={`peak ${bits(peakNet)} in 5 min`}>
          <Spark time={s.historyTime} values={net} colorVar="--chart-5" top={netTop} topLabel={bits(netTop)} midLabel={bits(netTop / 2)} format={bits} />
        </Metric>
      </div>

      {topo.data && (
        <section className="card ov-map">
          <div className="card-head">
            <span>{topo.data.name}</span>
            <span className="secondary tiny">
              {topo.data.physical} cores, {topo.data.logical} threads
            </span>
          </div>
          <DieMap topo={topo.data} load={s.threadUsage} legend={false} />
        </section>
      )}

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

function Metric({ title, value, sub, peak, children }: { title: string; value: string; sub?: string; peak?: string; children: React.ReactNode }) {
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
      <div className="metric-foot">
        <span>5 min</span>
        <span>{peak}</span>
      </div>
    </section>
  )
}

const pct = (v: number) => `${v.toFixed(0)} %`
