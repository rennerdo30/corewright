import { useEffect, useState } from 'react'
import { api } from '../api'
import type { Topology } from '../types'
import { parseCores } from './DieMap'

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

function threadsOf(topo: Topology) {
  return topo.ccds.flatMap((c) => c.cores.flatMap((k) => k.threads))
}

function presets(topo: Topology | undefined): Preset[] {
  const out: Preset[] = [{ value: 'all', label: 'All cores', lps: topo && threadsOf(topo) }]
  if (!topo) return out
  const cores = topo.ccds.flatMap((c) => c.cores.map((k) => ({ ccd: c.id, k })))
  if (topo.hybrid) {
    const p = cores.filter((c) => c.k.efficiencyClass > 0).flatMap((c) => c.k.threads)
    const e = cores.filter((c) => c.k.efficiencyClass === 0).flatMap((c) => c.k.threads)
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
  if (topo.ccds.length > 1) {
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
export function CorePicker({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const topo = useTopology()
  const list = presets(topo)
  const key = value.trim().toLowerCase()
  const isList = /^[\d\s,;-]+$/.test(key)
  // A token this CPU's preset list does not offer (or the list is still loading) stays selectable under its own name.
  const preset: Preset | undefined = isList ? undefined : (list.find((p) => p.value === key) ?? (topo && !DYNAMIC[key] ? undefined : { value: key, label: key }))
  const isCustom = !preset

  // What the grid shows as selected: the custom list, or what the preset resolves to on this CPU.
  const selected = new Set(isCustom ? parseCores(value) : (preset.lps ?? []))
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
        <select
          value={isCustom ? 'custom' : preset.value}
          onChange={(e) => {
            const v = e.target.value
            if (v === 'custom') {
              const current = isCustom ? parseCores(value) : (preset.lps ?? [])
              onChange(formatCores(current.length ? current : all.slice(0, Math.max(1, all.length / 2))) || '0')
            } else onChange(v)
          }}
        >
          {list.map((p) => (
            <option key={p.value} value={p.value}>
              {p.label}
            </option>
          ))}
          {preset && !list.includes(preset) && <option value={preset.value}>{preset.label}</option>}
          <option value="custom">Specific threads…</option>
        </select>
        {isCustom && (
          <input
            className="cores-input"
            value={value}
            placeholder="0-7,16"
            aria-label="Logical processors"
            spellCheck={false}
            onChange={(e) => onChange(e.target.value)}
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
                    const eCore = topo.hybrid && k.efficiencyClass === 0
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
