import type { Topology } from '../types'

export type Role = 'game' | 'build' | 'background' | 'balance'

/** A process drawn on the cores it may run on. */
export interface Token {
  key: string
  label: string
  role: Role
  lps: number[]
  title: string
}

const ROLE_NAMES: Record<Role, string> = { game: 'Game', build: 'Build tool', background: 'Background', balance: 'Balanced' }

/**
 * The CPU drawn like its silicon: each CCD a chiplet with its L3, each physical core a tile, each hardware thread a
 * lane that fills with live load. With tokens it becomes the Autopilot's map: which process runs where, the game's
 * CCD outlined, the strongest cores hatched because Autopilot keeps them free.
 */
export function DieMap({
  topo,
  load,
  tokens = [],
  gameCcd,
  keptFree,
  isolated,
  windowsCore,
  legend = false,
}: {
  topo: Topology
  load: number[]
  tokens?: Token[]
  gameCcd?: number
  keptFree?: Set<number>
  /** PM-49: logical processor → the rule that isolates it. */
  isolated?: Map<number, string>
  /** PM-49: logical processors Autopilot keeps for Windows. */
  windowsCore?: Set<number>
  legend?: boolean
}) {
  const usage = (lp: number) => load[lp] ?? 0
  // Tokens without cores (only priorities changed) run anywhere; drawn once in a strip, not on every core.
  // Same program and role (four cl.exe) become one chip with a count.
  const anywhere = [...tokens.filter((t) => t.lps.length === 0).reduce((m, t) => {
    const k = `${t.role}:${t.label.toLowerCase()}`
    const g = m.get(k)
    if (g) { g.count++; g.title += `
${t.title}` } else m.set(k, { ...t, count: 1 })
    return m
  }, new Map<string, Token & { count: number }>()).values()]
  return (
    <div className="die">
      <div className="die-chiplets">
        {topo.ccds.map((c) => {
          const lps = c.cores.flatMap((k) => k.threads)
          const avg = lps.reduce((s, lp) => s + usage(lp), 0) / Math.max(1, lps.length)
          return (
            <section key={c.id} className={`chiplet ${gameCcd === c.id ? 'game' : ''}`} aria-label={`CCD ${c.id}`}>
              <header className="chiplet-head">
                <span className="chiplet-name">CCD {c.id}</span>
                <span className="chiplet-cache">
                  {c.l3Bytes ? `${Math.round(c.l3Bytes / 1048576)} MB L3` : ''}
                  {c.vCache && <span className="vcache">3D V-Cache</span>}
                  {gameCcd === c.id && <span className="game-tag">Game</span>}
                </span>
                <span className="chiplet-load">{Math.round(avg)}%</span>
              </header>
              <div className="core-grid">
                {c.cores.map((k) => {
                  const here = tokens.filter((t) => t.lps.some((lp) => k.threads.includes(lp)))
                  const kept = keptFree?.has(k.id)
                  const iso = k.threads.map((lp) => isolated?.get(lp)).find(Boolean)
                  const win = k.threads.some((lp) => windowsCore?.has(lp))
                  return (
                    <div
                      key={k.id}
                      className={`core ${k.ccdRank === 1 ? 'best' : ''} ${kept ? 'kept' : ''} ${iso ? 'isolated' : ''} ${win ? 'windows' : ''}`}
                      title={`Core ${k.id}${k.performance ? `, CPPC ${k.performance}` : ''}${k.rank ? `, rank ${k.rank} of ${topo.physical}` : ''}${k.tied ? ' (tied)' : ''}${kept ? '. Kept free by Autopilot.' : ''}${iso ? `. Isolated for ${iso}: no other process runs here.` : ''}${win ? '. Kept for Windows, interrupts and the shell.' : ''}`}
                    >
                      <div className="core-head">
                        <span>Core {k.id}</span>
                        {iso && <span className="core-tag iso" title={`Isolated for ${iso}`}>{shortRule(iso)}</span>}
                        {win && <span className="core-tag win">Windows</span>}
                        {k.ccdRank === 1 && <span className="star">★{k.tied ? '=' : ''}</span>}
                        {k.ccdRank === 2 && <span className="dot2">•</span>}
                      </div>
                      <div className="lanes">
                        {k.threads.map((lp) => (
                          <div key={lp} className="lane" title={`Thread ${lp}: ${Math.round(usage(lp))}%`}>
                            <div className="lane-fill" style={{ height: `${Math.max(3, usage(lp))}%` }} />
                            <span className="lane-id">{lp}</span>
                            <span className="lane-load">{Math.round(usage(lp))}</span>
                          </div>
                        ))}
                      </div>
                      {here.length > 0 && (
                        <div className="tokens">
                          {here.slice(0, 2).map((t) => (
                            <span key={t.key} className={`token role-${t.role}`} title={t.title}>
                              {t.label}
                            </span>
                          ))}
                          {here.length > 2 && (
                            <span className="token more" title={here.slice(2).map((t) => t.label).join(', ')}>
                              +{here.length - 2}
                            </span>
                          )}
                        </div>
                      )}
                    </div>
                  )
                })}
              </div>
            </section>
          )
        })}
      </div>
      {anywhere.length > 0 && (
        <div className="die-anywhere">
          <span className="die-anywhere-label">Any core</span>
          <div className="tokens">
            {anywhere.slice(0, 24).map((t) => (
              <span key={t.key} className={`token role-${t.role}`} title={t.title}>
                {t.label}
                {t.count > 1 && <span className="token-count"> ×{t.count}</span>}
              </span>
            ))}
            {anywhere.length > 24 && (
              <span className="token more" title={anywhere.slice(24).map((t) => t.label).join(', ')}>
                +{anywhere.length - 24}
              </span>
            )}
          </div>
        </div>
      )}
      {legend && (
        <div className="die-legend">
          {(Object.keys(ROLE_NAMES) as Role[]).map((r) => (
            <span key={r} className={`legend-item role-${r}`}>
              <i />
              {ROLE_NAMES[r]}
            </span>
          ))}
          <span className="legend-item kept">
            <i />
            Kept free
          </span>
          {isolated && isolated.size > 0 && (
            <span className="legend-item isolated">
              <i />
              Isolated
            </span>
          )}
          {windowsCore && windowsCore.size > 0 && (
            <span className="legend-item windows">
              <i />
              Kept for Windows
            </span>
          )}
          {topo.rankingAvailable && <span className="legend-item">★ fastest core of its CCD, • second fastest</span>}
        </div>
      )}
    </div>
  )
}

/** "Quick: obs64.exe" → "obs64": the program a quick rule is about, or the rule's own name. */
function shortRule(name: string) {
  return name.replace(/^Quick:\s*/, '').replace(/\.exe$/i, '')
}

/** "16-17,30-31" → [16, 17, 30, 31] */
export function parseCores(text: string): number[] {
  const out: number[] = []
  for (const part of text.split(',')) {
    const [a, b] = part.split('-').map((x) => parseInt(x, 10))
    if (Number.isNaN(a)) continue
    for (let i = a; i <= (Number.isNaN(b) ? a : b); i++) out.push(i)
  }
  return out
}
