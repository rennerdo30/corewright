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
  legend = false,
}: {
  topo: Topology
  load: number[]
  tokens?: Token[]
  gameCcd?: number
  keptFree?: Set<number>
  legend?: boolean
}) {
  const usage = (lp: number) => load[lp] ?? 0
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
                  return (
                    <div
                      key={k.id}
                      className={`core ${k.ccdRank === 1 ? 'best' : ''} ${kept ? 'kept' : ''}`}
                      title={`Core ${k.id}${k.performance ? `, CPPC ${k.performance}` : ''}${k.rank ? `, rank ${k.rank} of ${topo.physical}` : ''}${k.tied ? ' (tied)' : ''}${kept ? '. Kept free by Autopilot.' : ''}`}
                    >
                      <div className="core-head">
                        <span>Core {k.id}</span>
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
          {topo.rankingAvailable && <span className="legend-item">★ fastest core of its CCD, • second fastest</span>}
        </div>
      )}
    </div>
  )
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
