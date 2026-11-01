import { useState } from 'react'
import { api } from '../api'
import { Spark, niceCeil } from '../components/Chart'
import { PageHeader } from '../components/PageHeader'
import { bytes, percent } from '../format'
import { usePoll } from '../hooks'
import type { Device } from '../types'

export const unavailable = 'not available'
export const rate = (n: number) => n < 1024 ? `${Math.round(n)} B/s` : `${bytes(n)}/s`
export const bits = (n: number) => n * 8 >= 1e9 ? `${(n * 8 / 1e9).toFixed(1)} Gbps` : `${(n * 8 / 1e6).toFixed(1)} Mbps`
export const display = (n: number | undefined | null, format: (n: number) => string) => n == null ? unavailable : format(n)
export function deviceSeries(d?: Device) {
  const h = d?.history
  return { time: h?.time ?? [], values: h?.primary.map((v, i) => d?.kind === 'gpu' ? v : v == null || h.secondary[i] == null ? null : v + h.secondary[i]!) ?? [] }
}
export const deviceLabel = (d: Device) => d.kind === 'disk' ? `Disk ${d.index} · ${d.name}` : d.alias || d.name
const lifetime = (units?: string) => units == null ? unavailable : `${(Number(BigInt(units) * 512000n) / 1e12).toFixed(2)} TB`

export function Devices() {
  const [hidden, setHidden] = useState(false)
  const data = usePoll(() => api.devices('*', hidden), 1000)
  const devices = data.data?.devices ?? []
  return <div className="page dense devices">
    <PageHeader title="Devices" actions={<label className="device-filter"><input type="checkbox" checked={hidden} onChange={e => { setHidden(e.target.checked); data.reload() }} /> Show hidden adapters</label>}>
      Your disks, graphics and connections. Live values update while this window is visible.
    </PageHeader>
    {data.error && <div className="banner">{data.error}</div>}
    {!data.data && !data.error && <div className="empty">Loading devices…</div>}
    {(['disk', 'gpu', 'network'] as const).map(kind => <section className="section" key={kind}>
      <h3>{kind === 'disk' ? 'Disks' : kind === 'gpu' ? 'Graphics' : 'Network adapters'} <span className="secondary">· {devices.filter(d => d.kind === kind).length}</span></h3>
      <div className="device-grid">{devices.filter(d => d.kind === kind).map(d => <DeviceCard key={d.id} d={d} sampling={data.data?.sampling ?? false} />)}</div>
      {data.data && !devices.some(d => d.kind === kind) && <p className="secondary">No {kind === 'gpu' ? 'graphics adapters' : kind === 'disk' ? 'disks' : 'network adapters'} reported by Windows.</p>}
    </section>)}
    <p className="secondary tiny">Inventory refreshes on request, at most every three minutes. Storage health refreshes once a minute while viewing. Some sensors require the installed service or driver support; unavailable readings are never estimated.</p>
  </div>
}
function DeviceCard({ d, sampling }: { d: Device; sampling: boolean }) {
  const v = d.values, h = d.health, series = deviceSeries(d), gpu = d.kind === 'gpu', disk = d.kind === 'disk'
  const format = gpu ? percent : disk ? rate : bits
  const total = gpu ? v.load : v.read == null || v.write == null ? undefined : v.read + v.write
  const peak = Math.max(0, ...series.values.filter((n): n is number => n != null))
  const top = gpu ? 100 : niceCeil(Math.max(peak * 1.1, 1e6))
  const detail = (label: string, value: string) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>
  const identity = disk ? `${d.bus ?? unavailable} · ${display(d.size, bytes)} · ${d.volumes?.join(', ') || 'Volumes not available'}` : gpu ? `${display(d.dedicatedMemory, bytes)} dedicated · ${display(d.sharedMemory, bytes)} shared` : `${d.type ?? unavailable} · ${d.connected == null ? unavailable : d.connected ? 'Connected' : 'Disconnected'} · ${display(d.receiveLinkSpeed, n => bits(n / 8))} link`
  return <article className="card device-card">
    <div className="device-heading"><span className="glyph">{disk ? '\uEDA2' : gpu ? '\uE7F4' : '\uE968'}</span><div><h4>{deviceLabel(d)}</h4><p className="secondary tiny">{identity}</p>{d.kind === 'network' && <p className="secondary tiny">{d.name}</p>}</div></div>
    <div className="device-live"><strong>{sampling ? display(total, format) : unavailable}</strong><span className="secondary tiny">{gpu ? 'GPU load' : disk ? 'read + write' : 'receive + send'}</span></div>
    <Spark {...series} colorVar={disk ? '--chart-3' : gpu ? '--chart-4' : '--chart-5'} top={top} topLabel={format(top)} midLabel={format(top / 2)} format={format} height={64} />
    <dl className="device-details">
      {gpu ? <>{detail('Dedicated in use', display(v.dedicatedUsed, bytes))}{detail('Shared in use', display(v.sharedUsed, bytes))}{detail('Temperature', display(h.temperature, n => `${n.toFixed(0)} °C`))}{detail('Fan', display(h.fanRpm, n => `${n} RPM`))}{detail('Power (% of limit)', display(h.powerPercent, percent))}{detail('Engine 0 clock', display(h.engineClockMhz, n => `${n.toFixed(0)} MHz`))}</> : <>
        {detail(disk ? 'Read' : 'Receive', display(v.read, format))}{detail(disk ? 'Write' : 'Send', display(v.write, format))}
        {disk && <>{detail('Active time', display(v.active, percent))}{detail('Temperature', display(h.temperature, n => `${n.toFixed(0)} °C`))}{detail('Wear', display(h.wearPercent, n => `${n} % used`))}{detail('Available spare', display(h.sparePercent, percent))}{detail('Health warning', h.criticalWarning == null ? unavailable : h.criticalWarning === 0 ? 'None reported' : `Warning flags: ${h.criticalWarning}`)}{detail('Media errors', h.mediaErrors ?? unavailable)}{detail('Power-on hours', h.powerOnHours ?? unavailable)}{detail('Unsafe shutdowns', h.unsafeShutdowns ?? unavailable)}{detail('Lifetime read', lifetime(h.dataUnitsRead))}{detail('Lifetime written', lifetime(h.dataUnitsWritten))}</>}
        {!disk && detail('Send link speed', display(d.sendLinkSpeed, n => bits(n / 8)))}
      </>}
    </dl>
  </article>
}
