import { useState } from 'react'
import { levelLabel } from '../host'
import type { Condition, Rule, RuleActions, RuleError } from '../types'
import { ConditionGroup, describeCondition } from './ConditionBuilder'
import { ContextMenu, type MenuEntry } from './ContextMenu'

type ActionKey = keyof RuleActions

interface ActionDef {
  key: ActionKey
  title: string
  description: string
  glyph: string
  kind: 'levels' | 'toggle' | 'cores' | 'mb' | 'mbps' | 'dscp'
  levels?: string[]
  initial: RuleActions[ActionKey]
}

export const ACTIONS: ActionDef[] = [
  { key: 'networkBlocked', title: 'Block network', description: 'Also blocks downloads. Existing connections may survive. Applies to every process using this executable path; any block claim wins.', glyph: '\uE701', kind: 'toggle', initial: true },
  { key: 'uploadLimitMbps', title: 'Upload limit', description: 'Upload only; download limits need a kernel driver and are out of scope. Shared by executable path; the lowest nonzero limit wins.', glyph: '\uE898', kind: 'mbps', initial: 10 },
  { key: 'networkPriority', title: 'Network priority (DSCP)', description: 'Only helps when your router honours it. Shared by executable path; the highest DSCP number wins.', glyph: '\uE968', kind: 'dscp', initial: 0 },
  { key: 'cpuPriority', title: 'CPU priority', description: 'How much CPU time the process gets when the CPU is busy.', glyph: '\uE9D9', kind: 'levels', levels: ['idle', 'belowNormal', 'normal', 'aboveNormal', 'high', 'realtime'], initial: 'belowNormal' },
  { key: 'ioPriority', title: 'Disk priority', description: 'Who goes first when several processes read or write.', glyph: '\uEDA2', kind: 'levels', levels: ['veryLow', 'low', 'normal', 'high'], initial: 'low' },
  { key: 'gpuPriority', title: 'GPU priority', description: 'Scheduling priority on the graphics card.', glyph: '\uE7F4', kind: 'levels', levels: ['idle', 'belowNormal', 'normal', 'aboveNormal', 'high', 'realtime'], initial: 'aboveNormal' },
  { key: 'memoryPriority', title: 'Memory priority', description: 'Low priority memory is the first to leave RAM when it gets tight.', glyph: '\uE964', kind: 'levels', levels: ['veryLow', 'low', 'medium', 'belowNormal', 'normal'], initial: 'low' },
  { key: 'affinity', title: 'CPU cores', description: 'Which logical processors the process may run on.', glyph: '\uE950', kind: 'cores', initial: 'all' },
  { key: 'efficiencyMode', title: 'Efficiency mode', description: 'Windows EcoQoS: slower clocks for this process, less heat and power.', glyph: '\uEC0A', kind: 'toggle', initial: true },
  { key: 'memoryLimitMb', title: 'Memory cap (hard)', description: 'Allocations above the cap fail; the app may crash when it hits it.', glyph: '\uE8B1', kind: 'mb', initial: 4096 },
  { key: 'workingSetLimitMb', title: 'RAM cap (soft)', description: 'Windows pages memory out instead of letting the process use more RAM.', glyph: '\uE7B8', kind: 'mb', initial: 2048 },
]

const CORE_PRESETS: [string, string][] = [
  ['all', 'All cores'],
  ['p-cores', 'Performance cores'],
  ['e-cores', 'Efficiency cores'],
]

/** A plain-language sentence for the rule list and the editor header. */
export function summarizeActions(a: RuleActions): string {
  const parts = ACTIONS.filter((d) => a[d.key] != null).map((d) => {
    const v = a[d.key]
    if (d.kind === 'toggle') return `${d.title.toLowerCase()} ${v ? 'on' : 'off'}`
    if (d.kind === 'mbps') return `upload ${v === 0 ? 'unlimited' : `${v} Mbps`}`
    if (d.kind === 'dscp') return `network DSCP ${v}`
    if (d.kind === 'mb') return `${d.title.toLowerCase().replace(/ \(.*\)/, '')} ${formatMb(v as number)}`
    if (d.kind === 'cores') return `cores ${v}`
    return `${d.title.replace(' priority', '').toLowerCase()} ${levelLabel(v as string).toLowerCase()}`
  })
  return parts.join(', ')
}

const formatMb = (mb: number) => (mb >= 1024 && mb % 1024 === 0 ? `${mb / 1024} GB` : `${mb} MB`)

export function RuleEditor(props: {
  rule: Rule
  errors: RuleError[]
  onChange: (r: Rule) => void
  onDelete: () => void
  onDuplicate: () => void
}) {
  const { rule, errors, onChange } = props
  const [json, setJson] = useState<string>()
  const [jsonError, setJsonError] = useState<string>()
  const [addMenu, setAddMenu] = useState<{ x: number; y: number }>()
  const [moreMenu, setMoreMenu] = useState<{ x: number; y: number }>()
  const [advanced, setAdvanced] = useState(false)

  const set = <K extends keyof Rule>(k: K, v: Rule[K]) => onChange({ ...rule, [k]: v })
  const setAction = (k: ActionKey, v: unknown) => {
    const actions = { ...rule.actions } as Record<string, unknown>
    if (v === undefined || v === null || v === '') delete actions[k]
    else actions[k] = v
    const next = { ...rule, actions: actions as RuleActions }
    if (actions.cpuPriority === 'realtime' || actions.gpuPriority === 'realtime') next.allowRealtime = true
    onChange(next)
  }

  const setActions = ACTIONS.filter((d) => rule.actions[d.key] != null)
  const unset = ACTIONS.filter((d) => rule.actions[d.key] == null)

  const moreItems: MenuEntry[] = [
    { type: 'item', label: 'Duplicate', glyph: '\uE8C8', onClick: props.onDuplicate },
    {
      type: 'item',
      label: 'Edit as JSON',
      glyph: '\uE943',
      onClick: () => {
        setJson(JSON.stringify(rule, null, 2))
        setJsonError(undefined)
      },
    },
    { type: 'sep' },
    { type: 'item', label: 'Delete rule', glyph: '\uE74D', onClick: props.onDelete },
  ]

  return (
    <div className="editor">
      <div className="editor-head">
        <input className="title-input" value={rule.name} onChange={(e) => set('name', e.target.value)} placeholder="Rule name" />
        <label className="toggle-label">
          {rule.enabled ? 'On' : 'Off'}
          <span className="switch">
            <input type="checkbox" checked={rule.enabled} onChange={(e) => set('enabled', e.target.checked)} />
            <span />
          </span>
        </label>
        <button className="icon big" aria-label="More" onClick={(e) => setMoreMenu({ x: e.clientX, y: e.clientY })}>
          {'\uE712'}
        </button>
      </div>

      <p className="rule-sentence">
        <strong>When</strong> {describeCondition(rule.when)}, <strong>set</strong> {summarizeActions(rule.actions) || 'nothing yet'}.
      </p>

      {errors.length > 0 && (
        <div className="banner danger">
          <span className="glyph">{'\uE783'}</span>
          <div>
            {errors.map((e, i) => (
              <div key={i}>{e.message}</div>
            ))}
          </div>
        </div>
      )}

      {json !== undefined ? (
        <section className="section">
          <div className="section-head">
            <h3>JSON</h3>
            <button className="btn" onClick={() => setJson(undefined)}>
              Done
            </button>
          </div>
          <textarea
            className="json"
            spellCheck={false}
            value={json}
            onChange={(e) => {
              setJson(e.target.value)
              try {
                const parsed = JSON.parse(e.target.value) as Rule
                setJsonError(undefined)
                onChange({ ...parsed, id: rule.id })
              } catch (err) {
                setJsonError((err as Error).message)
              }
            }}
          />
          {jsonError && <div className="secondary tiny">{jsonError}</div>}
        </section>
      ) : (
        <>
          <section className="section">
            <div className="section-head">
              <h3>Applies to</h3>
            </div>
            <ConditionGroup node={rule.when} onChange={(w: Condition) => set('when', w)} root />
          </section>

          <section className="section">
            <div className="section-head">
              <h3>Settings to apply</h3>
              <button className="btn" disabled={unset.length === 0} onClick={(e) => setAddMenu({ x: e.clientX, y: e.clientY })}>
                <span className="glyph">{'\uE710'}</span> Add setting
              </button>
            </div>
            {setActions.length === 0 && <div className="empty-card">No settings yet. Add what this rule should change.</div>}
            {setActions.map((d) => (
              <div className="setting-card" key={d.key}>
                <span className="setting-icon glyph">{d.glyph}</span>
                <div className="setting-text">
                  <div className="setting-title">{d.title}</div>
                  <div className="setting-desc">{d.description}</div>
                </div>
                <div className="setting-control">
                  <ActionControl def={d} value={rule.actions[d.key]} allowRealtime={rule.allowRealtime} onChange={(v) => setAction(d.key, v)} />
                </div>
                <button className="icon" aria-label={`Remove ${d.title}`} onClick={() => setAction(d.key, null)}>
                  {'\uE711'}
                </button>
              </div>
            ))}
          </section>

          <section className="section">
            <button className="expander" onClick={() => setAdvanced((a) => !a)}>
              <span className="glyph">{'\uE713'}</span>
              <span className="setting-text">
                <span className="setting-title">Advanced</span>
                <span className="setting-desc">
                  Rule priority {rule.priority} · apply after {rule.sustainSeconds} s · release after {rule.releaseSeconds} s
                </span>
              </span>
              <span className="chev glyph">{advanced ? '\uE70E' : '\uE70D'}</span>
            </button>
            {advanced && (
              <div className="expander-body">
                <SettingRow title="Rule priority" desc="When two rules set the same thing for a process, the higher number wins.">
                  <input type="number" className="num-input" value={rule.priority} onChange={(e) => set('priority', Number(e.target.value))} />
                </SettingRow>
                <SettingRow title="Apply after" desc="The conditions must hold this long first. Avoids reacting to short spikes.">
                  <Unit unit="s">
                    <input type="number" min={0} step={0.5} className="num-input" value={rule.sustainSeconds} onChange={(e) => set('sustainSeconds', Number(e.target.value))} />
                  </Unit>
                </SettingRow>
                <SettingRow title="Release after" desc="The conditions must be false this long before the original values come back.">
                  <Unit unit="s">
                    <input type="number" min={0} step={0.5} className="num-input" value={rule.releaseSeconds} onChange={(e) => set('releaseSeconds', Number(e.target.value))} />
                  </Unit>
                </SettingRow>
                <SettingRow title="Allow realtime" desc="Realtime CPU or GPU priority can freeze input and audio. Use with care.">
                  <span className="switch">
                    <input type="checkbox" checked={rule.allowRealtime} onChange={(e) => set('allowRealtime', e.target.checked)} />
                    <span />
                  </span>
                </SettingRow>
              </div>
            )}
          </section>
        </>
      )}

      {addMenu && (
        <ContextMenu
          x={addMenu.x}
          y={addMenu.y}
          onClose={() => setAddMenu(undefined)}
          items={unset.map((d) => ({ type: 'item', label: d.title, glyph: d.glyph, onClick: () => setAction(d.key, d.initial) }))}
        />
      )}
      {moreMenu && <ContextMenu x={moreMenu.x} y={moreMenu.y} onClose={() => setMoreMenu(undefined)} items={moreItems} />}
    </div>
  )
}

function SettingRow({ title, desc, children }: { title: string; desc: string; children: React.ReactNode }) {
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

function Unit({ unit, children }: { unit: string; children: React.ReactNode }) {
  return (
    <span className="unit-wrap">
      {children}
      <span className="unit">{unit}</span>
    </span>
  )
}

function ActionControl({ def, value, allowRealtime, onChange }: { def: ActionDef; value: unknown; allowRealtime: boolean; onChange: (v: unknown) => void }) {
  if (def.kind === 'mbps') return <Unit unit="Mbps"><input type="number" min={0} step={0.1} className="num-input wide" value={(value as number) ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} /></Unit>
  if (def.kind === 'dscp') return <span className="cores-control">
    <select value={value as number} onChange={(e) => onChange(e.target.value === 'default' ? null : Number(e.target.value))}>
      <option value="default">Default</option><option value={8}>Low (CS1)</option><option value={0}>Normal</option><option value={34}>High (AF41)</option><option value={46}>Highest (EF)</option>
      {![8, 0, 34, 46].includes(value as number) && <option value={value as number}>Custom DSCP</option>}
    </select><input aria-label="DSCP number" type="number" min={0} max={63} step={1} className="num-input" value={value as number} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />
  </span>
  if (def.kind === 'levels') {
    const levels = def.levels!.filter((l) => l !== 'realtime' || allowRealtime || value === 'realtime')
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
  if (def.kind === 'toggle') {
    return (
      <label className="toggle-label">
        {value ? 'On' : 'Off'}
        <span className="switch">
          <input type="checkbox" checked={!!value} onChange={(e) => onChange(e.target.checked)} />
          <span />
        </span>
      </label>
    )
  }
  if (def.kind === 'mb') {
    return (
      <Unit unit="MB">
        <input type="number" min={16} step={256} className="num-input wide" value={(value as number) ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />
      </Unit>
    )
  }
  // cores
  const v = (value as string) ?? ''
  const preset = CORE_PRESETS.find(([k]) => k === v)
  return (
    <span className="cores-control">
      <select value={preset ? v : 'custom'} onChange={(e) => onChange(e.target.value === 'custom' ? '0-7' : e.target.value)}>
        {CORE_PRESETS.map(([k, l]) => (
          <option key={k} value={k}>
            {l}
          </option>
        ))}
        <option value="custom">Specific cores…</option>
      </select>
      {!preset && <input className="cores-input" value={v} placeholder="0-7,16" onChange={(e) => onChange(e.target.value)} />}
    </span>
  )
}
