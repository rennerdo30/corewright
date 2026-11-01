import { useEffect, useState } from 'react'
import { api } from '../api'
import { Spark, niceCeil } from '../components/Chart'
import { PageHeader, plural } from '../components/PageHeader'
import { Toggle } from '../components/SettingsControls'
import { bytes } from '../format'
import { usePoll } from '../hooks'
import type { Device, MemoryModule, SystemInfo, Topology } from '../types'
import type { Page } from '../App'

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

type Section = 'cpu' | 'memory' | 'disk' | 'gpu' | 'network'
let pendingSection: Section | undefined

/** Opens Devices at one section (Overview card titles). */
export function openDeviceSection(section: Section, open: (p: Page) => void) {
  pendingSection = section
  open('devices')
}
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
  const sys = usePoll(api.system, 1000)
  const topo = usePoll(api.topology, 600_000)
  const modules = usePoll(api.memory, 600_000)

  // Arrived from an Overview card: scroll to its section once it exists.
  useEffect(() => {
    if (!pendingSection) return
    const id = `dev-${pendingSection}`
    let tries = 0
    const timer = window.setInterval(() => {
      const el = document.getElementById(id)
      if (el || ++tries > 20) {
        window.clearInterval(timer)
        el?.scrollIntoView({ behavior: 'smooth', block: 'start' })
        pendingSection = undefined
      }
    }, 100)
    return () => window.clearInterval(timer)
  }, [])
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
      {topo.data && sys.data && (
        <section className="dev-section" id="dev-cpu">
          <h3>Processor</h3>
          <CpuCard topo={topo.data} sys={sys.data} />
        </section>
      )}
      {sys.data && (
        <section className="dev-section" id="dev-memory">
          <h3>Memory</h3>
          <MemoryCard sys={sys.data} modules={modules.data ?? []} />
        </section>
      )}
      {(['disk', 'gpu', 'network'] as const).map((kind) => {
        const list = devices.filter((d) => d.kind === kind)
        if (data.data && list.length === 0) return null
        return (
          <section className="dev-section" key={kind} id={`dev-${kind}`}>
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


/** Model, layout and live load; the fastest cores by the CPPC ranking Windows uses (like Ryzen Master's star). */
function CpuCard({ topo, sys }: { topo: Topology; sys: SystemInfo }) {
  const cores = topo.ccds.flatMap((c) => c.cores.map((k) => ({ ...k, ccd: c.id })))
  const ranked = topo.rankingAvailable ? [...cores].filter((k) => k.rank > 0).sort((a, b) => a.rank - b.rank) : []
  const peak = Math.max(0, ...sys.historyCpu)
  const facts = [
    `${topo.physical} cores, ${topo.logical} threads`,
    ...(topo.ccds.length > 1 ? [`${topo.ccds.length} CCDs`] : []),
    ...(topo.hybrid ? ['performance and efficiency cores'] : []),
  ]
  return (
    <article className="card dev-card kind-cpu">
      <header className="dev-head">
        <span className="dev-icon glyph">{'\uE950'}</span>
        <div className="dev-title">
          <h4>{topo.name}</h4>
          <div className="dev-facts">
            {facts.map((f) => (
              <span key={f} className="chip-static">
                {f}
              </span>
            ))}
          </div>
        </div>
      </header>
      <div className="dev-body">
        <div className="dev-live">
          <div className="dev-value">
            <strong>{sys.cpu.toFixed(0)} %</strong>
            <span className="secondary">load, peak {peak.toFixed(0)} % in 5 min</span>
          </div>
          <Spark time={sys.historyTime} values={sys.historyCpu} colorVar="--chart-1" top={100} topLabel="100 %" midLabel="50 %" format={(n) => `${n.toFixed(0)} %`} height={56} />
        </div>
        <dl className="dev-stats">
          {topo.ccds.map((c) => {
            const lps = c.cores.flatMap((k) => k.threads)
            const load = lps.reduce((s, i) => s + (sys.threadUsage[i] ?? 0), 0) / Math.max(1, lps.length)
            return (
              <div key={c.id}>
                <dt>
                  CCD {c.id}, threads {Math.min(...lps)}-{Math.max(...lps)}
                </dt>
                <dd>
                  {Math.round(c.l3Bytes / 1048576)} MB L3{c.vCache ? ', 3D V-Cache' : ''}, {load.toFixed(0)} % busy
                </dd>
              </div>
            )
          })}
          {ranked.length > 0 && (
            <div>
              <dt>Fastest cores (CPPC)</dt>
              <dd>{ranked.slice(0, 4).map((k) => `Core ${k.id}`).join(', ')}</dd>
            </div>
          )}
        </dl>
      </div>
    </article>
  )
}

/** Maker and rated speed from common part numbers, when the firmware does not name the maker. */
function moduleMaker(m: MemoryModule): { maker: string; rated?: number } {
  const part = m.part.toUpperCase()
  const known = m.manufacturer && !/^unknown$/i.test(m.manufacturer) ? m.manufacturer : ''
  let maker = known
  let rated: number | undefined
  let r: RegExpMatchArray | null
  if ((r = part.match(/^F[345]-(\d{4})/))) { maker ||= 'G.Skill'; rated = Number(r[1]) }
  else if ((r = part.match(/^CM[A-Z]\d+[A-Z]+\d?[A-Z]+\d?[A-Z](\d{4})/))) { maker ||= 'Corsair'; rated = Number(r[1]) }
  else if ((r = part.match(/^KF(\d)(\d{2})/))) { maker ||= 'Kingston FURY'; rated = Number(r[2]) * 100 }
  return { maker: maker || 'Unknown maker', rated }
}

function MemoryCard({ sys, modules }: { sys: SystemInfo; modules: MemoryModule[] }) {
  const usedShare = sys.memoryTotal ? sys.memoryUsed / sys.memoryTotal : 0
  const installed = modules.reduce((s, m) => s + m.sizeMb, 0)
  const first = modules[0]
  const info = first ? moduleMaker(first) : undefined
  const running = first?.configuredMts || first?.ratedMts
  const slow = info?.rated && running && running < info.rated
  const facts = [
    installed ? `${(installed / 1024).toFixed(0)} GB installed` : '',
    first ? `${modules.length} × ${(first.sizeMb / 1024).toFixed(0)} GB` : '',
    first?.type ?? '',
    running ? `${running} MT/s` : '',
  ].filter(Boolean)
  return (
    <article className="card dev-card kind-memory">
      <header className="dev-head">
        <span className="dev-icon glyph">{'\uE964'}</span>
        <div className="dev-title">
          <h4>{info && first ? `${info.maker} ${first.part}` : 'Memory'}</h4>
          <div className="dev-facts">
            {facts.map((f) => (
              <span key={f} className="chip-static">
                {f}
              </span>
            ))}
          </div>
        </div>
        {slow && (
          <span className="dev-verdict tone-caution" title="Turn on EXPO or XMP in the BIOS to run the kit at its rated speed.">
            Running below rated speed
          </span>
        )}
      </header>
      <div className="dev-body">
        <div className="dev-live">
          <div className="dev-value">
            <strong>{bytes(sys.memoryUsed)}</strong>
            <span className="secondary">
              in use of {bytes(sys.memoryTotal, 0)}, {Math.round(usedShare * 100)} %
            </span>
          </div>
          <Spark time={sys.historyTime} values={sys.historyMemory} colorVar="--chart-2" top={100} topLabel={bytes(sys.memoryTotal, 0)} midLabel={bytes(sys.memoryTotal / 2, 0)} format={(v) => bytes((v / 100) * sys.memoryTotal)} height={56} />
        </div>
        <dl className="dev-stats">
          <div>
            <dt>Free</dt>
            <dd>{bytes(sys.memoryTotal - sys.memoryUsed)}</dd>
          </div>
          {modules.map((m) => (
            <div key={m.slot + m.part}>
              <dt>{m.slot || 'Module'}</dt>
              <dd>
                {(m.sizeMb / 1024).toFixed(0)} GB {m.type}, {m.configuredMts || m.ratedMts} MT/s
              </dd>
            </div>
          ))}
          {slow && info?.rated && (
            <div className="tone-caution">
              <dt>Rated speed</dt>
              <dd>{info.rated} MT/s (EXPO / XMP off)</dd>
            </div>
          )}
        </dl>
      </div>
    </article>
  )
}
