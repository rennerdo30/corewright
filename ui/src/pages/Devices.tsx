import { useState } from 'react'
import { api } from '../api'
import { Spark, niceCeil } from '../components/Chart'
import { PageHeader, plural } from '../components/PageHeader'
import { Toggle } from '../components/SettingsControls'
import { bytes } from '../format'
import { usePoll } from '../hooks'
import type { Device } from '../types'

export const unavailable = 'not available'
export const rate = (n: number) => (n < 1024 ? `${Math.round(n)} B/s` : `${bytes(n)}/s`)
export const bits = (n: number) => {
  const b = n * 8
  return b >= 1e9 ? `${(b / 1e9).toFixed(1)} Gbps` : b >= 1e6 ? `${(b / 1e6).toFixed(1)} Mbps` : `${(b / 1e3).toFixed(0)} Kbps`
}
export const display = (n: number | undefined | null, format: (n: number) => string) => (n == null ? unavailable : format(n))

/** The device's chart: GPU load, or read + write (receive + send) per second. */
export function deviceSeries(d?: Device) {
  const h = d?.history
  return {
    time: h?.time ?? [],
    values:
      h?.primary.map((v, i) => (d?.kind === 'gpu' ? v : v == null || h.secondary[i] == null ? null : v + h.secondary[i]!)) ?? [],
  }
}

/** Short names for selectors: "Disk 0, Northstar N4 Pro (C:)", "GPU 1, Lumen Arc 24", "Wi-Fi". */
export const deviceLabel = (d: Device) =>
  d.kind === 'disk'
    ? `Disk ${d.index}, ${d.name}${d.volumes?.length ? ` (${d.volumes.map((v) => v.replace(/\\$/, '')).join(' ')})` : ''}`
    : d.kind === 'gpu'
      ? `GPU ${d.index ?? d.id.split(':')[1]}, ${d.name}`
      : d.alias || d.name

const KIND_TITLE = { disk: 'Disks', gpu: 'Graphics', network: 'Network adapters' } as const
const KIND_GLYPH = { disk: '', gpu: '', network: '' } as const
const lifetime = (units?: string) => (units == null ? undefined : `${(Number(BigInt(units) * 512000n) / 1e12).toFixed(1)} TB`)

type Verdict = { tone: 'good' | 'caution' | 'critical' | 'neutral'; text: string }

/** One plain-language verdict first, the numbers after (what SMART data is for). */
function verdict(d: Device): Verdict | undefined {
  const h = d.health
  if (d.kind === 'disk') {
    const problems: string[] = []
    if (h.criticalWarning) problems.push('the drive reports a warning')
    if (h.sparePercent != null && h.sparePercent < 10) problems.push('spare blocks are almost used up')
    if (h.wearPercent != null && h.wearPercent >= 90) problems.push('it is nearly worn out')
    if (h.mediaErrors != null && Number(h.mediaErrors) > 0) problems.push(`${h.mediaErrors} media errors`)
    if (problems.length) return { tone: 'critical', text: `Check this drive: ${problems.join(', ')}` }
    if (h.wearPercent != null && h.wearPercent >= 70) return { tone: 'caution', text: `Wearing: ${h.wearPercent} % of its rated life used` }
    if (h.temperature != null && h.temperature >= 70) return { tone: 'caution', text: `Hot: ${h.temperature.toFixed(0)} °C` }
    if (h.wearPercent != null || h.criticalWarning != null || h.sparePercent != null) return { tone: 'good', text: 'Healthy' }
    return undefined
  }
  if (d.kind === 'gpu') {
    if (h.temperature != null && h.temperature >= 90) return { tone: 'caution', text: `Hot: ${h.temperature.toFixed(0)} °C` }
    return undefined
  }
  if (d.connected == null) return undefined
  return d.connected ? { tone: 'good', text: 'Connected' } : { tone: 'neutral', text: 'Not connected' }
}

export function Devices() {
  const [hidden, setHidden] = useState(false)
  const data = usePoll(() => api.devices('*', hidden), 1000)
  // Virtual adapters (VPN, Hyper-V, WSL switches) are hidden unless asked for: they rarely carry real traffic.
  const devices = (data.data?.devices ?? []).filter((d) => hidden || d.kind !== 'network' || d.type !== 'Virtual')
  const sampling = data.data?.sampling ?? false
  const count = (k: Device['kind']) => devices.filter((d) => d.kind === k).length
  const warnings = devices.filter((d) => verdict(d)?.tone === 'critical')

  return (
    <div className="page dense devices">
      <PageHeader
        title="Devices"
        actions={
          <label className="pg-toggle">
            <span className="secondary">Virtual adapters</span>
            <Toggle
              value={hidden}
              onChange={(v) => {
                setHidden(v)
                data.reload()
              }}
            />
          </label>
        }
      >
        {devices.length === 0 ? (
          <>Reading your disks, graphics and network adapters…</>
        ) : (
          <>
            {plural(count('disk'), 'disk')}, {plural(count('gpu'), 'graphics adapter')} and {plural(count('network'), 'network adapter')}.{' '}
            {warnings.length ? (
              <strong className="tone-critical">{plural(warnings.length, 'drive needs', 'drives need')} attention.</strong>
            ) : (
              <>No health warnings.</>
            )}
          </>
        )}
      </PageHeader>
      {data.error && <div className="banner danger">{data.error}</div>}
      {(['disk', 'gpu', 'network'] as const).map((kind) => {
        const list = devices.filter((d) => d.kind === kind)
        if (data.data && list.length === 0) return null
        return (
          <section className="dev-section" key={kind}>
            <h3>{KIND_TITLE[kind]}</h3>
            <div className="dev-list">
              {list.map((d) => (
                <DeviceCard key={d.id} d={d} sampling={sampling} />
              ))}
            </div>
          </section>
        )
      })}
      <p className="dev-note secondary tiny">
        Health and temperature come from the drive and the graphics driver; some need the installed Corewright service. Values a device does not report are listed, never guessed.
      </p>
    </div>
  )
}

interface Stat {
  label: string
  value?: string
  tone?: Verdict['tone']
}

function DeviceCard({ d, sampling }: { d: Device; sampling: boolean }) {
  const v = d.values
  const h = d.health
  const series = deviceSeries(d)
  const gpu = d.kind === 'gpu'
  const disk = d.kind === 'disk'
  const format = gpu ? (n: number) => `${n.toFixed(0)} %` : disk ? rate : bits
  const live = gpu ? v.load : v.read == null || v.write == null ? undefined : v.read + v.write
  const peak = Math.max(0, ...series.values.filter((n): n is number => n != null))
  const top = gpu ? 100 : disk ? niceCeil(Math.max((peak * 1.1) / 1048576, 1)) * 1048576 : (niceCeil(Math.max((peak * 1.1 * 8) / 1e6, 1)) * 1e6) / 8
  const verdictNow = verdict(d)

  const facts: string[] = disk
    ? [d.bus, d.size != null ? bytes(d.size, 0) : undefined, d.volumes?.length ? d.volumes.map((x) => x.replace(/\\$/, '')).join(' ') : undefined].filter((x): x is string => !!x)
    : gpu
      ? [d.dedicatedMemory != null ? `${bytes(d.dedicatedMemory, 0)} video memory` : undefined].filter((x): x is string => !!x)
      : [d.type, d.receiveLinkSpeed != null ? `${bits(d.receiveLinkSpeed / 8)} link` : undefined, d.alias && d.alias !== d.name ? d.name : undefined].filter((x): x is string => !!x)

  const stats: Stat[] = disk
    ? [
        { label: 'Read', value: v.read != null ? rate(v.read) : undefined },
        { label: 'Write', value: v.write != null ? rate(v.write) : undefined },
        { label: 'Active time', value: v.active != null ? `${v.active.toFixed(0)} %` : undefined },
        { label: 'Temperature', value: h.temperature != null ? `${h.temperature.toFixed(0)} °C` : undefined, tone: h.temperature != null && h.temperature >= 70 ? 'caution' : undefined },
        { label: 'Wear', value: h.wearPercent != null ? `${h.wearPercent} % used` : undefined, tone: h.wearPercent != null && h.wearPercent >= 70 ? 'caution' : undefined },
        { label: 'Spare', value: h.sparePercent != null ? `${h.sparePercent.toFixed(0)} %` : undefined, tone: h.sparePercent != null && h.sparePercent < 10 ? 'critical' : undefined },
        { label: 'Media errors', value: h.mediaErrors, tone: h.mediaErrors != null && Number(h.mediaErrors) > 0 ? 'critical' : undefined },
        { label: 'Powered on', value: h.powerOnHours != null ? `${Number(h.powerOnHours).toLocaleString()} h` : undefined },
        { label: 'Unsafe shutdowns', value: h.unsafeShutdowns },
        { label: 'Read in total', value: lifetime(h.dataUnitsRead) },
        { label: 'Written in total', value: lifetime(h.dataUnitsWritten) },
      ]
    : gpu
      ? [
          { label: 'Video memory', value: v.dedicatedUsed != null ? (d.dedicatedMemory ? `${bytes(v.dedicatedUsed)} of ${bytes(d.dedicatedMemory, 0)}` : bytes(v.dedicatedUsed)) : undefined },
          { label: 'Shared memory', value: v.sharedUsed != null ? bytes(v.sharedUsed) : undefined },
          { label: 'Temperature', value: h.temperature != null ? `${h.temperature.toFixed(0)} °C` : undefined, tone: h.temperature != null && h.temperature >= 90 ? 'caution' : undefined },
          { label: 'Fan', value: h.fanRpm != null ? `${h.fanRpm} RPM` : undefined },
          { label: 'Power', value: h.powerPercent != null ? `${h.powerPercent.toFixed(0)} % of limit` : undefined },
          { label: 'Clock', value: h.engineClockMhz != null ? `${h.engineClockMhz.toFixed(0)} MHz` : undefined },
        ]
      : [
          { label: 'Receive', value: v.read != null ? bits(v.read) : undefined },
          { label: 'Send', value: v.write != null ? bits(v.write) : undefined },
          {
            label: 'Link',
            value:
              d.receiveLinkSpeed == null
                ? undefined
                : (d.sendLinkSpeed ?? d.receiveLinkSpeed) === d.receiveLinkSpeed
                  ? `${bits(d.receiveLinkSpeed / 8)} both ways`
                  : `${bits(d.receiveLinkSpeed / 8)} / ${bits(d.sendLinkSpeed! / 8)}`,
          },
        ]
  const shown = stats.filter((s) => s.value != null)
  const missing = stats.filter((s) => s.value == null).map((s) => s.label.toLowerCase())
  const memoryShare = gpu && v.dedicatedUsed != null && d.dedicatedMemory ? v.dedicatedUsed / d.dedicatedMemory : undefined

  return (
    <article className={`card dev-card kind-${d.kind}`}>
      <header className="dev-head">
        <span className="dev-icon glyph">{KIND_GLYPH[d.kind]}</span>
        <div className="dev-title">
          <h4>
            {d.kind === 'network' ? d.alias || d.name : d.name}
            {d.kind !== 'network' && <span className="secondary dev-index">{d.kind === 'disk' ? `Disk ${d.index}` : `GPU ${d.index ?? ''}`}</span>}
          </h4>
          <div className="dev-facts">
            {facts.map((f) => (
              <span key={f} className="chip-static">
                {f}
              </span>
            ))}
          </div>
        </div>
        {verdictNow && <span className={`dev-verdict tone-${verdictNow.tone}`}>{verdictNow.text}</span>}
      </header>
      <div className="dev-body">
        <div className="dev-live">
          <div className="dev-value">
            <strong>{sampling ? display(live, format) : '–'}</strong>
            <span className="secondary">{gpu ? 'load' : disk ? 'read and write' : 'receive and send'}</span>
          </div>
          <Spark {...series} colorVar={disk ? '--chart-3' : gpu ? '--chart-4' : '--chart-5'} top={top} topLabel={format(top)} midLabel={format(top / 2)} format={format} height={56} />
          {memoryShare != null && (
            <div className="dev-meter" title="Video memory in use">
              <i style={{ width: `${Math.min(100, memoryShare * 100).toFixed(1)}%` }} />
            </div>
          )}
        </div>
        <dl className="dev-stats">
          {shown.map((s) => (
            <div key={s.label} className={s.tone ? `tone-${s.tone}` : undefined}>
              <dt>{s.label}</dt>
              <dd>{s.value}</dd>
            </div>
          ))}
        </dl>
      </div>
      {missing.length > 0 && <p className="dev-missing secondary tiny">Not reported: {missing.join(', ')}.</p>}
    </article>
  )
}

