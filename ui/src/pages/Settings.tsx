import { useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../api'
import { ChipInput } from '../components/ConditionBuilder'
import { UpdatesCard } from '../components/UpdatesCard'
import { isHost, levelLabel, post } from '../host'
import { usePoll } from '../hooks'
import type { AppSettings, RuleError, Topology } from '../types'

const CPU_LEVELS = ['belowNormal', 'normal', 'aboveNormal', 'high']
const LOW_LEVELS = ['idle', 'belowNormal']

/** Windows Settings style page: changes save automatically, like the Settings app. */
export function Settings() {
  const status = usePoll(api.status, 3000)
  const topo = usePoll(api.topology, 600_000)
  const [settings, setSettings] = useState<AppSettings>()
  const [errors, setErrors] = useState<RuleError[]>([])
  const [saved, setSaved] = useState<string>()
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => {
    api.settings().then(setSettings)
  }, [])

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
        setSaved('Saved')
        window.setTimeout(() => setSaved(undefined), 1500)
      } catch (e) {
        if (e instanceof ApiError && e.status === 400) setErrors((e.body as { errors: RuleError[] }).errors ?? [])
      }
    }, 400)
  }

  if (!settings) return <div className="empty">Loading…</div>
  const a = settings.autopilot
  const g = a.gameMode
  const b = a.balance
  const bg = a.background
  const s = status.data

  return (
    <div className="page dense settings">
      <div className="toolbar">
        <h2 className="page-title">Settings</h2>
        {saved && <span className="secondary tiny">{saved}</span>}
      </div>
      {errors.length > 0 && (
        <div className="banner danger">
          {errors.map((e, i) => (
            <div key={i}>
              {e.ruleName}: {e.message}
            </div>
          ))}
        </div>
      )}

      <section className="section">
        <h3>Autopilot</h3>
        <Card glyph={'\uE945'} title="Autopilot" desc="Schedules processes automatically on top of your rules: game mode and balancing. Your own rules always win.">
          <Toggle value={a.enabled} onChange={(v) => update((x) => (x.autopilot.enabled = v))} />
        </Card>
        <div className={`card-group ${a.enabled ? '' : 'disabled'}`}>
          <Card glyph={'\uE7FC'} title="Game mode" desc={gameStatus(s?.game)}>
            <Toggle value={g.enabled} onChange={(v) => update((x) => (x.autopilot.gameMode.enabled = v))} />
          </Card>
          <Row title="Game CPU priority" desc="Priority of the detected game.">
            <Levels levels={CPU_LEVELS} value={g.gamePriority} onChange={(v) => update((x) => (x.autopilot.gameMode.gamePriority = v))} />
          </Row>
          <Row title="Game GPU priority" desc="Scheduling priority of the game on the graphics card.">
            <Levels levels={CPU_LEVELS} value={g.gameGpuPriority} onChange={(v) => update((x) => (x.autopilot.gameMode.gameGpuPriority = v))} />
          </Row>
          <Row title="Run games on" desc={placementHint(topo.data)}>
            <select value={g.placement} onChange={(e) => update((x) => (x.autopilot.gameMode.placement = e.target.value))}>
              <option value="auto">Automatic (recommended)</option>
              <option value="all">All cores</option>
              <option value="frequency-ccd">Fastest CCD</option>
              {topo.data?.ccds.some((c) => c.vCache) && topo.data.ccds.some((c) => !c.vCache) && <option value="vcache-ccd">V-Cache CCD</option>}
              {topo.data && topo.data.ccds.length > 1 && topo.data.ccds.map((c) => (
                <option key={c.id} value={`ccd${c.id}`}>
                  CCD {c.id}
                </option>
              ))}
            </select>
          </Row>
          <Row title="Move background work off the game" desc="While a game runs, busy background processes get lower priority and the game's CCD to themselves.">
            <Toggle value={g.moveBackground} onChange={(v) => update((x) => (x.autopilot.gameMode.moveBackground = v))} />
          </Row>
          <Row title="Background priority while gaming" desc="CPU priority for busy background processes during a game.">
            <Levels levels={LOW_LEVELS} value={g.backgroundPriority} onChange={(v) => update((x) => (x.autopilot.gameMode.backgroundPriority = v))} />
          </Row>
          <ListRow title="Always games" desc="Treated as games even when they run in a window." value={g.games} onChange={(v) => update((x) => (x.autopilot.gameMode.games = v))} />
          <ListRow title="Never games" desc="Browsers, video players and editors that go full screen." value={g.notGames} onChange={(v) => update((x) => (x.autopilot.gameMode.notGames = v))} />
          <ListRow title="Keep responsive" desc="Voice chat and streaming: kept above normal and never moved while you play." value={g.keepResponsive} onChange={(v) => update((x) => (x.autopilot.gameMode.keepResponsive = v))} />

          <Card glyph={'\uE943'} title="Compilers and build tools" desc="High load but not latency critical: builds run below normal with low disk priority, so the PC stays responsive while you compile.">
            <Toggle value={a.buildTools.enabled} onChange={(v) => update((x) => (x.autopilot.buildTools.enabled = v))} />
          </Card>
          <Row title="Build priority" desc="CPU priority for compilers, linkers and build drivers.">
            <Levels levels={LOW_LEVELS} value={a.buildTools.priority} onChange={(v) => update((x) => (x.autopilot.buildTools.priority = v))} />
          </Row>
          <ListRow title="Build tools" desc="MSVC, Clang, GCC, Rust, .NET, Go, Java, shader compilers and build drivers. Add your own." value={a.buildTools.programs} onChange={(v) => update((x) => (x.autopilot.buildTools.programs = v))} />

          <Card glyph={'\uE9D9'} title="Balancing" desc="Like ProBalance: background processes that hog the CPU get lower priority until they calm down. The foreground app and services are never touched.">
            <Toggle value={b.enabled} onChange={(v) => update((x) => (x.autopilot.balance.enabled = v))} />
          </Card>
          <Row title="CPU threshold" desc="Per-process CPU, as a share of the whole machine, that counts as hogging.">
            <Unit unit="%">
              <input type="number" min={1} max={100} step={1} className="num-input" value={b.cpuPercent} onChange={(e) => update((x) => (x.autopilot.balance.cpuPercent = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Act after" desc="How long a process must stay above the threshold.">
            <Unit unit="s">
              <input type="number" min={0} step={0.5} className="num-input" value={b.sustainSeconds} onChange={(e) => update((x) => (x.autopilot.balance.sustainSeconds = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Restore after" desc="How long it must stay calm before its priority comes back.">
            <Unit unit="s">
              <input type="number" min={0} step={0.5} className="num-input" value={b.releaseSeconds} onChange={(e) => update((x) => (x.autopilot.balance.releaseSeconds = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Lowered priority" desc="Priority while a process is restrained.">
            <Levels levels={LOW_LEVELS} value={b.priority} onChange={(v) => update((x) => (x.autopilot.balance.priority = v))} />
          </Row>
          <ListRow title="Never restrain" desc="Processes balancing leaves alone." value={b.exclusions} onChange={(v) => update((x) => (x.autopilot.balance.exclusions = v))} />

          <Card glyph={'\uE9F5'} title="Background placement" desc="Learns over hours which programs never come to the foreground and keeps them away from your strongest cores. Services are never touched.">
            <Toggle value={bg.enabled} onChange={(v) => update((x) => (x.autopilot.background.enabled = v))} />
          </Card>
          <Row title="Observe at least" desc="Runtime a program needs before Corewright decides anything about it.">
            <Unit unit="min">
              <input type="number" min={5} step={5} className="num-input" value={bg.minObservedMinutes} onChange={(e) => update((x) => (x.autopilot.background.minObservedMinutes = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Foreground share" desc="Programs in the foreground more often than this are interactive and left alone.">
            <Unit unit="%">
              <input type="number" min={0} step={0.5} className="num-input" value={bg.maxForegroundPercent} onChange={(e) => update((x) => (x.autopilot.background.maxForegroundPercent = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Single-core load" desc="Average load up to this many cores counts as single-threaded: one physical core, filled from the back.">
            <Unit unit="cores">
              <input type="number" min={0.5} step={0.1} className="num-input" value={bg.singleCoreLoad} onChange={(e) => update((x) => (x.autopilot.background.singleCoreLoad = Number(e.target.value)))} />
            </Unit>
          </Row>
          <Row title="Keep the strongest cores free" desc="Number of fastest cores (CPPC ranking) background programs never use.">
            <input type="number" min={0} max={16} step={1} className="num-input" value={bg.keepBestCoresFree} onChange={(e) => update((x) => (x.autopilot.background.keepBestCoresFree = Number(e.target.value)))} />
          </Row>
          <Row title="Multi-threaded background priority" desc="Busy background programs that use several cores get this priority.">
            <Levels levels={LOW_LEVELS} value={bg.priority} onChange={(v) => update((x) => (x.autopilot.background.priority = v))} />
          </Row>
          <ListRow title="Never place" desc="Programs background placement leaves alone." value={bg.exclusions} onChange={(v) => update((x) => (x.autopilot.background.exclusions = v))} />
          <BackgroundTable />
          <PlacementTable />
        </div>
      </section>

      <section className="section">
        <h3>Startup</h3>
        <Autostart />
      </section>
      <UpdatesCard />

      <section className="section">
        <h3>About</h3>
        <div className="card-group">
          <Row title="Version" desc="Corewright">
            <span>{s?.version}</span>
          </Row>
          <Row title="Service" desc={s?.mode === 'service' ? 'Installed Windows service' : 'Running as an app (start.bat)'}>
            <span>{s ? (s.elevated || s.mode === 'service' ? 'Full access' : 'Limited: only your own processes') : ''}</span>
          </Row>
          <Row title="Data folder" desc="Rules, settings and logs.">
            <span className="mono selectable">{s?.dataDirectory}</span>
          </Row>
        </div>
      </section>
    </div>
  )
}

/** What the observer has learned so far: the programs it watched the longest and its verdict for each. */
function BackgroundTable() {
  const rows = usePoll(api.background, 15_000)
  const list = (rows.data ?? []).filter((r) => r.verdict !== 'Idle').slice(0, 40)
  return (
    <div className="setting-row bg-table">
      <div className="setting-text">
        <div className="setting-title">What Corewright has learned</div>
        <div className="setting-desc">Programs by total CPU use. Decisions update once a minute; statistics are kept for 30 days.</div>
        <table className="mini">
          <thead>
            <tr>
              <th>Program</th>
              <th className="num">Observed</th>
              <th className="num">Foreground</th>
              <th className="num">Avg cores</th>
              <th>Decision</th>
              <th>Cores</th>
            </tr>
          </thead>
          <tbody>
            {list.map((r) => (
              <tr key={r.name} title={r.reason}>
                <td>{r.name}</td>
                <td className="num secondary">{r.observedMinutes >= 120 ? `${(r.observedMinutes / 60).toFixed(1)} h` : `${Math.round(r.observedMinutes)} min`}</td>
                <td className="num secondary">{r.foregroundPercent} %</td>
                <td className="num secondary">{r.averageCores}</td>
                <td className={r.verdict.startsWith('Background') ? 'accent-text' : 'secondary'}>{r.verdict}</td>
                <td className="secondary">{r.affinity ?? ''}</td>
              </tr>
            ))}
            {list.length === 0 && (
              <tr>
                <td className="secondary">Nothing learned yet. Corewright needs some runtime first.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

/** Which process the placer put where right now, and why: the whole picture behind the decisions. */
function PlacementTable() {
  const rows = usePoll(api.placements, 3000)
  const list = rows.data ?? []
  return (
    <div className="setting-row bg-table">
      <div className="setting-text">
        <div className="setting-title">Live placements</div>
        <div className="setting-desc">
          Processes with automatic cores. Placed once on the CCD where their demand is cheapest (load, reservations, core strength, game CCD, V-Cache), then
          kept there; they only grow within that CCD.
        </div>
        <table className="mini">
          <thead>
            <tr>
              <th>Process</th>
              <th className="num">PID</th>
              <th>CCD</th>
              <th>Threads</th>
              <th className="num">Demand</th>
              <th className="num">Since</th>
              <th>Why</th>
            </tr>
          </thead>
          <tbody>
            {list.map((r) => (
              <tr key={r.pid}>
                <td>{r.name}</td>
                <td className="num secondary">{r.pid}</td>
                <td className="secondary">CCD {r.ccd}</td>
                <td className="accent-text">{r.cores}</td>
                <td className="num secondary">{r.demandThreads}</td>
                <td className="num secondary">{r.placedSecondsAgo >= 120 ? `${Math.round(r.placedSecondsAgo / 60)} min` : `${r.placedSecondsAgo} s`}</td>
                <td className="secondary" title={r.why}>
                  {r.why}
                </td>
              </tr>
            ))}
            {list.length === 0 && (
              <tr>
                <td className="secondary">No process uses automatic placement right now.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function gameStatus(game?: { name: string; pid: number; reason?: string } | null) {
  return game ? `Playing ${game.name} (PID ${game.pid}): ${game.reason ?? 'detected'}.` : 'Detects games (game libraries, full screen with Direct3D or Vulkan) and gives them priority. No game running now.'
}

function placementHint(t?: Topology) {
  if (!t) return 'Which cores the game runs on.'
  if (t.ccds.length < 2) return 'This CPU has a single CCD, so games keep all cores.'
  const vc = t.ccds.filter((c) => c.vCache)
  if (vc.length === 1) return `Automatic puts games on CCD ${vc[0].id} (3D V-Cache).`
  const fastest = t.ccds.find((c) => c.cores.some((k) => k.rank === 1))
  return `Automatic puts games on CCD ${fastest?.id ?? 0}${vc.length > 1 ? ' (both CCDs have V-Cache; this one has the fastest cores)' : ' (fastest cores)'}.`
}

/** Start with Windows: the dashboard window writes the per-user Run entry (same one the installer uses). */
function Autostart() {
  const [state, setState] = useState<{ enabled: boolean; available: boolean; note?: string }>()
  useEffect(() => {
    const wv = window.chrome?.webview
    if (!wv) return
    const on = (e: { data: unknown }) => {
      const d = e.data as { type?: string; enabled?: boolean; available?: boolean; note?: string }
      if (d?.type === 'autostart') setState({ enabled: !!d.enabled, available: !!d.available, note: d.note })
    }
    wv.addEventListener('message', on)
    post({ type: 'autostart-get' })
    return () => wv.removeEventListener('message', on)
  }, [])
  return (
    <Card glyph={'\uE7E8'} title="Start Corewright when I sign in" desc={!isHost ? 'Available in the Corewright window.' : (state?.note ?? 'Starts the tray icon at sign-in; the service itself starts with Windows.')}>
      <Toggle value={!!state?.enabled} disabled={!state?.available} onChange={(v) => post({ type: 'autostart-set', enabled: v })} />
    </Card>
  )
}

function Card({ glyph, title, desc, children }: { glyph: string; title: string; desc: string; children: React.ReactNode }) {
  return (
    <div className="setting-card">
      <span className="setting-icon glyph">{glyph}</span>
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      <div className="setting-control">{children}</div>
    </div>
  )
}

function Row({ title, desc, children }: { title: string; desc: string; children: React.ReactNode }) {
  return (
    <div className="setting-row">
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      <div className="setting-control">{children}</div>
    </div>
  )
}

function ListRow({ title, desc, value, onChange }: { title: string; desc: string; value: string[]; onChange: (v: string[]) => void }) {
  return (
    <div className="setting-row list-row">
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      <div className="setting-list">
        <ChipInput value={value.join(', ')} onChange={(v) => onChange(v.split(',').map((x) => x.trim()).filter(Boolean))} />
      </div>
    </div>
  )
}

function Toggle({ value, onChange, disabled }: { value: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <label className="toggle-label">
      {value ? 'On' : 'Off'}
      <span className="switch">
        <input type="checkbox" checked={value} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
        <span />
      </span>
    </label>
  )
}

function Levels({ levels, value, onChange }: { levels: string[]; value: string; onChange: (v: string) => void }) {
  return (
    <div className="seg levels">
      {levels.map((l) => (
        <button key={l} className={value === l ? 'on' : ''} onClick={() => onChange(l)}>
          {levelLabel(l)}
        </button>
      ))}
    </div>
  )
}

function Unit({ unit, children }: { unit: string; children: React.ReactNode }) {
  return (
    <span className="unit-wrap">
      {children}
      <span className="unit">{unit}</span>
    </span>
  )
}
