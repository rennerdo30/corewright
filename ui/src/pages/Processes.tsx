import { useVirtualizer } from '@tanstack/react-virtual'
import { useMemo, useRef, useState } from 'react'
import { api } from '../api'
import { ContextMenu, type MenuEntry } from '../components/ContextMenu'
import { Dot, PageHeader, joinAnd, plural } from '../components/PageHeader'
import { buildProcessMenu } from '../components/processMenu'
import { bytes, newId, percent } from '../format'
import { iconUrl, isHost, levelLabel, post, useDescriptions } from '../host'
import { usePoll } from '../hooks'
import { quickRule, setQuick } from '../quick'
import { isAutopilot, ownerOf } from '../roles'
import type { AppSettings, ExplainRule, ProcessRow, Rule, RuleSet, Topology } from '../types'
import { COLUMN_GROUPS, COLUMNS, type ColumnDef } from './processColumns'

type View = 'apps' | 'tree' | 'list'

/** One visible line: a single process, or an app group (all processes with the same exe name). */
interface Line {
  key: string
  items: ProcessRow[]
  p: ProcessRow // first process: used for name, icon, menus and details
  depth: number
  expandable: boolean
  expanded: boolean
}

const WIDTHS_KEY = 'corewright.processColumns.widths'
const VISIBLE_KEY = 'corewright.processColumns.visible'
const DEFAULT_VISIBLE = COLUMNS.filter((c) => c.visible).map((c) => c.id)

function load<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key)
    return raw ? (JSON.parse(raw) as T) : fallback
  } catch {
    return fallback
  }
}

/** Value of a column for a line: one process, or the group aggregate (sum / max / same-or-Mixed). */
function cellValue(c: ColumnDef, l: Line): number | string | boolean | undefined {
  if (l.items.length === 1) return c.value(l.p)
  const values = l.items.map(c.value)
  if (c.agg === 'sum') return values.reduce<number>((s, v) => s + (typeof v === 'number' ? v : 0), 0)
  if (c.agg === 'max') return values.reduce<number>((s, v) => Math.max(s, typeof v === 'number' ? v : 0), 0)
  const first = values[0]
  return values.every((v) => v === first) ? first : 'mixed'
}

export function Processes({ onCreateRule }: { onCreateRule: (r: Rule) => void }) {
  const [visible, setVisible] = useState<string[]>(() => load(VISIBLE_KEY, DEFAULT_VISIBLE))
  const [widths, setWidths] = useState<Record<string, number>>(() => load(WIDTHS_KEY, {}))
  const columns = COLUMNS.filter((c) => c.id === 'name' || visible.includes(c.id))
  // Only ask the service for what visible columns need (handle reads cost CPU).
  const include = [...new Set(columns.map((c) => c.needs).filter(Boolean))].sort().join(',')

  const procs = usePoll(() => api.processes(include), 1000)
  const sys = usePoll(api.system, 2000)
  const topo = usePoll(api.topology, 600_000)
  const [search, setSearch] = useState('')
  const [view, setView] = useState<View>('apps')
  const [managedOnly, setManagedOnly] = useState(false)
  const [sort, setSort] = useState<{ id: string; desc: boolean }>({ id: 'cpu', desc: true })
  const [toggled, setToggled] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<number>()
  const [menu, setMenu] = useState<{ x: number; y: number; p: ProcessRow; rules?: RuleSet; settings?: AppSettings }>()
  const [columnMenu, setColumnMenu] = useState<{ x: number; y: number }>()
  const [toast, setToast] = useState<string>()

  const list = procs.data ?? []
  const descriptions = useDescriptions(list.map((p) => p.path))
  const label = (p: ProcessRow) => (p.path && descriptions.get(p.path)) || p.name
  const width = (c: ColumnDef) => widths[c.id] ?? c.width

  const lines = useMemo<Line[]>(() => {
    const q = search.trim().toLowerCase()
    const shown = list.filter(
      (p) =>
        (!managedOnly || p.applied?.length) &&
        (!q || p.name.toLowerCase().includes(q) || String(p.pid) === q || p.path?.toLowerCase().includes(q) || label(p).toLowerCase().includes(q)),
    )
    const sortCol = COLUMNS.find((c) => c.id === sort.id) ?? COLUMNS[0]
    const cmp = (a: Line, b: Line) => {
      let r: number
      if (sortCol.id === 'name') r = label(a.p).localeCompare(label(b.p))
      else {
        const va = cellValue(sortCol, a)
        const vb = cellValue(sortCol, b)
        r = typeof va === 'number' && typeof vb === 'number' ? va - vb : String(va ?? '').localeCompare(String(vb ?? ''))
      }
      return sort.desc ? -r : r
    }
    const one = (p: ProcessRow, depth: number, expandable = false, expanded = false): Line => ({
      key: `p${p.pid}`,
      items: [p],
      p,
      depth,
      expandable,
      expanded,
    })

    if (view === 'list') return shown.map((p) => one(p, 0)).sort(cmp)

    if (view === 'apps') {
      // Like Task Manager: processes with the same exe form one app line with a count; groups start collapsed.
      const groups = new Map<string, ProcessRow[]>()
      for (const p of shown) {
        const k = p.name.toLowerCase()
        const g = groups.get(k)
        if (g) g.push(p)
        else groups.set(k, [p])
      }
      const heads = [...groups.entries()].map(([k, ps]): Line =>
        ps.length === 1 ? one(ps[0], 0) : { key: `g${k}`, items: ps, p: ps[0], depth: 0, expandable: true, expanded: toggled.has(k) },
      )
      const out: Line[] = []
      for (const h of heads.sort(cmp)) {
        out.push(h)
        if (h.items.length > 1 && h.expanded) out.push(...h.items.map((p) => one(p, 1)).sort(cmp))
      }
      return out
    }

    // Tree by parent process; starts expanded.
    const pids = new Set(list.map((p) => p.pid))
    const byParent = new Map<number, ProcessRow[]>()
    const roots: ProcessRow[] = []
    for (const p of shown) {
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
  const headRef = useRef<HTMLDivElement>(null)
  const virtual = useVirtualizer({ count: lines.length, getScrollElement: () => scroller.current, estimateSize: () => 30, overscan: 16 })

  const toggle = (l: Line) =>
    setToggled((s) => {
      const k = l.items.length > 1 ? l.p.name.toLowerCase() : l.key
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
    // Autopilot's game lists, for the Game submenu.
    api.settings().then((settings) => setMenu((m) => (m && m.p.pid === p.pid ? { ...m, settings } : m))).catch(() => {})
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

  // ---- Columns: visibility (chooser) and widths (drag the header edge; double-click resets) ----

  const saveVisible = (next: string[]) => {
    setVisible(next)
    localStorage.setItem(VISIBLE_KEY, JSON.stringify(next))
  }

  const columnItems: MenuEntry[] = [
    ...COLUMN_GROUPS.flatMap((g): MenuEntry[] => [
      { type: 'head', label: g },
      ...COLUMNS.filter((c) => c.group === g && c.id !== 'name').map(
        (c): MenuEntry => ({
          type: 'item',
          label: c.label,
          checked: visible.includes(c.id),
          onClick: () => saveVisible(visible.includes(c.id) ? visible.filter((v) => v !== c.id) : [...visible, c.id]),
        }),
      ),
    ]),
    { type: 'sep' },
    { type: 'item', label: 'Reset columns', glyph: '\uE777', onClick: () => { saveVisible(DEFAULT_VISIBLE); setWidths({}); localStorage.removeItem(WIDTHS_KEY) } },
  ]

  const startResize = (c: ColumnDef, e: React.PointerEvent) => {
    e.preventDefault()
    e.stopPropagation()
    const startX = e.clientX
    const startW = width(c)
    const min = c.id === 'name' ? 160 : 48
    document.body.classList.add('resizing')
    const move = (ev: PointerEvent) => setWidths((w) => ({ ...w, [c.id]: Math.max(min, Math.round(startW + ev.clientX - startX)) }))
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      document.body.classList.remove('resizing')
      setWidths((w) => {
        localStorage.setItem(WIDTHS_KEY, JSON.stringify(w))
        return w
      })
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  const resetWidth = (c: ColumnDef) =>
    setWidths((w) => {
      const next = { ...w }
      delete next[c.id]
      localStorage.setItem(WIDTHS_KEY, JSON.stringify(next))
      return next
    })

  const template = columns.map((c) => `${width(c)}px`).join(' ') + ' minmax(0, 1fr)'
  const tableWidth = columns.reduce((s, c) => s + width(c), 0)
  const s = sys.data
  const sel = list.find((p) => p.pid === selected)

  return (
    <div className="page split dense">
      <div className="split-main proc-wrap">
        <PageHeader title="Processes">
          <ProcessSummary rows={procs.data} />
        </PageHeader>
        <div className="toolbar">
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
          <button className="btn subtle" onClick={(e) => setColumnMenu({ x: e.clientX, y: e.clientY })} title="Choose columns (or right-click a column header)">
            <span className="glyph">{'\uE8A4'}</span> Columns
          </button>
          <button className="btn subtle" onClick={saveSnapshot} title="Save the process list as JSON">
            <span className="glyph">{'\uE74E'}</span> Snapshot
          </button>
        </div>
        {procs.error && <div className="banner danger">{procs.error}</div>}
        <div className="ptable" style={{ '--cols': template, '--table-w': `${tableWidth}px` } as React.CSSProperties}>
          <div
            className="phead"
            ref={headRef}
            onContextMenu={(e) => {
              e.preventDefault()
              setColumnMenu({ x: e.clientX, y: e.clientY })
            }}
          >
            <div className="phead-inner">
              {columns.map((c) => (
                <div key={c.id} className="ph-cell">
                  <button
                    className={`ph ${c.num ? 'num' : ''} ${sort.id === c.id ? 'sorted' : ''}`}
                    title={c.label}
                    onClick={() => setSort((x) => ({ id: c.id, desc: x.id === c.id ? !x.desc : !!c.num }))}
                  >
                    {c.total && s && <span className="ph-total">{c.total(s)}</span>}
                    <span className="ph-label">
                      {c.label}
                      {sort.id === c.id && <span className="sort-glyph">{sort.desc ? '\uE70D' : '\uE70E'}</span>}
                    </span>
                  </button>
                  <span className="col-resize" title="Drag to resize, double-click to reset" onPointerDown={(e) => startResize(c, e)} onDoubleClick={() => resetWidth(c)} />
                </div>
              ))}
            </div>
          </div>
          <div className="pbody" ref={scroller} onScroll={(e) => headRef.current && (headRef.current.scrollLeft = e.currentTarget.scrollLeft)}>
            <div className="pbody-inner" style={{ height: virtual.getTotalSize() }}>
              {virtual.getVirtualItems().map((v) => {
                const l = lines[v.index]
                const p = l.p
                const group = l.items.length > 1
                const icon = iconUrl(p.path)
                return (
                  <div
                    key={l.key}
                    className={`prow ${v.index % 2 ? 'odd' : ''} ${l.depth > 0 && view === 'apps' ? 'child' : ''} ${selected === p.pid && !group ? 'sel' : ''} ${p.protected ? 'dim' : ''}`}
                    style={{ transform: `translateY(${v.start}px)` }}
                    onClick={() => setSelected(p.pid)}
                    onDoubleClick={() => l.expandable && toggle(l)}
                    onContextMenu={(e) => openMenu(e, p)}
                  >
                    {columns.map((c) => {
                      if (c.id === 'name')
                        return (
                          <div key={c.id} className="pc col-name" style={{ paddingLeft: 6 + l.depth * 20 }}>
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
                              {group && <span className="pcount"> ({l.items.length})</span>}
                            </span>
                            {(() => {
                              const a = l.items.flatMap((x) => x.applied ?? [])
                              if (!a.length) return null
                              return (
                                <span className={`managed role-${ownerOf(a[0].ruleId)}`} title={`Managed by ${[...new Set(a.map((x) => x.ruleName))].join(', ')}`}>
                                  {'\uE9F5'}
                                </span>
                              )
                            })()}
                            {p.foreground && !group && <span className="tag">foreground</span>}
                          </div>
                        )
                      const raw = c.id === 'pid' && group ? undefined : cellValue(c, l)
                      const display = raw === 'mixed' ? 'Mixed' : c.format ? c.format(raw) : raw === undefined ? '' : String(raw)
                      const managedSlot = c.slot ? l.items.flatMap((x) => x.applied ?? []).find((a) => a.slot === c.slot) : undefined
                      const level = c.heat && typeof raw === 'number' ? c.heat(raw, s) : 0
                      // Defaults and zeros recede; real values and rule-set values stand out.
                      const quiet = !managedSlot && (raw === undefined || raw === '' || raw === 0 || (typeof raw === 'number' && parseFloat(display) === 0) || (c.defaultValue !== undefined && raw === c.defaultValue))
                      return (
                        <div
                          key={c.id}
                          className={`pc ${c.num ? 'num' : ''} ${c.heat ? 'heat' : ''} ${quiet ? 'quiet' : ''} ${managedSlot ? `set-by-rule role-${ownerOf(managedSlot.ruleId)}` : ''}`}
                          style={heat(level)}
                          title={managedSlot ? `${display}, set by ${managedSlot.ruleName}` : display}
                        >
                          {display}
                        </div>
                      )
                    })}
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
          items={buildProcessMenu(menu.p, quickRule(menu.rules, menu.p.name), topo.data, s?.cores.hybrid ?? false, {
            setAlways: (patch) =>
              setQuick(menu.p, patch)
                .then(() => notify(`Saved. Applies to every ${menu.p.name} from now on.`))
                .catch((e) => notify(e instanceof Error ? e.message : String(e))),
            createRule: () => onCreateRule(ruleFor(menu.p)),
            showDetails: () => setSelected(menu.p.pid),
            reveal: isHost && menu.p.path ? () => post({ type: 'reveal', path: menu.p.path }) : undefined,
            copy: (text) => navigator.clipboard.writeText(text).then(() => notify('Copied')),
            gameKind: menu.settings ? gameKindOf(menu.settings, menu.p.name) : undefined,
            setGame: menu.settings
              ? (kind) =>
                  setGameKind(menu.settings!, menu.p.name, kind)
                    .then(() => notify(kind === 'always' ? `${menu.p.name} always counts as a game.` : kind === 'never' ? `${menu.p.name} never counts as a game.` : `Autopilot decides whether ${menu.p.name} is a game.`))
                    .catch((e) => notify(e instanceof Error ? e.message : String(e)))
              : undefined,
          })}
        />
      )}
      {columnMenu && <ContextMenu x={columnMenu.x} y={columnMenu.y} onClose={() => setColumnMenu(undefined)} items={columnItems} keepOpen />}
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

      <RuleEvaluation rules={explain.data?.rules} />

      {!p.protected && (
        <button className="btn primary wide" onClick={() => onCreateRule(ruleFor(p))}>
          Create a rule for {p.name}
        </button>
      )}
    </aside>
  )
}

/** Rules that set something on this process first, then matching-but-overruled ones, the rest collapsed. */
function RuleEvaluation({ rules }: { rules?: ExplainRule[] }) {
  const [showOthers, setShowOthers] = useState(false)
  if (!rules?.length) return <><h3>Rules</h3><div className="secondary">No enabled rules.</div></>
  const applied = rules.filter((r) => r.active && r.won.length > 0)
  const overruled = rules.filter((r) => r.matches && !(r.active && r.won.length > 0))
  const others = rules.filter((r) => !r.matches)
  return (
    <>
      <h3>Applied rules</h3>
      {applied.length ? (
        <ul className="explain">
          {applied.map((r) => (
            <li key={r.ruleId} className="on">
              <div>
                <span className="dot teal" />
                <strong>{r.ruleName}</strong>
              </div>
              <div className="tiny">sets {r.won.join(', ')}</div>
              {r.lost.length > 0 && <div className="tiny secondary">overruled by a higher rule: {r.lost.join(', ')}</div>}
            </li>
          ))}
        </ul>
      ) : (
        <div className="secondary">No rule changes this process.</div>
      )}
      {overruled.length > 0 && (
        <>
          <h3>Matching, but not applied</h3>
          <ul className="explain">
            {overruled.map((r) => (
              <li key={r.ruleId}>
                <div>
                  <span className="dot warn" />
                  {r.ruleName}
                </div>
                <div className="tiny secondary">
                  {!r.active ? 'waiting for “apply after”' : `everything it sets is owned by a higher rule (${r.lost.join(', ')})`}
                </div>
              </li>
            ))}
          </ul>
        </>
      )}
      {others.length > 0 && (
        <button className="link others-toggle" onClick={() => setShowOthers((v) => !v)}>
          {showOthers ? 'Hide' : 'Show'} {others.length} other rule{others.length > 1 ? 's' : ''} that don't apply
        </button>
      )}
      {showOthers && (
        <ul className="explain others">
          {others.map((r) => (
            <li key={r.ruleId}>
              <span className="dot" />
              {r.ruleName}
            </li>
          ))}
        </ul>
      )}
    </>
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

export type { Topology }

/** "312 processes. Your rules manage 12 of them and Autopilot 4. You are using Code." */
function ProcessSummary({ rows }: { rows?: ProcessRow[] }) {
  if (!rows) return <>Reading processes…</>
  let byRules = 0
  let byAutopilot = 0
  for (const p of rows) {
    const owners = (p.applied ?? []).map((a) => ownerOf(a.ruleId))
    if (owners.some((o) => !isAutopilot(o))) byRules++
    else if (owners.length) byAutopilot++
  }
  const fg = rows.find((p) => p.foreground)
  const parts: React.ReactNode[] = []
  if (byRules) parts.push(<Dot key="r" role="rule">your rules manage {byRules}</Dot>)
  if (byAutopilot) parts.push(<Dot key="a" role="background">Autopilot {byAutopilot}</Dot>)
  return (
    <>
      {plural(rows.length, 'process', 'processes')} running.{' '}
      {parts.length ? <>Of these, {joinAnd(parts)}. </> : <>None of them is managed right now. </>}
      {fg ? (
        <>
          In front: <strong>{fg.name.replace(/\.exe$/i, '')}</strong>.
        </>
      ) : null}
    </>
  )
}

const sameName = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

/** Where a program stands in Autopilot's game lists. */
function gameKindOf(s: AppSettings, name: string): 'always' | 'never' | 'auto' {
  const g = s.autopilot.gameMode
  if (g.notGames.some((n) => sameName(n, name))) return 'never'
  if (g.games.some((n) => sameName(n, name))) return 'always'
  return 'auto'
}

/** Moves a program between "Always games", "Never games" and neither, and saves the settings. */
async function setGameKind(s: AppSettings, name: string, kind: 'always' | 'never' | 'auto') {
  const next = structuredClone(s)
  const g = next.autopilot.gameMode
  g.games = g.games.filter((n) => !sameName(n, name))
  g.notGames = g.notGames.filter((n) => !sameName(n, name))
  if (kind === 'always') g.games.push(name)
  if (kind === 'never') g.notGames.push(name)
  await api.saveSettings(next)
}
