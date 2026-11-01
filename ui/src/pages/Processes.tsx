import { useVirtualizer } from '@tanstack/react-virtual'
import { useMemo, useRef, useState } from 'react'
import { api } from '../api'
import { ContextMenu } from '../components/ContextMenu'
import { buildProcessMenu } from '../components/processMenu'
import { bytes, newId, percent } from '../format'
import { iconUrl, isHost, levelLabel, post, useDescriptions } from '../host'
import { usePoll } from '../hooks'
import { quickRule, setQuick } from '../quick'
import type { ProcessRow, Rule, RuleSet } from '../types'

type SortKey = 'name' | 'pid' | 'cpu' | 'privateBytes' | 'ioBytesPerSec' | 'threads'
type View = 'apps' | 'tree' | 'list'

/** One visible line: a process, or an app group (all processes with the same exe name). */
interface Line {
  key: string
  p: ProcessRow // the process, or the group's first process (used for menus and details)
  depth: number
  count: number // > 1 for groups
  expandable: boolean
  expanded: boolean
  cpu: number
  privateBytes: number
  ioBytesPerSec: number
  threads: number
  priority: string
  managed: boolean
}

const RATE_FULL = 50 * 1024 * 1024 // I/O heat is full at 50 MB/s

export function Processes({ onCreateRule }: { onCreateRule: (r: Rule) => void }) {
  const procs = usePoll(api.processes, 1000)
  const sys = usePoll(api.system, 2000)
  const [search, setSearch] = useState('')
  const [view, setView] = useState<View>('apps')
  const [managedOnly, setManagedOnly] = useState(false)
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'cpu', desc: true })
  const [toggled, setToggled] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<number>()
  const [menu, setMenu] = useState<{ x: number; y: number; p: ProcessRow; rules?: RuleSet }>()
  const [toast, setToast] = useState<string>()

  const list = procs.data ?? []
  const descriptions = useDescriptions(list.map((p) => p.path))
  const label = (p: ProcessRow) => (p.path && descriptions.get(p.path)) || p.name
  const memTotal = sys.data?.memoryTotal ?? 0

  const lines = useMemo<Line[]>(() => {
    const q = search.trim().toLowerCase()
    const visible = list.filter(
      (p) =>
        (!managedOnly || p.applied?.length) &&
        (!q || p.name.toLowerCase().includes(q) || String(p.pid) === q || p.path?.toLowerCase().includes(q) || label(p).toLowerCase().includes(q)),
    )
    const value = (l: Line) =>
      sort.key === 'name' ? 0 : sort.key === 'pid' ? l.p.pid : sort.key === 'cpu' ? l.cpu : sort.key === 'privateBytes' ? l.privateBytes : sort.key === 'ioBytesPerSec' ? l.ioBytesPerSec : l.threads
    const cmp = (a: Line, b: Line) => {
      const r = sort.key === 'name' ? label(a.p).localeCompare(label(b.p)) : value(a) - value(b)
      return sort.desc ? -r : r
    }
    const one = (p: ProcessRow, depth: number, expandable = false, expanded = false): Line => ({
      key: `p${p.pid}`,
      p,
      depth,
      count: 1,
      expandable,
      expanded,
      cpu: p.cpu,
      privateBytes: p.privateBytes,
      ioBytesPerSec: p.ioBytesPerSec,
      threads: p.threads,
      priority: p.priority,
      managed: !!p.applied?.length,
    })

    if (view === 'list') return visible.map((p) => one(p, 0)).sort(cmp)

    if (view === 'apps') {
      // Like Task Manager: processes with the same exe form one app line with a count; groups start collapsed.
      const groups = new Map<string, ProcessRow[]>()
      for (const p of visible) {
        const k = p.name.toLowerCase()
        const g = groups.get(k)
        if (g) g.push(p)
        else groups.set(k, [p])
      }
      const heads = [...groups.entries()].map(([k, ps]): Line => {
        if (ps.length === 1) return one(ps[0], 0)
        const prios = new Set(ps.map((p) => p.priority))
        return {
          key: `g${k}`,
          p: ps[0],
          depth: 0,
          count: ps.length,
          expandable: true,
          expanded: toggled.has(k),
          cpu: ps.reduce((s, p) => s + p.cpu, 0),
          privateBytes: ps.reduce((s, p) => s + p.privateBytes, 0),
          ioBytesPerSec: ps.reduce((s, p) => s + p.ioBytesPerSec, 0),
          threads: ps.reduce((s, p) => s + p.threads, 0),
          priority: prios.size === 1 ? ps[0].priority : 'mixed',
          managed: ps.some((p) => p.applied?.length),
        }
      })
      const out: Line[] = []
      for (const h of heads.sort(cmp)) {
        out.push(h)
        if (h.count > 1 && h.expanded) out.push(...groups.get(h.p.name.toLowerCase())!.map((p) => one(p, 1)).sort(cmp))
      }
      return out
    }

    // Tree by parent process; starts expanded.
    const pids = new Set(list.map((p) => p.pid))
    const byParent = new Map<number, ProcessRow[]>()
    const roots: ProcessRow[] = []
    for (const p of visible) {
      if (!q && !managedOnly && p.parentPid != null && pids.has(p.parentPid) && p.parentPid !== p.pid) {
        const arr = byParent.get(p.parentPid) ?? []
        arr.push(p)
        byParent.set(p.parentPid, arr)
      } else roots.push(p)
    }
    const out: Line[] = []
    const walk = (p: ProcessRow, depth: number) => {
      const kids = byParent.get(p.pid)
      const expanded = !toggled.has(`p${p.pid}`)
      out.push(one(p, depth, !!kids?.length, expanded))
      if (kids && expanded) for (const k of kids.map((x) => one(x, 0)).sort(cmp)) walk(k.p, depth + 1)
    }
    for (const r of roots.map((x) => one(x, 0)).sort(cmp)) walk(r.p, 0)
    return out
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [list, search, view, managedOnly, sort, toggled, descriptions.size])

  const scroller = useRef<HTMLDivElement>(null)
  const virtual = useVirtualizer({ count: lines.length, getScrollElement: () => scroller.current, estimateSize: () => 30, overscan: 16 })

  const toggle = (l: Line) =>
    setToggled((s) => {
      const k = l.count > 1 ? l.p.name.toLowerCase() : l.key
      const n = new Set(s)
      if (n.has(k)) n.delete(k)
      else n.add(k)
      return n
    })

  const notify = (text: string) => {
    setToast(text)
    window.setTimeout(() => setToast(undefined), 2500)
  }

  const openMenu = async (e: React.MouseEvent, p: ProcessRow) => {
    e.preventDefault()
    setSelected(p.pid)
    setMenu({ x: e.clientX, y: e.clientY, p })
    try {
      const rules = await api.rules()
      setMenu((m) => (m && m.p.pid === p.pid ? { ...m, rules } : m))
    } catch {
      /* the menu still works, without check marks */
    }
  }

  const saveSnapshot = () => {
    const blob = new Blob([JSON.stringify({ takenAt: new Date().toISOString(), processes: list }, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `corewright-processes-${new Date().toISOString().replace(/[:.]/g, '-')}.json`
    a.click()
    URL.revokeObjectURL(a.href)
  }

  const s = sys.data
  const headers: { key: SortKey | null; label: string; total?: string; num?: boolean; col: string }[] = [
    { key: 'name', label: 'Name', col: 'name' },
    { key: 'pid', label: 'PID', num: true, col: 'pid' },
    { key: null, label: 'Priority', col: 'priority' },
    { key: 'cpu', label: 'CPU', total: s ? percent(s.cpu, 0) : undefined, num: true, col: 'cpu' },
    { key: 'privateBytes', label: 'Memory', total: s ? percent((100 * s.memoryUsed) / s.memoryTotal, 0) : undefined, num: true, col: 'mem' },
    { key: 'ioBytesPerSec', label: 'Disk', total: s ? rateShort(s.ioBytesPerSec) : undefined, num: true, col: 'io' },
    { key: 'threads', label: 'Threads', num: true, col: 'threads' },
  ]

  const sel = list.find((p) => p.pid === selected)

  return (
    <div className="page split">
      <div className="split-main proc-wrap">
        <div className="toolbar">
          <h1>Processes</h1>
          <div className="search-box">
            <span className="glyph">{'\uE721'}</span>
            <input placeholder="Search by name, PID or path" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
          <div className="seg">
            {(['apps', 'tree', 'list'] as View[]).map((v) => (
              <button key={v} className={view === v ? 'on' : ''} onClick={() => setView(v)}>
                {v === 'apps' ? 'Apps' : v === 'tree' ? 'Tree' : 'List'}
              </button>
            ))}
          </div>
          <label className="check">
            <input type="checkbox" checked={managedOnly} onChange={(e) => setManagedOnly(e.target.checked)} /> Managed only
          </label>
          <div className="spacer" />
          <button className="btn subtle" onClick={saveSnapshot} title="Save the process list as JSON">
            <span className="glyph">{'\uE74E'}</span> Snapshot
          </button>
        </div>
        {procs.error && <div className="banner danger">{procs.error}</div>}
        <div className="ptable">
          <div className="phead">
            {headers.map((h) => (
              <button
                key={h.label}
                className={`ph col-${h.col} ${h.num ? 'num' : ''} ${sort.key === h.key ? 'sorted' : ''}`}
                disabled={!h.key}
                onClick={() => h.key && setSort((x) => ({ key: h.key!, desc: x.key === h.key ? !x.desc : h.key !== 'name' }))}
              >
                {h.total && <span className="ph-total">{h.total}</span>}
                <span className="ph-label">
                  {h.label}
                  {sort.key === h.key && <span className="sort-glyph">{sort.desc ? '\uE70D' : '\uE70E'}</span>}
                </span>
              </button>
            ))}
          </div>
          <div className="pbody" ref={scroller}>
            <div style={{ height: virtual.getTotalSize(), position: 'relative' }}>
              {virtual.getVirtualItems().map((v) => {
                const l = lines[v.index]
                const p = l.p
                const icon = iconUrl(p.path)
                const isSel = selected === p.pid && l.count === 1
                return (
                  <div
                    key={l.key}
                    className={`prow ${isSel ? 'sel' : ''} ${p.protected ? 'dim' : ''}`}
                    style={{ transform: `translateY(${v.start}px)` }}
                    onClick={() => setSelected(p.pid)}
                    onDoubleClick={() => l.expandable && toggle(l)}
                    onContextMenu={(e) => openMenu(e, p)}
                  >
                    <div className="pc col-name" style={{ paddingLeft: 8 + l.depth * 20 }}>
                      <span
                        className="twisty"
                        onClick={(e) => {
                          e.stopPropagation()
                          if (l.expandable) toggle(l)
                        }}
                      >
                        {l.expandable ? (l.expanded ? '\uE70D' : '\uE76C') : ''}
                      </span>
                      {icon ? (
                        <img className="picon" src={icon} alt="" onError={(e) => ((e.target as HTMLImageElement).style.visibility = 'hidden')} />
                      ) : (
                        <span className="picon glyph">{'\uECAA'}</span>
                      )}
                      <span className="pname" title={`${p.name}${p.path ? `\n${p.path}` : ''}`}>
                        {label(p)}
                        {l.count > 1 && <span className="pcount"> ({l.count})</span>}
                      </span>
                      {l.managed && (
                        <span className="managed" title="Corewright manages this process">
                          {'\uE9F5'}
                        </span>
                      )}
                      {p.foreground && l.count === 1 && <span className="tag">foreground</span>}
                    </div>
                    <div className="pc num col-pid secondary">{l.count > 1 ? '' : p.pid}</div>
                    <div className="pc col-priority secondary">{l.priority === 'mixed' ? 'Mixed' : levelLabel(l.priority)}</div>
                    <div className="pc num col-cpu heat" style={heat(l.cpu / 25)}>
                      {l.cpu >= 0.05 ? percent(l.cpu) : '0%'}
                    </div>
                    <div className="pc num col-mem heat" style={heat(memTotal ? (l.privateBytes / memTotal) * 12 : 0)}>
                      {bytes(l.privateBytes)}
                    </div>
                    <div className="pc num col-io heat" style={heat(l.ioBytesPerSec / RATE_FULL)}>
                      {rateShort(l.ioBytesPerSec)}
                    </div>
                    <div className="pc num col-threads secondary">{l.threads}</div>
                  </div>
                )
              })}
            </div>
          </div>
        </div>
      </div>
      {sel && <ProcessPanel p={sel} title={label(sel)} onClose={() => setSelected(undefined)} onCreateRule={onCreateRule} />}
      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(undefined)}
          items={buildProcessMenu(menu.p, quickRule(menu.rules, menu.p.name), s?.cores.logicalProcessors ?? 0, s?.cores.hybrid ?? false, {
            setAlways: (patch) =>
              setQuick(menu.p, patch)
                .then(() => notify(`Saved. Applies to every ${menu.p.name} from now on.`))
                .catch((e) => notify(e instanceof Error ? e.message : String(e))),
            createRule: () => onCreateRule(ruleFor(menu.p)),
            showDetails: () => setSelected(menu.p.pid),
            reveal: isHost && menu.p.path ? () => post({ type: 'reveal', path: menu.p.path }) : undefined,
            copy: (text) => navigator.clipboard.writeText(text).then(() => notify('Copied')),
          })}
        />
      )}
      {toast && <div className="toast">{toast}</div>}
    </div>
  )
}

/** Task Manager style heat: the cell background gets stronger with the value (0..1). */
function heat(level: number): React.CSSProperties | undefined {
  const v = Math.max(0, Math.min(1, level))
  if (v < 0.02) return undefined
  return { background: `color-mix(in srgb, var(--heat) ${Math.round(12 + v * 58)}%, transparent)` }
}

function rateShort(n: number): string {
  if (n < 1024) return '0 MB/s'
  return `${(n / 1048576).toFixed(n < 10 * 1048576 ? 1 : 0)} MB/s`
}

function ProcessPanel({ p, title, onClose, onCreateRule }: { p: ProcessRow; title: string; onClose: () => void; onCreateRule: (r: Rule) => void }) {
  const explain = usePoll(() => api.explain(p.pid), 2000)
  const icon = iconUrl(p.path)
  return (
    <aside className="panel">
      <div className="panel-head">
        <div className="panel-title">
          {icon && <img src={icon} alt="" width={32} height={32} />}
          <div>
            <h2>{title}</h2>
            <div className="secondary tiny">
              {p.name} · PID {p.pid}
              {p.parentPid != null ? ` · parent ${p.parentPid}` : ''}
            </div>
          </div>
        </div>
        <button className="icon" onClick={onClose} aria-label="Close">
          {'\uE711'}
        </button>
      </div>
      {p.path && <div className="path">{p.path}</div>}
      {p.protected && <div className="banner warn">Protected system process. Corewright never changes it.</div>}

      <div className="kv">
        <span>Priority</span>
        <strong>{levelLabel(p.priority) || '–'}</strong>
        <span>CPU</span>
        <strong>{percent(p.cpu)}</strong>
        <span>Private memory</span>
        <strong>{bytes(p.privateBytes)}</strong>
        <span>Working set</span>
        <strong>{bytes(p.workingSet)}</strong>
        <span>Threads · handles</span>
        <strong>
          {p.threads} · {p.handles}
        </strong>
      </div>

      <h3>Applied by Corewright</h3>
      {p.applied?.length ? (
        <div className="kv">
          {p.applied.map((a) => (
            <AppliedRow key={a.slot} slot={a.slot} value={levelLabel(a.value) || a.value} rule={a.ruleName} />
          ))}
        </div>
      ) : (
        <div className="secondary">Nothing. The process runs with its own settings.</div>
      )}

      <h3>Rule evaluation</h3>
      {explain.data?.rules.length ? (
        <ul className="explain">
          {explain.data.rules.map((r) => (
            <li key={r.ruleId} className={r.active ? 'on' : ''}>
              <div>
                <span className={`dot ${r.active ? 'teal' : r.matches ? 'warn' : ''}`} />
                {r.ruleName}
              </div>
              {r.won.length > 0 && <div className="tiny">sets {r.won.join(', ')}</div>}
              {r.lost.length > 0 && <div className="tiny secondary">overruled: {r.lost.join(', ')}</div>}
              {r.matches && !r.active && <div className="tiny secondary">matches, waiting for “apply after”</div>}
              {!r.matches && <div className="tiny secondary">does not match</div>}
            </li>
          ))}
        </ul>
      ) : (
        <div className="secondary">No enabled rules.</div>
      )}

      {!p.protected && (
        <button className="btn primary wide" onClick={() => onCreateRule(ruleFor(p))}>
          Create a rule for {p.name}
        </button>
      )}
    </aside>
  )
}

function AppliedRow({ slot, value, rule }: { slot: string; value: string; rule: string }) {
  return (
    <>
      <span title={`Set by ${rule}`}>{slot}</span>
      <strong>{value}</strong>
    </>
  )
}

function ruleFor(p: ProcessRow): Rule {
  return {
    id: newId(),
    name: `Rule for ${p.name}`,
    enabled: true,
    priority: 50,
    sustainSeconds: 0,
    releaseSeconds: 0,
    allowRealtime: false,
    when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'eq', value: p.name }] },
    actions: { cpuPriority: 'aboveNormal' },
  }
}
