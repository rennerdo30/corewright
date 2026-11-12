import { useEffect, useState } from 'react'
import { api } from '../api'
import { usePoll } from '../hooks'
import type { Topology } from '../types'

// The topology is read once when the service starts, so one request per dashboard session is enough.
let topoRequest: Promise<Topology> | undefined
function useTopology(): Topology | undefined {
  const [topo, setTopo] = useState<Topology>()
  useEffect(() => {
    let live = true
    topoRequest ??= api.topology().catch((e) => {
      topoRequest = undefined
      throw e
    })
    topoRequest.then((t) => live && setTopo(t)).catch(() => {})
    return () => {
      live = false
    }
  }, [])
  return topo
}

interface Preset {
  value: string
  label: string
  /** Logical processors it resolves to now; undefined when it is decided at run time. */
  lps?: number[]
}

const DYNAMIC: Record<string, string> = {
  'auto-game': 'Decided while a game runs: the game’s CCD.',
  'non-game-ccd': 'Decided while a game runs: every CCD except the game’s. All cores otherwise.',
  'auto-background': 'Decided by Autopilot while the foreground is busy.',
}

/** PM-49: "isolate:N" → N, else undefined. */
export function isolateCount(value: string | null | undefined): number | undefined {
  const m = /^isolate:(\d+)$/i.exec((value ?? '').trim())
  return m ? Number(m[1]) : undefined
}

/** The service's limit: physical core 0 and one more core always stay for everything else. */
export function maxIsolated(topo: Topology): number {
  const width = Math.max(1, ...topo.ccds.flatMap((c) => c.cores.map((k) => k.threads.length)))
  return topo.logical >= 64 ? 0 : Math.max(0, topo.logical - 2 * width)
}

/** Highest logical processor number the service can address (processor group 0). */
const MAX_LP = 63

/**
 * Parses a thread list the way the service does (separators , ; and spaces, ranges a-b). Never expands past
 * processor 63, so a pasted "0-999999999" cannot hang the window. `ok` is false when the service would reject it.
 */
export function parseThreadList(text: string): { lps: number[]; ok: boolean } {
  const out = new Set<number>()
  let ok = true
  for (const part of text.split(/[,;\s]+/).filter(Boolean)) {
    const m = /^(\d{1,6})(?:-(\d{1,6}))?$/.exec(part)
    if (!m) { ok = false; continue }
    const a = Number(m[1])
    const b = m[2] === undefined ? a : Number(m[2])
    if (b < a || b > MAX_LP) ok = false
    for (let i = a; i <= Math.min(b, MAX_LP); i++) out.add(i)
  }
  return { lps: [...out].sort((x, y) => x - y), ok: ok && out.size > 0 }
}

/** The service's rule: only the highest efficiency class is "performance"; every lower class counts as efficiency. */
function topClass(topo: Topology) {
  return Math.max(...topo.ccds.flatMap((c) => c.cores.map((k) => k.efficiencyClass)))
}

function threadsOf(topo: Topology) {
  return topo.ccds.flatMap((c) => c.cores.flatMap((k) => k.threads))
}

function presets(topo: Topology | undefined): Preset[] {
  const out: Preset[] = [{ value: 'all', label: 'All cores', lps: topo && threadsOf(topo) }]
  if (!topo) return out
  const cores = topo.ccds.flatMap((c) => c.cores.map((k) => ({ ccd: c.id, k })))
  if (topo.hybrid) {
    const top = topClass(topo)
    const p = cores.filter((c) => c.k.efficiencyClass === top).flatMap((c) => c.k.threads)
    const e = cores.filter((c) => c.k.efficiencyClass < top).flatMap((c) => c.k.threads)
    out.push({ value: 'p-cores', label: 'Performance cores', lps: p }, { value: 'e-cores', label: 'Efficiency cores', lps: e })
  }
  const ranked = topo.rankingAvailable ? [...cores].sort((a, b) => a.k.rank - b.k.rank) : []
  const fastestCcd = ranked[0]?.ccd
  const vcache = topo.ccds.filter((c) => c.vCache)
  const singleVCache = vcache.length === 1 && topo.ccds.length > 1 ? vcache[0] : undefined
  if (topo.ccds.length > 1) {
    for (const c of topo.ccds) {
      const lps = c.cores.flatMap((k) => k.threads)
      const tags = [singleVCache?.id === c.id ? 'V-Cache' : '', fastestCcd === c.id ? 'fastest' : ''].filter(Boolean).join(', ')
      out.push({ value: `ccd${c.id}`, label: `CCD ${c.id}${tags ? ` (${tags})` : ''}`, lps })
    }
    if (singleVCache) out.push({ value: 'vcache-ccd', label: 'The V-Cache CCD', lps: singleVCache.cores.flatMap((k) => k.threads) })
    if (fastestCcd !== undefined)
      out.push({ value: 'frequency-ccd', label: 'The fastest CCD', lps: topo.ccds.find((c) => c.id === fastestCcd)!.cores.flatMap((k) => k.threads) })
  }
  if (topo.rankingAvailable)
    for (const n of [1, 2, 4, 8]) if (n < topo.physical) out.push({ value: `best-cores:${n}`, label: `Best ${n} core${n > 1 ? 's' : ''}`, lps: ranked.slice(0, n).flatMap((c) => c.k.threads) })
  // The service supports game placement only below 64 logical processors.
  if (topo.ccds.length > 1 && topo.logical < 64) {
    out.push({ value: 'auto-game', label: 'While gaming: the game’s CCD' })
    out.push({ value: 'non-game-ccd', label: 'While gaming: all but the game’s CCD' })
  }
  return out
}

/** [16, 17, 30, 31] → "16-17,30-31" (the service's own format). */
export function formatCores(lps: Iterable<number>): string {
  const s = [...new Set(lps)].sort((a, b) => a - b)
  const parts: string[] = []
  for (let i = 0; i < s.length; i++) {
    let j = i
    while (j + 1 < s.length && s[j + 1] === s[j] + 1) j++
    parts.push(j > i ? `${s[i]}-${s[j]}` : `${s[i]}`)
    i = j
  }
  return parts.join(',')
}

/** "CPU cores" action: presets from the real CPU plus a map of CCDs, cores and threads to pick from. */
export function CorePicker({ value, onChange, ruleId }: { value: string; onChange: (v: string) => void; ruleId?: string }) {
  const topo = useTopology()
  if (isolateCount(value) !== undefined) return <IsolatePicker value={value} onChange={onChange} ruleId={ruleId} topo={topo} />
  return <ListPicker value={value} onChange={onChange} topo={topo} />
}

/**
 * PM-49 "isolate:N": the user says how many threads; the service picks them (strongest whole cores, one CCD, P-cores,
 * never core 0, off the game's CCD unless it is the game) and moves every other process off them. The map shows the
 * live pick once the program runs.
 */
function IsolatePicker({ value, onChange, ruleId, topo }: { value: string; onChange: (v: string) => void; ruleId?: string; topo?: Topology }) {
  const n = isolateCount(value) ?? 1
  const max = topo ? maxIsolated(topo) : 62
  const status = usePoll(api.status, 3000)
  const live = status.data?.isolations?.find((i) => i.ruleId === ruleId)
  const threads = new Set(live ? parseThreadList(live.threads).lps : [])
  const reserved = new Set(live ? parseThreadList(live.reserved).lps : [])
  const [draft, setDraft] = useState(String(n))
  useEffect(() => setDraft(String(n)), [n])
  const commit = (text: string) => {
    const v = Math.round(Number(text))
    if (Number.isFinite(v) && v >= 1 && v <= Math.max(1, max)) onChange(`isolate:${v}`)
  }
  const smt = topo ? topo.ccds.some((c) => c.cores.some((k) => k.threads.length > 1)) : false
  const odd = smt && n % 2 === 1
  return (
    <div className="corepick">
      <div className="corepick-bar">
        <ModeSelect value="isolate" topo={topo} onChange={onChange} current={value} />
        <div className="iso-count">
          <button className="btn subtle" aria-label="Fewer threads" disabled={n <= 1} onClick={() => onChange(`isolate:${n - 1}`)}>
            {'\uE949'}
          </button>
          <input
            className="cores-input iso-input"
            inputMode="numeric"
            value={draft}
            aria-label="Isolated threads"
            aria-invalid={Number(draft) > max}
            onChange={(e) => {
              setDraft(e.target.value)
              commit(e.target.value)
            }}
            onBlur={() => setDraft(String(n))}
          />
          <button className="btn subtle" aria-label="More threads" disabled={n >= max} onClick={() => onChange(`isolate:${n + 1}`)}>
            {'\uE948'}
          </button>
          <span className="iso-unit">threads of its own</span>
        </div>
        <span className="corepick-sum">
          {live
            ? `Running on threads ${live.threads}${live.reserved !== live.threads ? ` (cores ${live.reserved} reserved)` : ''}. ${live.keptOff} other process${live.keptOff === 1 ? '' : 'es'} moved off${live.refused ? `, ${live.refused} refused` : ''}.`
            : 'Picked when the program starts.'}
          {Number(draft) > max && <span className="corepick-warn"> · at most {max} on this CPU</span>}
        </span>
      </div>
      <p className="iso-explain">
        Corewright picks the strongest free cores{topo && topo.ccds.length > 1 ? ' inside one CCD (the game’s CCD only for a game)' : ''}
        {topo?.hybrid ? ', performance cores only' : ''}, never core 0, and moves every other process it can control off them.
        {odd ? ' An odd count leaves the second thread of the last core idle, so nothing shares that core.' : ''} Windows itself,
        interrupts and protected processes can still run there.
      </p>
      {topo && (
        <div className="corepick-map preview iso-map">
          {topo.ccds.map((c) => (
            <section key={c.id} className="corepick-ccd">
              <div className="corepick-ccd-head static">
                <span className="chiplet-name">{topo.ccds.length > 1 ? `CCD ${c.id}` : topo.name || 'CPU'}</span>
                <span className="chiplet-cache">
                  {c.l3Bytes ? `${Math.round(c.l3Bytes / 1048576)} MB L3` : ''}
                  {c.vCache && topo.ccds.some((o) => !o.vCache) && <span className="vcache">3D V-Cache</span>}
                </span>
              </div>
              <div className="corepick-cores">
                {c.cores.map((k) => {
                  const core0 = k.threads.includes(0)
                  const on = k.threads.some((lp) => reserved.has(lp))
                  return (
                    <div key={k.id} className={`corepick-core ${on ? 'on' : ''} ${core0 ? 'win' : ''}`} title={core0 ? 'Core 0 stays with Windows' : `Core ${k.id}`}>
                      <div className="corepick-core-head static">
                        <span>Core {k.id}</span>
                        {core0 && <span className="corepick-e">Win</span>}
                        {k.ccdRank === 1 && <span className="star">★{k.tied ? '=' : ''}</span>}
                      </div>
                      <div className="corepick-threads">
                        {k.threads.map((lp) => (
                          <span key={lp} className={`corepick-thread ${threads.has(lp) ? 'on' : reserved.has(lp) ? 'idle' : ''}`} title={threads.has(lp) ? `Thread ${lp}: isolated` : reserved.has(lp) ? `Thread ${lp}: kept idle` : `Thread ${lp}`}>
                            {lp}
                          </span>
                        ))}
                      </div>
                    </div>
                  )
                })}
              </div>
            </section>
          ))}
        </div>
      )}
    </div>
  )
}

/** The mode list shared by both pickers: every preset, specific threads, or isolated threads. */
function ModeSelect({ value, topo, onChange, current, extra }: { value: string; topo?: Topology; onChange: (v: string) => void; current: string; extra?: React.ReactNode }) {
  const list = presets(topo)
  const max = topo ? maxIsolated(topo) : 2
  return (
    <select
      value={value}
      onChange={(e) => {
        const v = e.target.value
        if (v === 'isolate') onChange(`isolate:${Math.min(2, Math.max(1, max))}`)
        else if (v === 'custom') onChange(topo ? formatCores(threadsOf(topo).slice(0, Math.max(1, threadsOf(topo).length / 2))) || '0' : '0')
        else onChange(v)
      }}
      aria-label="CPU cores"
      data-current={current}
    >
      {list.map((p) => (
        <option key={p.value} value={p.value}>
          {p.label}
        </option>
      ))}
      {extra}
      <option value="custom">Specific threads…</option>
      {max >= 1 && <option value="isolate">Own threads, isolated…</option>}
    </select>
  )
}

function ListPicker({ value, onChange, topo }: { value: string; onChange: (v: string) => void; topo?: Topology }) {
  const list = presets(topo)
  const key = value.trim().toLowerCase()
  const isList = /^[\d\s,;-]+$/.test(key)
  // A token this CPU's preset list does not offer (or the list is still loading) stays selectable under its own name.
  const preset: Preset | undefined = isList ? undefined : (list.find((p) => p.value === key) ?? (topo && !DYNAMIC[key] ? undefined : { value: key, label: key }))
  const isCustom = !preset

  // The text field edits a draft, so clearing it to retype never sends "" (which would remove the setting).
  const [draft, setDraft] = useState(value)
  useEffect(() => setDraft(value), [value])
  const parsed = parseThreadList(isCustom ? draft : '')

  // What the grid shows as selected: the custom list, or what the preset resolves to on this CPU.
  const selected = new Set(isCustom ? parsed.lps : (preset.lps ?? []))
  const dynamic = !isCustom && preset.lps === undefined
  const all = topo ? threadsOf(topo) : []
  const unknown = topo ? [...selected].filter((lp) => !all.includes(lp)) : []

  // An empty list would remove the setting, so the last thread cannot be deselected.
  const setSet = (lps: Set<number>) => {
    if (lps.size > 0) onChange(formatCores(lps))
  }
  const toggle = (lps: number[]) => {
    const next = new Set(selected)
    const on = lps.every((lp) => next.has(lp))
    for (const lp of lps) on ? next.delete(lp) : next.add(lp)
    setSet(next)
  }

  const selCores = topo ? topo.ccds.flatMap((c) => c.cores).filter((k) => k.threads.some((lp) => selected.has(lp))).length : 0
  const selCcds = topo ? topo.ccds.filter((c) => c.cores.some((k) => k.threads.some((lp) => selected.has(lp)))) : []
  const smt = topo ? topo.ccds.some((c) => c.cores.some((k) => k.threads.length > 1)) : false

  return (
    <div className="corepick">
      <div className="corepick-bar">
        <ModeSelect
          value={isCustom ? 'custom' : preset.value}
          topo={topo}
          current={value}
          extra={preset && !list.includes(preset) ? <option value={preset.value}>{preset.label}</option> : undefined}
          onChange={(v) => {
            if (v !== 'isolate' && /^[\d\s,;-]+$/.test(v)) {
              // "Specific threads…": start from what the current choice resolves to.
              const current = isCustom ? parsed.lps : (preset.lps ?? [])
              onChange(formatCores(current.length ? current : all.slice(0, Math.max(1, all.length / 2))) || '0')
            } else onChange(v)
          }}
        />
        {isCustom && (
          <input
            className="cores-input"
            value={draft}
            placeholder="0-7,16"
            aria-label="Logical processors"
            aria-invalid={!parsed.ok}
            spellCheck={false}
            onChange={(e) => {
              setDraft(e.target.value)
              if (e.target.value.trim()) onChange(e.target.value)
            }}
            onBlur={() => setDraft(value)}
          />
        )}
        <span className="corepick-sum">
          {dynamic
            ? DYNAMIC[preset.value]
            : selected.size === 0
              ? 'No thread selected'
              : topo
                ? `${selected.size} thread${selected.size === 1 ? '' : 's'} on ${selCores} core${selCores === 1 ? '' : 's'}${topo.ccds.length > 1 && selCcds.length ? ` · ${selCcds.map((c) => `CCD ${c.id}`).join(', ')}` : ''}`
                : ''}
          {isCustom && !parsed.ok && draft.trim() !== '' && <span className="corepick-warn"> · not a valid list (e.g. 0-7,16)</span>}
          {unknown.length > 0 && <span className="corepick-warn"> · {formatCores(unknown)} not on this CPU</span>}
        </span>
      </div>

      {topo && (
        <div className={`corepick-map ${dynamic ? 'dynamic' : ''} ${isCustom ? 'editing' : 'preview'}`}>
          {topo.ccds.map((c) => {
            const lps = c.cores.flatMap((k) => k.threads)
            const ccdOn = lps.every((lp) => selected.has(lp))
            return (
              <section key={c.id} className="corepick-ccd">
                <button className={`corepick-ccd-head ${ccdOn ? 'on' : ''}`} onClick={() => toggle(lps)} title={`${ccdOn ? 'Clear' : 'Select'} all of CCD ${c.id}`}>
                  <span className="chiplet-name">{topo.ccds.length > 1 ? `CCD ${c.id}` : topo.name || 'CPU'}</span>
                  <span className="chiplet-cache">
                    {c.l3Bytes ? `${Math.round(c.l3Bytes / 1048576)} MB L3` : ''}
                    {c.vCache && topo.ccds.some((o) => !o.vCache) && <span className="vcache">3D V-Cache</span>}
                  </span>
                  <span className="corepick-range">
                    threads {Math.min(...lps)}–{Math.max(...lps)}
                  </span>
                </button>
                <div className="corepick-cores">
                  {c.cores.map((k) => {
                    const coreOn = k.threads.every((lp) => selected.has(lp))
                    const coreSome = k.threads.some((lp) => selected.has(lp))
                    const eCore = topo.hybrid && k.efficiencyClass < topClass(topo)
                    return (
                      <div key={k.id} className={`corepick-core ${coreOn ? 'on' : coreSome ? 'some' : ''} ${k.ccdRank === 1 ? 'best' : ''}`}>
                        <button
                          className="corepick-core-head"
                          onClick={() => toggle(k.threads)}
                          title={`Core ${k.id}${eCore ? ' (efficiency core)' : ''}${k.rank ? `, ${ordinal(k.rank)} fastest of ${topo.physical}` : ''}${k.performance ? ` (CPPC ${k.performance})` : ''}${k.tied ? ', tied' : ''}. Click for ${k.threads.length > 1 ? 'both threads' : 'this core'}.`}
                        >
                          <span>Core {k.id}</span>
                          {eCore && <span className="corepick-e">E</span>}
                          {k.ccdRank === 1 && <span className="star">★{k.tied ? '=' : ''}</span>}
                          {k.ccdRank === 2 && <span className="dot2">•</span>}
                        </button>
                        <div className="corepick-threads">
                          {k.threads.map((lp, i) => (
                            <button
                              key={lp}
                              className={`corepick-thread ${selected.has(lp) ? 'on' : ''}`}
                              aria-pressed={selected.has(lp)}
                              onClick={() => toggle([lp])}
                              title={`Logical processor ${lp}${k.threads.length > 1 ? (i === 0 ? ', first thread of core ' : ', SMT sibling on core ') + k.id : ''}`}
                            >
                              {lp}
                            </button>
                          ))}
                        </div>
                      </div>
                    )
                  })}
                </div>
              </section>
            )
          })}
        </div>
      )}

      {topo && (
        <div className="corepick-tools">
          <button className="btn subtle" onClick={() => setSet(new Set(all))}>
            Select all
          </button>
          {smt && (
            <button
              className="btn subtle"
              title="Keep only the first thread of each selected core, so no two threads share a core"
              onClick={() => {
                const base = selected.size ? selected : new Set(all)
                setSet(new Set(topo.ccds.flatMap((c) => c.cores).filter((k) => k.threads.some((lp) => base.has(lp))).map((k) => k.threads[0])))
              }}
            >
              One thread per core
            </button>
          )}
          {topo.rankingAvailable && <span className="corepick-legend">★ fastest core of its CCD · • second fastest</span>}
        </div>
      )}
    </div>
  )
}

function ordinal(n: number) {
  const s = n % 100 >= 11 && n % 100 <= 13 ? 'th' : ['th', 'st', 'nd', 'rd'][n % 10] ?? 'th'
  return `${n}${s}`
}
