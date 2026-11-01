import type { Condition } from '../types'

type Kind = 'text' | 'number' | 'bool' | 'fact'

export const FIELDS: { group: string; items: { id: string; label: string; kind: Kind; unit?: string }[] }[] = [
  {
    group: 'Process',
    items: [
      { id: 'name', label: 'Name', kind: 'text' },
      { id: 'path', label: 'Path', kind: 'text' },
      { id: 'parentName', label: 'Parent name', kind: 'text' },
      { id: 'commandLine', label: 'Command line', kind: 'text' },
      { id: 'pid', label: 'PID', kind: 'number' },
      { id: 'session', label: 'Session', kind: 'number' },
    ],
  },
  {
    group: 'Live',
    items: [
      { id: 'cpu', label: 'CPU', kind: 'number', unit: '%' },
      { id: 'privateMb', label: 'Private memory', kind: 'number', unit: 'MB' },
      { id: 'workingSetMb', label: 'Working set', kind: 'number', unit: 'MB' },
      { id: 'ioMBps', label: 'I/O', kind: 'number', unit: 'MB/s' },
      { id: 'threads', label: 'Threads', kind: 'number' },
      { id: 'handles', label: 'Handles', kind: 'number' },
      { id: 'isForeground', label: 'Is foreground', kind: 'bool' },
    ],
  },
  { group: 'Integrations', items: [{ id: 'fact', label: 'Fact (from an app)', kind: 'fact' }] },
]

const OPS: Record<Kind, { id: string; label: string }[]> = {
  text: [
    { id: 'eq', label: 'is' },
    { id: 'ne', label: 'is not' },
    { id: 'contains', label: 'contains' },
    { id: 'startsWith', label: 'starts with' },
    { id: 'endsWith', label: 'ends with' },
    { id: 'in', label: 'is one of' },
    { id: 'glob', label: 'matches (* ?)' },
    { id: 'regex', label: 'matches regex' },
  ],
  number: [
    { id: 'gt', label: '>' },
    { id: 'gte', label: '≥' },
    { id: 'lt', label: '<' },
    { id: 'lte', label: '≤' },
    { id: 'eq', label: '=' },
    { id: 'ne', label: '≠' },
  ],
  bool: [
    { id: 'isTrue', label: 'is true' },
    { id: 'isFalse', label: 'is false' },
  ],
  fact: [
    { id: 'isTrue', label: 'is true' },
    { id: 'isFalse', label: 'is false / missing' },
    { id: 'eq', label: 'is' },
    { id: 'ne', label: 'is not' },
    { id: 'contains', label: 'contains' },
    { id: 'gt', label: '>' },
    { id: 'lt', label: '<' },
    { id: 'exists', label: 'exists' },
    { id: 'notExists', label: 'does not exist' },
  ],
}

const NO_VALUE = new Set(['isTrue', 'isFalse', 'exists', 'notExists'])

const fieldOf = (id?: string) => FIELDS.flatMap((g) => g.items).find((f) => f.id === id) ?? FIELDS[0].items[0]

export function ConditionGroup(props: { node: Condition; onChange: (c: Condition) => void; onRemove?: () => void; root?: boolean }) {
  const { node, onChange } = props
  const children = node.children ?? []
  const setChildren = (c: Condition[]) => onChange({ ...node, children: c })

  return (
    <div className={`cgroup ${props.root ? 'root' : ''}`}>
      <div className="cgroup-head">
        <select value={node.negate ? 'not' : 'is'} onChange={(e) => onChange({ ...node, negate: e.target.value === 'not' })}>
          <option value="is">Match</option>
          <option value="not">Do not match</option>
        </select>
        <select value={node.combinator ?? 'and'} onChange={(e) => onChange({ ...node, combinator: e.target.value as 'and' | 'or' })}>
          <option value="and">all of</option>
          <option value="or">any of</option>
        </select>
        <span className="muted tiny">{children.length === 0 ? 'no conditions: matches every process' : ''}</span>
        <div className="spacer" />
        <button className="btn small" onClick={() => setChildren([...children, { type: 'cond', field: 'name', op: 'eq', value: '' }])}>
          + Condition
        </button>
        <button className="btn small" onClick={() => setChildren([...children, { type: 'group', combinator: 'or', children: [] }])}>
          + Group
        </button>
        {props.onRemove && (
          <button className="icon" onClick={props.onRemove} aria-label="Remove group">
            {'\uE711'}
          </button>
        )}
      </div>
      {children.map((c, i) => {
        const update = (n: Condition) => setChildren(children.map((x, j) => (j === i ? n : x)))
        const remove = () => setChildren(children.filter((_, j) => j !== i))
        return c.type === 'group' ? (
          <ConditionGroup key={i} node={c} onChange={update} onRemove={remove} />
        ) : (
          <ConditionRow key={i} node={c} onChange={update} onRemove={remove} />
        )
      })}
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
    onChange({ ...node, field: id, op, key: f.kind === 'fact' ? node.key ?? '' : undefined })
  }

  return (
    <div className="crow">
      <select value={field.id} onChange={(e) => changeField(e.target.value)}>
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
        <input className="key" placeholder="fact key, e.g. obs.recording" value={node.key ?? ''} onChange={(e) => onChange({ ...node, key: e.target.value })} />
      )}
      <select value={node.op} onChange={(e) => onChange({ ...node, op: e.target.value })}>
        {ops.map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </select>
      {!NO_VALUE.has(node.op ?? '') && (
        <div className="value-wrap">
          <input
            placeholder={node.op === 'in' ? 'a.exe, b.exe' : field.kind === 'number' ? '0' : 'value'}
            value={node.value ?? ''}
            onChange={(e) => onChange({ ...node, value: e.target.value })}
          />
          {field.unit && <span className="unit">{field.unit}</span>}
        </div>
      )}
      <button className="icon" onClick={props.onRemove} aria-label="Remove condition">
        {'\uE711'}
      </button>
    </div>
  )
}
