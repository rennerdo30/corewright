import { useVirtualizer } from '@tanstack/react-virtual'
import { useMemo, useRef, useState } from 'react'
import { api } from '../api'
import { bytes, newId, percent, rate } from '../format'
import { usePoll } from '../hooks'
import { ContextMenu } from '../components/ContextMenu'
import { buildProcessMenu } from '../components/processMenu'
import { quickRule, setQuick } from '../quick'
import type { ProcessRow, Rule, RuleSet } from '../types'

type SortKey = 'name' | 'pid' | 'cpu' | 'privateBytes' | 'workingSet' | 'ioBytesPerSec' | 'threads'

const COLUMNS: { key: SortKey; label: string; num?: boolean }[] = [
  { key: 'name', label: 'Name' },
  { key: 'pid', label: 'PID', num: true },
  { key: 'cpu', label: 'CPU', num: true },
  { key: 'privateBytes', label: 'Private', num: true },
  { key: 'workingSet', label: 'Working set', num: true },
  { key: 'ioBytesPerSec', label: 'I/O', num: true },
  { key: 'threads', label: 'Threads', num: true },
]

interface Row {
  p: ProcessRow
  depth: number
  hasChildren: boolean
}

export function Processes({ onCreateRule }: { onCreateRule: (r: Rule) => void }) {
  const procs = usePoll(api.processes, 1000)
  const [search, setSearch] = useState('')
  const [tree, setTree] = useState(true)
  const [managedOnly, setManagedOnly] = useState(false)
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'cpu', desc: true })
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set())
  const [selected, setSelected] = useState<number>()
  const [menu, setMenu] = useState<{ x: number; y: number; p: ProcessRow; rules?: RuleSet }>()
  const [toast, setToast] = useState<string>()
  const sys = usePoll(api.system, 60_000)
  const canReveal = !!window.chrome?.webview

  const openMenu = async (e: React.MouseEvent, p: ProcessRow) => {
    e.preventDefault()
    setSelected(p.pid)
    const at = { x: e.clientX, y: e.clientY, p }
    setMenu(at)
    try {
      const rules = await api.rules()
      setMenu((m) => (m && m.p.pid === p.pid ? { ...m, rules } : m))
    } catch {
      /* menu still works, without check marks */
    }
  }

  const notify = (text: string) => {
    setToast(text)
    window.setTimeout(() => setToast(undefined), 2500)
  }

  const rows = useMemo<Row[]>(() => {
    const list = procs.data ?? []
    const cmp = (a: ProcessRow, b: ProcessRow) => {
      const k = sort.key
      const r = k === 'name' ? a.name.localeCompare(b.name) : (a[k] as number) - (b[k] as number)
      return sort.desc ? -r : r
    }
    const q = search.trim().toLowerCase()
    const filtered = list.filter(
      (p) =>
        (!managedOnly || p.applied?.length) &&
        (!q || p.name.toLowerCase().includes(q) || String(p.pid) === q || p.path?.toLowerCase().includes(q)),
    )
    if (!tree || q || managedOnly) return filtered.sort(cmp).map((p) => ({ p, depth: 0, hasChildren: false }))

    const byParent = new Map<number, ProcessRow[]>()
    const pids = new Set(list.map((p) => p.pid))
    const roots: ProcessRow[] = []
    for (const p of list) {
      if (p.parentPid != null && pids.has(p.parentPid) && p.parentPid !== p.pid) {
        const arr = byParent.get(p.parentPid) ?? []
        arr.push(p)
        byParent.set(p.parentPid, arr)
      } else roots.push(p)
    }
    const out: Row[] = []
    const walk = (p: ProcessRow, depth: number) => {
      const kids = byParent.get(p.pid)
      out.push({ p, depth, hasChildren: !!kids?.length })
      if (kids && !collapsed.has(p.pid)) for (const k of kids.sort(cmp)) walk(k, depth + 1)
    }
    for (const r of roots.sort(cmp)) walk(r, 0)
    return out
  }, [procs.data, search, tree, managedOnly, sort, collapsed])

  const scroller = useRef<HTMLDivElement>(null)
  const virtual = useVirtualizer({ count: rows.length, getScrollElement: () => scroller.current, estimateSize: () => 30, overscan: 12 })

  const toggle = (pid: number) =>
    setCollapsed((s) => {
      const n = new Set(s)
      if (n.has(pid)) n.delete(pid)
      else n.add(pid)
      return n
    })

  const saveSnapshot = () => {
    const blob = new Blob([JSON.stringify({ takenAt: new Date().toISOString(), processes: procs.data ?? [] }, null, 2)], {
      type: 'application/json',
    })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `corewright-processes-${new Date().toISOString().replace(/[:.]/g, '-')}.json`
    a.click()
    URL.revokeObjectURL(a.href)
  }

  const sel = procs.data?.find((p) => p.pid === selected)

  return (
    <div className="page split">
      <div className="split-main">
        <div className="toolbar">
          <h1>Processes</h1>
          <input className="search" placeholder="Filter by name, PID or path" value={search} onChange={(e) => setSearch(e.target.value)} />
          <div className="seg">
            <button className={tree ? 'on' : ''} onClick={() => setTree(true)}>
              Tree
            </button>
            <button className={!tree ? 'on' : ''} onClick={() => setTree(false)}>
              Flat
            </button>
          </div>
          <label className="check">
            <input type="checkbox" checked={managedOnly} onChange={(e) => setManagedOnly(e.target.checked)} /> Managed only
          </label>
          <button className="btn" onClick={saveSnapshot} title="Download the current process tree as JSON">
            Save snapshot
          </button>
        </div>
        {procs.error && <div className="banner danger">{procs.error}</div>}
        <div className="table">
          <div className="thead">
            {COLUMNS.map((c) => (
              <button
                key={c.key}
                className={`th ${c.num ? 'num' : ''} col-${c.key}`}
                onClick={() => setSort((s) => ({ key: c.key, desc: s.key === c.key ? !s.desc : c.key !== 'name' }))}
              >
                {c.label}
                {sort.key === c.key ? (sort.desc ? ' ↓' : ' ↑') : ''}
              </button>
            ))}
            <div className="th col-priority">Priority</div>
            <div className="th col-rules">Rules</div>
          </div>
          <div className="tbody" ref={scroller}>
            <div style={{ height: virtual.getTotalSize(), position: 'relative' }}>
              {virtual.getVirtualItems().map((v) => {
                const { p, depth, hasChildren } = rows[v.index]
                return (
                  <div
                    key={p.pid}
                    className={`tr ${selected === p.pid ? 'sel' : ''} ${p.protected ? 'dim' : ''}`}
                    style={{ transform: `translateY(${v.start}px)` }}
                    onClick={() => setSelected(p.pid)}
                    onContextMenu={(e) => openMenu(e, p)}
                  >
                    <div className="td col-name" style={{ paddingLeft: 10 + depth * 16 }}>
                      {hasChildren ? (
                        <span
                          className="twisty"
                          onClick={(e) => {
                            e.stopPropagation()
                            toggle(p.pid)
                          }}
                        >
                          {collapsed.has(p.pid) ? '\uE76C' : '\uE70D'}
                        </span>
                      ) : (
                        <span className="twisty" />
                      )}
                      <span className="pname" title={p.path}>
                        {p.name}
                      </span>
                      {p.foreground && <span className="tag teal">fg</span>}
                    </div>
                    <div className="td num col-pid">{p.pid}</div>
                    <div className={`td num col-cpu ${p.cpu > 10 ? 'hot' : ''}`}>{p.cpu >= 0.05 ? percent(p.cpu) : ''}</div>
                    <div className="td num col-privateBytes">{bytes(p.privateBytes)}</div>
                    <div className="td num col-workingSet">{bytes(p.workingSet)}</div>
                    <div className="td num col-ioBytesPerSec">{rate(p.ioBytesPerSec)}</div>
                    <div className="td num col-threads">{p.threads}</div>
                    <div className="td col-priority">{p.priority}</div>
                    <div className="td col-rules">
                      {p.applied?.length ? <span className="tag indigo">{new Set(p.applied.map((a) => a.ruleName)).size} rule</span> : null}
                    </div>
                  </div>
                )
              })}
            </div>
          </div>
        </div>
      </div>
      {sel && <ProcessPanel p={sel} onClose={() => setSelected(undefined)} onCreateRule={onCreateRule} />}
      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(undefined)}
          items={buildProcessMenu(menu.p, quickRule(menu.rules, menu.p.name), sys.data?.cores.logicalProcessors ?? 0, sys.data?.cores.hybrid ?? false, {
            setAlways: (patch) =>
              setQuick(menu.p, patch)
                .then(() => notify(`Saved for ${menu.p.name}. Applies to every ${menu.p.name} from now on.`))
                .catch((e) => notify(e instanceof Error ? e.message : String(e))),
            createRule: () => onCreateRule(ruleFor(menu.p)),
            showDetails: () => setSelected(menu.p.pid),
            reveal: canReveal && menu.p.path ? () => window.chrome?.webview?.postMessage({ type: 'reveal', path: menu.p.path }) : undefined,
            copy: (text) => navigator.clipboard.writeText(text).then(() => notify('Copied')),
          })}
        />
      )}
      {toast && <div className="toast">{toast}</div>}
    </div>
  )
}

function ProcessPanel({ p, onClose, onCreateRule }: { p: ProcessRow; onClose: () => void; onCreateRule: (r: Rule) => void }) {
  const explain = usePoll(() => api.explain(p.pid), 2000)

  const createRule = () => onCreateRule(ruleFor(p))

  return (
    <aside className="panel">
      <div className="panel-head">
        <div>
          <h2>{p.name}</h2>
          <div className="muted tiny">PID {p.pid}{p.parentPid != null ? ` · parent ${p.parentPid}` : ''} · session {p.session}</div>
        </div>
        <button className="icon" onClick={onClose} aria-label="Close">
          {'\uE711'}
        </button>
      </div>
      {p.path && <div className="path mono">{p.path}</div>}
      {p.protected && <div className="banner warn">Protected system process: Corewright never changes it.</div>}

      <h3>Applied by Corewright</h3>
      {p.applied?.length ? (
        <table className="mini">
          <tbody>
            {p.applied.map((a) => (
              <tr key={a.slot}>
                <td className="muted">{a.slot}</td>
                <td className="mono">{a.value}</td>
                <td className="muted tiny">{a.ruleName}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <div className="muted">Nothing. The process runs with its own settings.</div>
      )}

      <h3>Why (rule evaluation)</h3>
      {explain.data?.rules.length ? (
        <ul className="explain">
          {explain.data.rules.map((r) => (
            <li key={r.ruleId} className={r.active ? 'on' : ''}>
              <div>
                <span className={`dot ${r.active ? 'teal' : r.matches ? 'warn' : ''}`} />
                {r.ruleName} <span className="muted tiny">prio {r.priority}</span>
              </div>
              {r.won.length > 0 && <div className="tiny">wins: {r.won.join(', ')}</div>}
              {r.lost.length > 0 && <div className="tiny muted">loses: {r.lost.join(', ')}</div>}
              {r.matches && !r.active && <div className="tiny muted">matches, waiting for sustain time</div>}
            </li>
          ))}
        </ul>
      ) : (
        <div className="muted">No enabled rules.</div>
      )}

      {!p.protected && (
        <button className="btn primary wide" onClick={createRule}>
          Create a rule for {p.name}
        </button>
      )}
    </aside>
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
