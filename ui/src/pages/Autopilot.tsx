import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api'
import { DieMap, parseCores, type Role, type Token } from '../components/DieMap'
import { Group, Levels, ListRow, NumberBox, Row, Toggle } from '../components/SettingsControls'
import { usePoll } from '../hooks'
import type { AppSettings, ProcessRow, RuleError, Topology } from '../types'

const CPU_LEVELS = ['belowNormal', 'normal', 'aboveNormal', 'high']
const LOW_LEVELS = ['idle', 'belowNormal']

/** Which Autopilot part manages a process, from the rule that set its settings. */
function roleOf(p: ProcessRow): Role | undefined {
  const ids = (p.applied ?? []).map((a) => a.ruleId)
  if (ids.includes('autopilot-game')) return 'game'
  if (ids.includes('autopilot-build-tools')) return 'build'
  if (ids.some((id) => id.startsWith('autopilot-background') || id === 'autopilot-game-background')) return 'background'
  if (ids.includes('autopilot-balance')) return 'balance'
  return undefined
}

export function Autopilot() {
  const [settings, setSettings] = useState<AppSettings>()
  const [errors, setErrors] = useState<RuleError[]>([])
  const [savedAt, setSavedAt] = useState<number>()
  const [tab, setTab] = useState<'placements' | 'learned'>('placements')
  const timer = useRef<number | undefined>(undefined)

  const status = usePoll(api.status, 2000)
  const sys = usePoll(api.system, 2000)
  const topo = usePoll(api.topology, 600_000)
  const procs = usePoll(() => api.processes(), 3000)
  const placements = usePoll(api.placements, 3000)
  const preview = usePoll(api.autopilotPreview, 5000, settings?.autopilot.mode === 'observe')

  useEffect(() => {
    api.settings().then(setSettings)
  }, [])

  // Changes save on their own, like Windows Settings.
  const update = (fn: (s: AppSettings) => void) => {
    if (!settings) return
    const next = structuredClone(settings)
    fn(next)
    setSettings(next)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(async () => {
      try {
        await api.saveSettings(next)
        setErrors([])
        setSavedAt(Date.now())
      } catch (e) {
        if (e instanceof ApiError && e.status === 400) setErrors((e.body as { errors: RuleError[] }).errors ?? [])
      }
    }, 400)
  }

  const managed = useMemo(() => {
    const byRole: Record<Role, ProcessRow[]> = { game: [], build: [], background: [], balance: [] }
    for (const p of procs.data ?? []) {
      const r = roleOf(p)
      if (r) byRole[r].push(p)
    }
    return byRole
  }, [procs.data])

  const t = topo.data
  const game = status.data?.game
  const gameCcd = useMemo(() => gameCcdOf(settings, t), [settings, t])

  const tokens = useMemo<Token[]>(() => {
    const roles = new Map<number, Role>((procs.data ?? []).flatMap((p) => (roleOf(p) ? [[p.pid, roleOf(p)!] as [number, Role]] : [])))
    const list: Token[] = (placements.data ?? []).map((pl) => ({
      key: `p${pl.pid}`,
      label: pl.name.replace(/\.exe$/i, ''),
      role: roles.get(pl.pid) ?? 'background',
      lps: parseCores(pl.cores),
      title: `${pl.name} (PID ${pl.pid}) on threads ${pl.cores}: ${pl.why}`,
    }))
    if (game && t && gameCcd !== undefined) {
      const lps = t.ccds.find((c) => c.id === gameCcd)?.cores.flatMap((k) => k.threads) ?? []
      list.unshift({ key: 'game', label: game.name.replace(/\.exe$/i, ''), role: 'game', lps, title: `${game.name}: ${game.reason ?? 'game'}` })
    }
    return list
  }, [placements.data, procs.data, game, t, gameCcd])

  const keptFree = useMemo(() => {
    if (!t || !settings?.autopilot.enabled) return undefined
    const n = settings.autopilot.background.keepBestCoresFree
    return new Set(t.ccds.flatMap((c) => c.cores).filter((k) => k.rank > 0 && k.rank <= n).map((k) => k.id))
  }, [t, settings])

  if (!settings) return <div className="empty">Loading…</div>
  const a = settings.autopilot
  const g = a.gameMode
  const b = a.balance
  const bt = a.buildTools
  const bg = a.background
  const total = managed.build.length + managed.background.length + managed.balance.length + managed.game.length

  return (
    <div className="page dense autopilot">
      <header className="ap-hero">
        <div className="ap-switch">
          <h2 className="page-title">Autopilot</h2>
          <select aria-label="Autopilot mode" value={a.mode ?? (a.enabled ? 'on' : 'off')} onChange={(e) => update((x) => (x.autopilot.mode = e.target.value as 'off' | 'observe' | 'on'))}>
            <option value="off">Off</option><option value="observe">Observe</option><option value="on">On</option>
          </select>
        </div>
        <p className="ap-status">
          {a.mode === 'observe' ? <>Observing: learning and showing proposed changes without applying Autopilot settings.</> : !a.enabled ? (
            <>Autopilot is off. Your own rules still apply. Turn it on to let Corewright schedule games, builds and background work for you.</>
          ) : (
            <>
              {game ? (
                <>
                  Playing <strong>{game.name}</strong>
                  {gameCcd !== undefined ? <> on CCD {gameCcd}</> : null}.{' '}
                  <button
                    className="btn subtle ap-notgame"
                    title={`Detected because: ${game.reason ?? 'game'}. Adds ${game.name} to "Never games".`}
                    onClick={() =>
                      update((x) => {
                        const gm = x.autopilot.gameMode
                        gm.games = gm.games.filter((n) => n.toLowerCase() !== game.name.toLowerCase())
                        if (!gm.notGames.some((n) => n.toLowerCase() === game.name.toLowerCase())) gm.notGames.push(game.name)
                      })
                    }
                  >
                    Not a game
                  </button>{' '}
                </>
              ) : (
                <>No game running. </>
              )}
              {total === 0 ? <>Nothing needs managing right now.</> : <ManagedSummary managed={managed} />}
            </>
          )}
          {savedAt && <span className="ap-saved"> Saved.</span>}
        </p>
      </header>

      {errors.length > 0 && (
        <div className="banner danger">
          {errors.map((e, i) => (
            <div key={i}>
              {e.ruleName}: {e.message}
            </div>
          ))}
        </div>
      )}

      {a.mode === 'observe' && <section className="card">
        <h3>What Autopilot would do</h3>
        {(preview.data ?? []).map((p, i) => <p key={`${p.pid}-${p.slot}-${i}`}>{p.process} ({p.pid}): {p.slot} → {p.value}. {p.reason}</p>)}
        {preview.data?.length === 0 && <p>No proposed changes right now.</p>}
      </section>}

      {t && sys.data && (
        <section className="ap-map card">
          <DieMap topo={t} load={sys.data.threadUsage} tokens={a.enabled ? tokens : []} gameCcd={game ? gameCcd : undefined} keptFree={keptFree} legend />
        </section>
      )}

      <section className="ap-groups">
        <h3>What Autopilot does</h3>
        <div className={a.enabled ? '' : 'groups-off'}>
          <Group
            glyph={'\uE7FC'}
            role="game"
            title="Game mode"
            enabled={g.enabled}
            onToggle={(v) => update((x) => (x.autopilot.gameMode.enabled = v))}
            summary={game ? `Playing ${game.name}: ${game.reason ?? 'detected'}.` : 'Finds games by their library folder, or full screen with Direct3D or Vulkan, and gives them the best CCD.'}
          >
            <Row title="Game CPU priority" desc="Priority of the detected game.">
              <Levels levels={CPU_LEVELS} value={g.gamePriority} onChange={(v) => update((x) => (x.autopilot.gameMode.gamePriority = v))} />
            </Row>
            <Row title="Game GPU priority" desc="Scheduling priority of the game on the graphics card.">
              <Levels levels={CPU_LEVELS} value={g.gameGpuPriority} onChange={(v) => update((x) => (x.autopilot.gameMode.gameGpuPriority = v))} />
            </Row>
            <Row title="Run games on" desc={placementHint(t)}>
              <select value={g.placement} onChange={(e) => update((x) => (x.autopilot.gameMode.placement = e.target.value))}>
                <option value="auto">Automatic (recommended)</option>
                <option value="all">All cores</option>
                <option value="frequency-ccd">Fastest CCD</option>
                {t?.ccds.some((c) => c.vCache) && t.ccds.some((c) => !c.vCache) && <option value="vcache-ccd">V-Cache CCD</option>}
                {t && t.ccds.length > 1 && t.ccds.map((c) => (
                  <option key={c.id} value={`ccd${c.id}`}>
                    CCD {c.id}
                  </option>
                ))}
              </select>
            </Row>
            <Row title="Move background work off the game" desc="While a game runs, busy background processes leave its CCD and get lower priority.">
              <Toggle value={g.moveBackground} onChange={(v) => update((x) => (x.autopilot.gameMode.moveBackground = v))} />
            </Row>
            <Row title="Background priority while gaming" desc="CPU priority for busy background processes during a game.">
              <Levels levels={LOW_LEVELS} value={g.backgroundPriority} onChange={(v) => update((x) => (x.autopilot.gameMode.backgroundPriority = v))} />
            </Row>
            <ListRow title="Always games" desc="Treated as games even when they run in a window." value={g.games} onChange={(v) => update((x) => (x.autopilot.gameMode.games = v))} />
            <ListRow title="Never games" desc="Browsers, video players and editors that go full screen." value={g.notGames} onChange={(v) => update((x) => (x.autopilot.gameMode.notGames = v))} />
            <ListRow title="Keep responsive" desc="Voice chat and streaming stay above normal and are never moved while you play." value={g.keepResponsive} onChange={(v) => update((x) => (x.autopilot.gameMode.keepResponsive = v))} />
          </Group>

          <Group
            glyph={'\uE943'}
            role="build"
            title="Compilers and build tools"
            enabled={bt.enabled}
            onToggle={(v) => update((x) => (x.autopilot.buildTools.enabled = v))}
            summary={`High load but not latency critical: ${bt.programs.length} known ${bt.programs.length === 1 ? 'tool runs' : 'tools run'} with lower CPU and I/O priority.`}
          >
            <Row title="Build priority" desc="CPU priority for compilers, linkers and build drivers.">
              <Levels levels={LOW_LEVELS} value={bt.priority} onChange={(v) => update((x) => (x.autopilot.buildTools.priority = v))} />
            </Row>
            <ListRow title="Build tools" desc="MSVC, Clang, GCC, Rust, .NET, Go, Java, shader compilers and build drivers. Add your own." value={bt.programs} onChange={(v) => update((x) => (x.autopilot.buildTools.programs = v))} />
          </Group>

          <Group
            glyph={'\uE9D9'}
            role="balance"
            title="Balancing"
            enabled={b.enabled}
            onToggle={(v) => update((x) => (x.autopilot.balance.enabled = v))}
            summary={`Under CPU pressure, background programs using at least ${b.cpuCores} of a core for ${b.sustainSeconds} s get lower priority.`}
          >
            <Row title="CPU threshold" desc="Per-process load in cores, while the system or foreground app is busy.">
              <NumberBox value={b.cpuCores} min={0.1} max={64} step={0.1} unit="cores" onChange={(v) => update((x) => (x.autopilot.balance.cpuCores = v))} />
            </Row>
            <Row title="Act after" desc="How long a process must stay above the threshold.">
              <NumberBox value={b.sustainSeconds} min={0} step={0.5} unit="s" onChange={(v) => update((x) => (x.autopilot.balance.sustainSeconds = v))} />
            </Row>
            <Row title="Restore after" desc="How long it must stay calm before its priority comes back.">
              <NumberBox value={b.releaseSeconds} min={0} step={0.5} unit="s" onChange={(v) => update((x) => (x.autopilot.balance.releaseSeconds = v))} />
            </Row>
            <Row title="Held-back priority" desc="Priority while a process is held back.">
              <Levels levels={LOW_LEVELS} value={b.priority} onChange={(v) => update((x) => (x.autopilot.balance.priority = v))} />
            </Row>
            <ListRow title="Never hold back" desc="Processes balancing leaves alone." value={b.exclusions} onChange={(v) => update((x) => (x.autopilot.balance.exclusions = v))} />
          </Group>

          <Group
            glyph={'\uE9F5'}
            role="background"
            title="Background placement"
            enabled={bg.enabled}
            onToggle={(v) => update((x) => (x.autopilot.background.enabled = v))}
            summary={`Learns what each program really is (python.exe runs many things), how much it works and how hard it bursts, and whether its app is ever in front. Clear background work moves off your ${bg.keepBestCoresFree} strongest cores${bg.includeServices ? ', services included' : ''}.`}
          >
            <Row title="Include services" desc="Windows Update, indexing, backup and database services are judged like programs. Audio, input, display drivers and the network core are always left alone.">
              <Toggle value={bg.includeServices} onChange={(v) => update((x) => (x.autopilot.background.includeServices = v))} />
            </Row>
            <Row title="Observe at least" desc="Runtime a program needs before Corewright decides anything about it.">
              <NumberBox value={bg.minObservedMinutes} min={5} step={5} unit="min" onChange={(v) => update((x) => (x.autopilot.background.minObservedMinutes = v))} />
            </Row>
            <Row title="Foreground share" desc="Programs whose app is in front more often than this are interactive. Anything in front in the last 30 minutes is too.">
              <NumberBox value={bg.maxForegroundPercent} min={0} step={0.5} unit="%" onChange={(v) => update((x) => (x.autopilot.background.maxForegroundPercent = v))} />
            </Row>
            <Row title="Single-core load" desc="Programs whose bursts (95th percentile) stay below this many cores only get placed; busier ones also get lower priority.">
              <NumberBox value={bg.singleCoreLoad} min={0.5} step={0.1} unit="cores" onChange={(v) => update((x) => (x.autopilot.background.singleCoreLoad = v))} />
            </Row>
            <Row title="Keep the strongest cores free" desc="Fastest cores (CPPC ranking) that no background program may use. Hatched on the map.">
              <NumberBox value={bg.keepBestCoresFree} min={0} max={16} step={1} onChange={(v) => update((x) => (x.autopilot.background.keepBestCoresFree = v))} />
            </Row>
            <Row title="Multi-threaded background priority" desc="Busy background programs that use several cores get this priority.">
              <Levels levels={LOW_LEVELS} value={bg.priority} onChange={(v) => update((x) => (x.autopilot.background.priority = v))} />
            </Row>
            <ListRow title="Never place" desc="Programs background placement leaves alone." value={bg.exclusions} onChange={(v) => update((x) => (x.autopilot.background.exclusions = v))} />
            <ListRow title="Launchers" desc="Never placed automatically. Node, Java and Gradle count when they have children." value={a.launchers ?? []} onChange={(v) => update((x) => (x.autopilot.launchers = v))} />
          </Group>
        </div>
      </section>
      <section className="ap-tables card">
        {/* Lists scroll inside the card so the settings stay one short scroll away (owner, 2026-11-02). */}
        <div className="ap-tabs">
          <div className="seg">
            <button className={tab === 'placements' ? 'on' : ''} onClick={() => setTab('placements')}>
              Where things run{placements.data?.length ? <span className="seg-count">{placements.data.length}</span> : null}
            </button>
            <button className={tab === 'learned' ? 'on' : ''} onClick={() => setTab('learned')}>
              What Corewright has learned
            </button>
          </div>
          <span className="secondary tiny">
            {tab === 'placements'
              ? 'Placed once on the CCD where they fit best, then kept there so their caches stay warm.'
              : 'Hours of observation per program. Decisions update once a minute; statistics are kept for 30 days.'}
          </span>
        </div>
        <div className="ap-scroll">{tab === 'placements' ? <Placements rows={placements.data ?? []} procs={procs.data ?? []} /> : <Learned />}</div>
      </section>

    </div>
  )
}

/** "Managing 3 processes: 2 build tools and 1 in the background." Only the parts that are non-zero. */
function ManagedSummary({ managed }: { managed: Record<Role, ProcessRow[]> }) {
  const parts: { role: Role; text: string }[] = []
  const n = (k: Role) => managed[k].length
  if (n('build')) parts.push({ role: 'build', text: `${n('build')} ${n('build') === 1 ? 'build tool' : 'build tools'}` })
  if (n('background')) parts.push({ role: 'background', text: `${n('background')} in the background` })
  if (n('balance')) parts.push({ role: 'balance', text: `${n('balance')} held back for hogging the CPU` })
  const total = parts.length
  return (
    <>
      Managing{' '}
      {parts.map((p, i) => (
        <span key={p.role}>
          <span className={`count role-${p.role}`}>
            <i />
            {p.text}
          </span>
          {i < total - 2 ? ', ' : i === total - 2 ? ' and ' : '.'}
        </span>
      ))}
    </>
  )
}


function gameCcdOf(s?: AppSettings, t?: Topology): number | undefined {
  if (!s || !t || t.ccds.length < 2) return undefined
  const p = s.autopilot.gameMode.placement
  if (p === 'all') return undefined
  if (p.startsWith('ccd')) return Number(p.slice(3))
  const vc = t.ccds.filter((c) => c.vCache)
  if ((p === 'auto' || p === 'vcache-ccd') && vc.length === 1) return vc[0].id
  if (p === 'vcache-ccd') return undefined
  return t.ccds.find((c) => c.cores.some((k) => k.rank === 1))?.id
}

function placementHint(t?: Topology) {
  if (!t) return 'Which cores the game runs on.'
  if (t.ccds.length < 2) return 'This CPU has a single CCD, so games keep all cores.'
  const vc = t.ccds.filter((c) => c.vCache)
  if (vc.length === 1) return `Automatic puts games on CCD ${vc[0].id}, the one with 3D V-Cache.`
  const fastest = t.ccds.find((c) => c.cores.some((k) => k.rank === 1))
  return `Automatic puts games on CCD ${fastest?.id ?? 0}${vc.length > 1 ? ': both CCDs have V-Cache, this one has the fastest cores.' : ', the one with the fastest cores.'}`
}

function Placements({ rows, procs }: { rows: { pid: number; name: string; cores: string; ccd: number; demandThreads: number; placedSecondsAgo: number; why: string }[]; procs: ProcessRow[] }) {
  const roles = new Map(procs.map((p) => [p.pid, roleOf(p)]))
  if (rows.length === 0) return <div className="ap-empty">No process has automatic cores right now. Background programs appear here as soon as Autopilot places them.</div>
  return (
    <table className="ap-table">
      <thead>
        <tr>
          <th>Process</th>
          <th>Threads</th>
          <th className="num">Needs</th>
          <th className="num">Placed</th>
          <th>Why there</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.pid}>
            <td>
              <span className={`count role-${roles.get(r.pid) ?? 'background'}`}>
                <i />
                {r.name}
              </span>
              <span className="secondary tiny"> {r.pid}</span>
            </td>
            <td>
              CCD {r.ccd}, {r.cores}
            </td>
            <td className="num">{r.demandThreads}</td>
            <td className="num secondary">{r.placedSecondsAgo >= 120 ? `${Math.round(r.placedSecondsAgo / 60)} min ago` : `${r.placedSecondsAgo} s ago`}</td>
            <td className="secondary why" title={r.why}>
              {r.why}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function Learned() {
  const rows = usePoll(api.background, 15_000)
  const list = (rows.data ?? []).filter((r) => !r.verdict.startsWith('Idle')).slice(0, 80)
  if (list.length === 0) return <div className="ap-empty">Nothing learned yet. Corewright watches every program for an hour before it decides anything.</div>
  return (
    <table className="ap-table">
      <thead>
        <tr>
          <th>Program</th>
          <th className="num">Watched</th>
          <th className="num">In front</th>
          <th className="num">Avg cores</th>
          <th className="num">Bursts to</th>
          <th>Decision</th>
        </tr>
      </thead>
      <tbody>
        {list.map((r) => (
          <tr key={r.program} title={`${r.reason}\n${r.program}`}>
            <td>
              {r.name}
              {r.service && <span className="tag">service</span>}
            </td>
            <td className="num secondary">{r.observedMinutes >= 120 ? `${(r.observedMinutes / 60).toFixed(1)} h` : `${Math.round(r.observedMinutes)} min`}</td>
            <td className="num secondary">{r.foregroundPercent} %</td>
            <td className="num">{r.averageCores}</td>
            <td className="num">{r.p95Cores}</td>
            <td className={r.verdict.startsWith('Background') ? 'decision on' : r.verdict.startsWith('Left alone') ? 'decision crit' : 'decision'}>{r.verdict}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
