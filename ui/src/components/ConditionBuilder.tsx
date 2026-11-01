import { useState } from 'react'
import type { Condition } from '../types'

type Kind = 'text' | 'number' | 'bool' | 'fact'

export const FIELDS: { group: string; items: { id: string; label: string; kind: Kind; unit?: string }[] }[] = [
  {
    group: 'Process',
    items: [
      { id: 'name', label: 'Process name', kind: 'text' },
      { id: 'path', label: 'File path', kind: 'text' },
      { id: 'parentName', label: 'Parent process', kind: 'text' },
      { id: 'commandLine', label: 'Command line', kind: 'text' },
      { id: 'pid', label: 'PID', kind: 'number' },
      { id: 'session', label: 'Session', kind: 'number' },
      { id: 'program', label: 'Program (what it runs)', kind: 'text' },
      { id: 'latencyCritical', label: 'Latency critical (audio, input, drivers)', kind: 'bool' },
    ],
  },
  {
    group: 'Live usage',
    items: [
      { id: 'cpu', label: 'CPU usage', kind: 'number', unit: '%' },
      { id: 'privateMb', label: 'Private memory', kind: 'number', unit: 'MB' },
      { id: 'workingSetMb', label: 'Working set', kind: 'number', unit: 'MB' },
      { id: 'ioMBps', label: 'Disk I/O', kind: 'number', unit: 'MB/s' },
      { id: 'threads', label: 'Threads', kind: 'number' },
      { id: 'handles', label: 'Handles', kind: 'number' },
      { id: 'isForeground', label: 'In the foreground', kind: 'bool' },
      { id: 'isForegroundApp', label: 'Part of the app in front', kind: 'bool' },
    ],
  },
  { group: 'Integrations', items: [{ id: 'fact', label: 'Fact from an app', kind: 'fact' }] },
]

const OPS: Record<Kind, { id: string; label: string }[]> = {
  text: [
    { id: 'eq', label: 'is' },
    { id: 'in', label: 'is one of' },
    { id: 'ne', label: 'is not' },
    { id: 'contains', label: 'contains' },
    { id: 'startsWith', label: 'starts with' },
    { id: 'endsWith', label: 'ends with' },
    { id: 'glob', label: 'matches pattern' },
    { id: 'regex', label: 'matches regex' },
  ],
  number: [
    { id: 'gt', label: 'is above' },
    { id: 'gte', label: 'is at least' },
    { id: 'lt', label: 'is below' },
    { id: 'lte', label: 'is at most' },
    { id: 'eq', label: 'equals' },
    { id: 'ne', label: 'is not' },
  ],
  bool: [
    { id: 'isTrue', label: 'yes' },
    { id: 'isFalse', label: 'no' },
  ],
  fact: [
    { id: 'isTrue', label: 'is true' },
    { id: 'isFalse', label: 'is false or missing' },
    { id: 'eq', label: 'is' },
    { id: 'ne', label: 'is not' },
    { id: 'contains', label: 'contains' },
    { id: 'gt', label: 'is above' },
    { id: 'lt', label: 'is below' },
    { id: 'exists', label: 'exists' },
    { id: 'notExists', label: 'does not exist' },
  ],
}

const NO_VALUE = new Set(['isTrue', 'isFalse', 'exists', 'notExists'])

const fieldOf = (id?: string) => FIELDS.flatMap((g) => g.items).find((f) => f.id === id) ?? FIELDS[0].items[0]
const opLabel = (kind: Kind, op?: string) => OPS[kind].find((o) => o.id === op)?.label ?? op ?? ''

/** Plain-language description of a condition tree: "process name is one of cl.exe, link.exe and not in the foreground". */
export function describeCondition(c: Condition): string {
  if (c.type === 'group') {
    const kids = c.children ?? []
    if (kids.length === 0) return c.negate ? 'never' : 'any process'
    const text = kids.map(describeCondition).join(c.combinator === 'or' ? ' or ' : ' and ')
    return c.negate ? `not (${text})` : kids.length > 1 && c.combinator === 'or' ? `(${text})` : text
  }
  const f = fieldOf(c.field)
  let s: string
  if (f.kind === 'bool') s = c.op === 'isFalse' ? `not ${f.label.toLowerCase()}` : f.label.toLowerCase()
  else if (f.kind === 'fact') s = `fact “${c.key ?? ''}” ${opLabel('fact', c.op)}${NO_VALUE.has(c.op ?? '') ? '' : ` ${c.value ?? ''}`}`
  else s = `${f.label.toLowerCase()} ${opLabel(f.kind, c.op)} ${c.op === 'in' ? listOf(c.value) : (c.value ?? '')}${f.unit ? ` ${f.unit}` : ''}`
  return c.negate ? `not ${s}` : s
}

const listOf = (v?: string) => {
  const items = splitList(v)
  return items.length > 3 ? `${items.slice(0, 3).join(', ')} and ${items.length - 3} more` : items.join(', ')
}

const splitList = (v?: string) => (v ?? '').split(/[,;]/).map((s) => s.trim()).filter(Boolean)

export function ConditionGroup(props: { node: Condition; onChange: (c: Condition) => void; onRemove?: () => void; root?: boolean }) {
  const { node, onChange } = props
  const children = node.children ?? []
  const setChildren = (c: Condition[]) => onChange({ ...node, children: c })
  const mode = node.negate ? 'none' : node.combinator === 'or' ? 'any' : 'all'

  return (
    <div className={`cgroup ${props.root ? 'root' : ''}`}>
      <div className="cgroup-head">
        <span>Processes that match</span>
        <select
          className="inline-select"
          value={mode}
          onChange={(e) => onChange({ ...node, combinator: e.target.value === 'any' ? 'or' : 'and', negate: e.target.value === 'none' })}
        >
          <option value="all">all</option>
          <option value="any">any</option>
          <option value="none">none</option>
        </select>
        <span>of these conditions</span>
        <div className="spacer" />
        {props.onRemove && (
          <button className="icon" onClick={props.onRemove} aria-label="Remove group">
            {'\uE711'}
          </button>
        )}
      </div>
      {children.length === 0 && <div className="empty-card">No conditions: the rule applies to every process.</div>}
      {children.map((c, i) => {
        const update = (n: Condition) => setChildren(children.map((x, j) => (j === i ? n : x)))
        const remove = () => setChildren(children.filter((_, j) => j !== i))
        return c.type === 'group' ? (
          <ConditionGroup key={i} node={c} onChange={update} onRemove={remove} />
        ) : (
          <ConditionRow key={i} node={c} onChange={update} onRemove={remove} />
        )
      })}
      <div className="cgroup-actions">
        <button className="btn subtle" onClick={() => setChildren([...children, { type: 'cond', field: 'name', op: 'eq', value: '' }])}>
          <span className="glyph">{'\uE710'}</span> Condition
        </button>
        <button className="btn subtle" onClick={() => setChildren([...children, { type: 'group', combinator: 'or', children: [] }])}>
          <span className="glyph">{'\uE710'}</span> Group
        </button>
      </div>
    </div>
  )
}

function ConditionRow(props: { node: Condition; onChange: (c: Condition) => void; onRemove: () => void }) {
  const { node, onChange } = props
  const field = fieldOf(node.field)
  const ops = OPS[field.kind]

  const changeField = (id: string) => {
    const f = fieldOf(id)
    const op = OPS[f.kind].some((o) => o.id === node.op) ? node.op : OPS[f.kind][0].id
    onChange({ ...node, field: id, op, key: f.kind === 'fact' ? (node.key ?? '') : undefined })
  }

  return (
    <div className="crow">
      <select className="field-select" value={field.id} onChange={(e) => changeField(e.target.value)}>
        {FIELDS.map((g) => (
          <optgroup key={g.group} label={g.group}>
            {g.items.map((f) => (
              <option key={f.id} value={f.id}>
                {f.label}
              </option>
            ))}
          </optgroup>
        ))}
      </select>
      {field.kind === 'fact' && (
        <input className="key" placeholder="fact, e.g. obs.recording" value={node.key ?? ''} onChange={(e) => onChange({ ...node, key: e.target.value })} />
      )}
      <select className="op-select" value={node.op} onChange={(e) => onChange({ ...node, op: e.target.value })}>
        {ops.map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </select>
      {!NO_VALUE.has(node.op ?? '') &&
        (node.op === 'in' ? (
          <ChipInput value={node.value ?? ''} onChange={(v) => onChange({ ...node, value: v })} />
        ) : (
          <div className="value-wrap">
            <input
              placeholder={field.kind === 'number' ? '0' : node.op === 'glob' ? 'C:/Games/*' : 'value'}
              value={node.value ?? ''}
              onChange={(e) => onChange({ ...node, value: e.target.value })}
            />
            {field.unit && <span className="unit">{field.unit}</span>}
          </div>
        ))}
      {NO_VALUE.has(node.op ?? '') && <div className="spacer" />}
      <button className="icon" onClick={props.onRemove} aria-label="Remove condition">
        {'\uE711'}
      </button>
    </div>
  )
}

/** "is one of": names as removable chips; type and press Enter or a comma to add. */
export function ChipInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const items = splitList(value)
  const [draft, setDraft] = useState('')
  const commit = (text = draft) => {
    const extra = splitList(text)
    if (extra.length) onChange([...items, ...extra].join(', '))
    setDraft('')
  }
  return (
    <div className="chips-input">
      {items.map((it, i) => (
        <span className="chip-token" key={`${it}-${i}`}>
          {it}
          <button aria-label={`Remove ${it}`} onClick={() => onChange(items.filter((_, j) => j !== i).join(', '))}>
            {'\uE711'}
          </button>
        </span>
      ))}
      <input
        value={draft}
        placeholder={items.length ? 'add…' : 'name.exe, other.exe'}
        onChange={(e) => (e.target.value.endsWith(',') ? commit(e.target.value.slice(0, -1)) : setDraft(e.target.value))}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit()
          if (e.key === 'Backspace' && !draft && items.length) onChange(items.slice(0, -1).join(', '))
        }}
        onBlur={() => commit()}
      />
    </div>
  )
}
