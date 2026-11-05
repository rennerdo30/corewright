import { useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, api } from '../api'
import { Chart } from '../components/Chart'
import { PageHeader } from '../components/PageHeader'
import { Card, NumberBox, Toggle } from '../components/SettingsControls'
import { bytes } from '../format'
import { usePoll } from '../hooks'
import type { AppSettings, UsageInfo, UsageProgram } from '../types'

type Range = 'today' | '24h' | '7d' | '30d' | 'all'
const RANGES: { id: Range; label: string }[] = [
  { id: 'today', label: 'Today' },
  { id: '24h', label: '24 hours' },
  { id: '7d', label: '7 days' },
  { id: '30d', label: '30 days' },
  { id: 'all', label: 'Everything' },
]

function rangeFrom(r: Range, now: number): number {
  if (r === 'today') {
    const d = new Date(now)
    d.setHours(0, 0, 0, 0)
    return d.getTime()
  }
  if (r === '24h') return now - 86_400_000
  if (r === '7d') return now - 7 * 86_400_000
  if (r === '30d') return now - 30 * 86_400_000
  return 0
}

/** "3 h 12 min", "42 min", "18 s". */
export function duration(seconds: number): string {
  if (seconds >= 86_400 * 2) return `${(seconds / 86_400).toFixed(1)} days`
  if (seconds >= 3600) {
    const minutes = Math.round(seconds / 60)
    const h = Math.floor(minutes / 60)
    const m = minutes % 60
    return m > 0 && h < 10 ? `${h} h ${m} min` : `${h} h`
  }
  if (seconds >= 60) return `${Math.round(seconds / 60)} min`
  return `${Math.round(seconds)} s`
}

const pct = (v: number) => (v >= 10 ? `${v.toFixed(0)} %` : v >= 0.1 ? `${v.toFixed(1)} %` : v > 0 ? '< 0.1 %' : '0 %')

/** Exe path or host target without the identity suffixes ("|?", "|<target>"), for the tooltip. */
const where = (program: string) => program.split('|')[0]

/**
 * Usage history (PM-45, owner 2026-11-05): which programs used the PC over hours and days. "Of all CPU" answers "how
 * much of the time the processors had did it take"; "of busy CPU" answers "how much of what the PC did was this".
 */
export function History() {
  const [range, setRange] = useState<Range>('24h')
  const [selected, setSelected] = useState<string>()
  const [settings, setSettings] = useState<AppSettings>()
  const [error, setError] = useState<string>()
  const [confirmClear, setConfirmClear] = useState(false)
  const timer = useRef<number>(undefined)

  const load = () => {
    const now = Date.now()
    return api.usage(rangeFrom(range, now), now, selected)
  }
  const usage = usePoll(load, 60_000)
  // A new range or program reloads at once instead of waiting for the next poll.
  const { reload } = usage
  const first = useRef(true)
  useEffect(() => {
    if (first.current) first.current = false
    else reload()
  }, [range, selected, reload])

  useEffect(() => {
    api.settings().then(setSettings)
  }, [])

  const update = (fn: (s: AppSettings) => void) => {
    if (!settings) return
    const next = structuredClone(settings)
    next.usage ??= { enabled: true, maxStorageMb: 64 }
    fn(next)
    setSettings(next)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(async () => {
      try {
        // Only the usage section is this page's: it goes onto the settings as they are now, so an Autopilot change made
        // meanwhile (another window, an API client) is not overwritten with the copy loaded when this page opened.
        const fresh = await api.settings()
        const saved = await api.saveSettings({ ...fresh, usage: next.usage })
        if (saved.settings) setSettings(saved.settings)
        setError(undefined)
        reload()
      } catch (e) {
        if (e instanceof ApiError && e.status === 400) setError((e.body as { errors?: { message: string }[] }).errors?.[0]?.message)
      }
    }, 400)
  }

  const clear = async () => {
    setConfirmClear(false)
    try {
      await api.clearUsage()
      setError(undefined)
    } catch (e) {
      setError(e instanceof ApiError ? ((e.body as { detail?: string })?.detail ?? e.message) : String(e))
    }
    setSelected(undefined)
    reload()
  }

  const u = usage.data
  const rec = settings?.usage ?? { enabled: true, maxStorageMb: 64 }
  const pick = u?.programs.find((p) => p.program === selected)

  const chart = useMemo(() => {
    const hours = u?.hours ?? []
    return { time: hours.map((h) => h.time), busy: hours.map((h) => h.cpuPercent), mine: hours.map((h) => h.programPercent) }
  }, [u])

  return (
    <div className="page dense history">
      <PageHeader
        title="History"
        actions={
          <div className="seg">
            {RANGES.map((r) => (
              <button key={r.id} className={range === r.id ? 'on' : ''} onClick={() => setRange(r.id)}>
                {r.label}
              </button>
            ))}
          </div>
        }
      >
        {usage.error ? usage.error : u ? <Summary u={u} /> : 'Loading…'}
      </PageHeader>

      <section className="card hist-chart">
        <div className="hist-chart-head">
          <span className="hist-key busy">
            <i />
            CPU busy, whole PC
          </span>
          {pick && (
            <span className="hist-key mine">
              <i />
              {pick.name}
            </span>
          )}
          <span className="spacer" />
          <span className="secondary tiny">One bar per hour</span>
        </div>
        {chart.time.length > 0 ? (
          <Chart time={chart.time} values={chart.busy} values2={selected ? chart.mine : undefined} colorVar="--chart-1" colorVar2={selected ? '--chart-3' : undefined} bars max={100} height={150} format={(v) => `${v.toFixed(0)} %`} />
        ) : (
          <div className="ap-empty">No hours recorded in this range yet. Corewright adds a bar for every hour it runs.</div>
        )}
      </section>

      <section className="card ap-tables">
        <div className="ap-scroll hist-scroll">{u && <Programs u={u} selected={selected} onSelect={(p) => setSelected(p === selected ? undefined : p)} />}</div>
      </section>

      <section className="section">
        <h3>Recording</h3>
        <div className="card-group">
          <Card glyph={''} title="Record usage history" desc="Adds up CPU time, disk activity and memory of every program, hour by hour. Uses the measurements Corewright already takes; it costs no extra CPU.">
            <Toggle value={rec.enabled} disabled={!settings} onChange={(v) => update((s) => (s.usage!.enabled = v))} />
          </Card>
          <Card
            glyph={''}
            title="Storage limit"
            desc={
              <>
                {u ? `${bytes(u.storage.bytes)} used${u.storage.oldestTime ? `, since ${new Date(u.storage.oldestTime).toLocaleDateString()}` : ''}. ` : ''}
                When the limit is reached, the oldest hours are deleted first.{error && <span className="danger-text"> {error}</span>}
                {u?.storage.problem && <span className="danger-text"> Writing the history failed: {u.storage.problem} Finished hours are kept in memory and written when it works again.</span>}
              </>
            }
          >
            <NumberBox value={rec.maxStorageMb} min={1} max={1024} step={16} unit="MB" onChange={(v) => update((s) => (s.usage!.maxStorageMb = Math.round(v)))} />
          </Card>
          <Card glyph={''} title="Clear history" desc="Deletes every recorded hour. Recording continues from now on.">
            {confirmClear ? (
              <span className="hist-confirm">
                <button className="btn danger" onClick={clear}>
                  Delete history
                </button>
                <button className="btn" onClick={() => setConfirmClear(false)}>
                  Cancel
                </button>
              </span>
            ) : (
              <button className="btn" disabled={!u || u.storage.bytes === 0} onClick={() => setConfirmClear(true)}>
                Clear…
              </button>
            )}
          </Card>
        </div>
      </section>
    </div>
  )
}

/** One sentence: how long was measured, how busy the PC was, and who used most of it. */
function Summary({ u }: { u: UsageInfo }) {
  if (!u.enabled && u.measuredSeconds === 0) return <>Recording is off. Turn it on below to see which programs use your PC over hours and days.</>
  if (u.measuredSeconds === 0) return <>Nothing recorded in this range yet.</>
  const busy = u.availableCpuSeconds > 0 ? (100 * u.busyCpuSeconds) / u.availableCpuSeconds : 0
  const top = u.programs[0]
  return (
    <>
      Measured {duration(u.measuredSeconds)}. Your processors were busy <b>{pct(busy)}</b> of that time.
      {top && top.cpuSeconds > 0 && (
        <>
          {' '}
          The hungriest program, <b>{top.name}</b>, used {pct(top.cpuPercent)} of all CPU time: {top.busyPercent >= 1 ? <b>{pct(top.busyPercent)} of everything your PC did</b> : 'a small part of what your PC did'}.
        </>
      )}
      {!u.enabled && ' Recording is off.'}
    </>
  )
}

function Programs({ u, selected, onSelect }: { u: UsageInfo; selected?: string; onSelect: (program: string) => void }) {
  const rows = u.programs
  const topBusy = Math.max(1, ...rows.map((r) => r.busyPercent))
  if (rows.length === 0) return <div className="ap-empty">No program has used noticeable CPU, disk or memory in this range yet.</div>
  return (
    <table className="ap-table hist-table">
      <thead>
        <tr>
          <th>Program</th>
          <th className="num" title="CPU time of all its processes on all cores">
            CPU time
          </th>
          <th className="num" title="Its CPU time divided by all the CPU time the processors had in the measured time">
            Of all CPU
          </th>
          <th title="Its share of the CPU time the processors were actually busy">Of busy CPU</th>
          <th className="num" title="Average number of cores it kept busy">
            Avg cores
          </th>
          <th className="num">Running</th>
          <th className="num">Disk and I/O</th>
          <th className="num" title="Average private memory while running">
            Avg memory
          </th>
          <th className="num">Peak memory</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <Row key={r.program} r={r} top={topBusy} measured={u.measuredSeconds} on={r.program === selected} onSelect={onSelect} />
        ))}
        {u.unlistedCpuSeconds > 0 && (
          <tr className="hist-other" title="Programs too small to list one by one, processes too short to be sampled, and time Windows spends on interrupts">
            <td className="secondary">Everything else</td>
            <td className="num secondary">{duration(u.unlistedCpuSeconds)}</td>
            <td className="num secondary">{pct(u.availableCpuSeconds > 0 ? (100 * u.unlistedCpuSeconds) / u.availableCpuSeconds : 0)}</td>
            <td />
            <td colSpan={5} />
          </tr>
        )}
      </tbody>
    </table>
  )
}

function Row({ r, top, measured, on, onSelect }: { r: UsageProgram; top: number; measured: number; on: boolean; onSelect: (program: string) => void }) {
  return (
    <tr className={on ? 'selected' : ''} onClick={() => onSelect(r.program)} title={`${where(r.program)}\nClick to show it in the chart`}>
      <td className="hist-name">{r.name}</td>
      <td className="num">{duration(r.cpuSeconds)}</td>
      <td className="num">{pct(r.cpuPercent)}</td>
      <td>
        <div className="hist-share">
          <span className="hist-bar">
            <i style={{ width: `${Math.min(100, (100 * r.busyPercent) / top)}%` }} />
          </span>
          <span className="num">{pct(r.busyPercent)}</span>
        </div>
      </td>
      <td className="num">{r.averageCores >= 0.01 ? r.averageCores.toFixed(2) : '< 0.01'}</td>
      <td className="num secondary">{measured > 0 && r.runningSeconds >= measured * 0.98 ? 'always' : duration(r.runningSeconds)}</td>
      <td className="num">{bytes(r.ioBytes)}</td>
      <td className="num">{bytes(r.averageMemory)}</td>
      <td className="num secondary">{bytes(r.peakMemory)}</td>
    </tr>
  )
}
