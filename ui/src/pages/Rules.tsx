import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api'
import { ConditionGroup } from '../components/ConditionBuilder'
import { LassoImport } from '../components/LassoImport'
import { newId } from '../format'
import type { Rule, RuleActions, RuleError, RuleSet, RulesResult } from '../types'

const LEVELS = {
  cpuPriority: ['idle', 'belowNormal', 'normal', 'aboveNormal', 'high', 'realtime'],
  ioPriority: ['veryLow', 'low', 'normal', 'high'],
  gpuPriority: ['idle', 'belowNormal', 'normal', 'aboveNormal', 'high', 'realtime'],
  memoryPriority: ['veryLow', 'low', 'medium', 'belowNormal', 'normal'],
}

const emptyRule = (): Rule => ({
  id: newId(),
  name: 'New rule',
  enabled: true,
  priority: 50,
  sustainSeconds: 0,
  releaseSeconds: 0,
  allowRealtime: false,
  when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'eq', value: '' }] },
  actions: { cpuPriority: 'belowNormal' },
})

export function Rules({ draft, onDraftConsumed }: { draft?: Rule; onDraftConsumed: () => void }) {
  const [saved, setSaved] = useState<RuleSet>()
  const [set, setSet] = useState<RuleSet>()
  const [selectedId, setSelectedId] = useState<string>()
  const [errors, setErrors] = useState<RuleError[]>([])
  const [message, setMessage] = useState<string>()
  const [saving, setSaving] = useState(false)

  const [importing, setImporting] = useState(false)

  useEffect(() => {
    api.rules().then((r) => {
      setSaved(r)
      const next = draft ? { ...r, rules: [...r.rules, draft] } : r
      setSet(structuredClone(next))
      setSelectedId(draft?.id ?? next.rules[0]?.id)
      if (draft) onDraftConsumed()
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const reloadAfterImport = async () => {
    const r = await api.rules()
    setSaved(r)
    setSet(structuredClone(r))
    setMessage('Process Lasso rules imported and active.')
  }

  const dirty = useMemo(() => JSON.stringify(set) !== JSON.stringify(saved), [set, saved])

  // Validate on the service (single source of truth) shortly after each edit.
  useEffect(() => {
    if (!set) return
    const t = window.setTimeout(() => {
      api.validateRules(set).then((r) => setErrors(r.errors)).catch(() => {})
    }, 300)
    return () => window.clearTimeout(t)
  }, [set])

  if (!set) return <div className="empty">Loading…</div>

  const ordered = [...set.rules].sort((a, b) => b.priority - a.priority)
  const selected = set.rules.find((r) => r.id === selectedId)
  const updateRule = (rule: Rule) => setSet({ ...set, rules: set.rules.map((r) => (r.id === rule.id ? rule : r)) })

  const save = async () => {
    setSaving(true)
    setMessage(undefined)
    try {
      await api.saveRules(set)
      setSaved(structuredClone(set))
      setMessage('Saved. Rules apply within a second.')
    } catch (e) {
      if (e instanceof ApiError && e.status === 400) setErrors((e.body as RulesResult).errors)
      setMessage(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const add = () => {
    const r = emptyRule()
    setSet({ ...set, rules: [...set.rules, r] })
    setSelectedId(r.id)
  }

  const remove = (id: string) => {
    setSet({ ...set, rules: set.rules.filter((r) => r.id !== id) })
    if (selectedId === id) setSelectedId(undefined)
  }

  const duplicate = (rule: Rule) => {
    const copy = { ...structuredClone(rule), id: newId(), name: `${rule.name} (copy)` }
    setSet({ ...set, rules: [...set.rules, copy] })
    setSelectedId(copy.id)
  }

  return (
    <div className="page split">
      <div className="rules-list">
        <div className="toolbar">
          <h1>Rules</h1>
          <div className="row-gap">
            <button className="btn" onClick={() => setImporting(true)} disabled={dirty} title={dirty ? 'Save or discard your changes first' : undefined}>
              Import…
            </button>
            <button className="btn" onClick={add}>
              + New rule
            </button>
          </div>
        </div>
        {importing && <LassoImport onClose={() => setImporting(false)} onImported={reloadAfterImport} />}
        <div className="muted tiny explain-text">
          Each setting goes to the highest-priority matching rule that sets it. When no rule claims it any more, Corewright restores the
          original value.
        </div>
        <ul className="rule-items">
          {ordered.map((r) => {
            const errs = errors.filter((e) => e.ruleId === r.id)
            return (
              <li key={r.id} className={`${r.id === selectedId ? 'sel' : ''} ${r.enabled ? '' : 'off'}`} onClick={() => setSelectedId(r.id)}>
                <label className="switch" onClick={(e) => e.stopPropagation()}>
                  <input type="checkbox" checked={r.enabled} onChange={(e) => updateRule({ ...r, enabled: e.target.checked })} />
                  <span />
                </label>
                <div className="rule-text">
                  <div className="rule-name">{r.name || 'Untitled rule'}</div>
                  <div className="muted tiny">{summarize(r.actions)}</div>
                </div>
                <span className="prio">{r.priority}</span>
                {errs.length > 0 && <span className="tag danger">{errs.length}</span>}
              </li>
            )
          })}
        </ul>
        <div className="savebar">
          {message && <span className="muted tiny">{message}</span>}
          <button className="btn" disabled={!dirty || saving} onClick={() => saved && setSet(structuredClone(saved))}>
            Discard
          </button>
          <button className="btn primary" disabled={!dirty || saving || errors.length > 0} onClick={save}>
            {saving ? 'Saving…' : 'Save rules'}
          </button>
        </div>
      </div>
      {selected ? (
        <RuleEditor
          key={selected.id}
          rule={selected}
          errors={errors.filter((e) => e.ruleId === selected.id)}
          onChange={updateRule}
          onDelete={() => remove(selected.id)}
          onDuplicate={() => duplicate(selected)}
        />
      ) : (
        <div className="editor empty">Select a rule or create a new one.</div>
      )}
    </div>
  )
}

function summarize(a: RuleActions): string {
  const parts: string[] = []
  if (a.cpuPriority) parts.push(`CPU ${a.cpuPriority}`)
  if (a.affinity) parts.push(`cores ${a.affinity}`)
  if (a.ioPriority) parts.push(`I/O ${a.ioPriority}`)
  if (a.gpuPriority) parts.push(`GPU ${a.gpuPriority}`)
  if (a.memoryPriority) parts.push(`memory prio ${a.memoryPriority}`)
  if (a.efficiencyMode != null) parts.push(a.efficiencyMode ? 'efficiency mode' : 'no efficiency mode')
  if (a.memoryLimitMb) parts.push(`max ${a.memoryLimitMb} MB`)
  if (a.workingSetLimitMb) parts.push(`RAM ≤ ${a.workingSetLimitMb} MB`)
  return parts.join(' · ') || 'no actions'
}

function RuleEditor(props: { rule: Rule; errors: RuleError[]; onChange: (r: Rule) => void; onDelete: () => void; onDuplicate: () => void }) {
  const { rule, errors, onChange } = props
  const [json, setJson] = useState(false)
  const [jsonText, setJsonText] = useState('')
  const [jsonError, setJsonError] = useState<string>()
  const set = <K extends keyof Rule>(k: K, v: Rule[K]) => onChange({ ...rule, [k]: v })
  const setAction = <K extends keyof RuleActions>(k: K, v: RuleActions[K] | undefined) => {
    const actions = { ...rule.actions }
    if (v === undefined || v === null || v === '') delete actions[k]
    else actions[k] = v
    onChange({ ...rule, actions })
  }

  return (
    <div className="editor">
      <div className="editor-head">
        <input className="title-input" value={rule.name} onChange={(e) => set('name', e.target.value)} placeholder="Rule name" />
        <div className="seg">
          <button className={!json ? 'on' : ''} onClick={() => setJson(false)}>
            Builder
          </button>
          <button
            className={json ? 'on' : ''}
            onClick={() => {
              setJsonText(JSON.stringify(rule, null, 2))
              setJsonError(undefined)
              setJson(true)
            }}
          >
            JSON
          </button>
        </div>
        <button className="btn" onClick={props.onDuplicate}>
          Duplicate
        </button>
        <button className="btn danger" onClick={props.onDelete}>
          Delete
        </button>
      </div>

      {errors.length > 0 && (
        <div className="banner danger">
          {errors.map((e, i) => (
            <div key={i}>{e.message}</div>
          ))}
        </div>
      )}

      {json ? (
        <div className="json-edit">
          <textarea
            spellCheck={false}
            value={jsonText}
            onChange={(e) => {
              setJsonText(e.target.value)
              try {
                const parsed = JSON.parse(e.target.value) as Rule
                setJsonError(undefined)
                onChange({ ...parsed, id: rule.id })
              } catch (err) {
                setJsonError((err as Error).message)
              }
            }}
          />
          {jsonError && <div className="muted tiny">{jsonError}</div>}
        </div>
      ) : (
        <>
          <section className="block">
            <h3>Settings</h3>
            <div className="form-row">
              <label>
                Priority
                <input type="number" value={rule.priority} onChange={(e) => set('priority', Number(e.target.value))} />
              </label>
              <label title="The condition must hold this long before the rule applies">
                Apply after (s)
                <input type="number" min={0} step={0.5} value={rule.sustainSeconds} onChange={(e) => set('sustainSeconds', Number(e.target.value))} />
              </label>
              <label title="The condition must be false this long before the rule releases">
                Release after (s)
                <input type="number" min={0} step={0.5} value={rule.releaseSeconds} onChange={(e) => set('releaseSeconds', Number(e.target.value))} />
              </label>
              <label className="check">
                <input type="checkbox" checked={rule.allowRealtime} onChange={(e) => set('allowRealtime', e.target.checked)} /> Allow realtime
              </label>
            </div>
          </section>

          <section className="block">
            <h3>When</h3>
            <ConditionGroup node={rule.when} onChange={(w) => set('when', w)} root />
          </section>

          <section className="block">
            <h3>Then set</h3>
            <div className="actions-grid">
              {(Object.keys(LEVELS) as (keyof typeof LEVELS)[]).map((k) => (
                <ActionSelect key={k} label={ACTION_LABELS[k]} value={rule.actions[k] ?? ''} options={LEVELS[k]} onChange={(v) => setAction(k, v)} />
              ))}
              <label className="action">
                <span>CPU cores (affinity)</span>
                <input
                  placeholder="unchanged · all · p-cores · e-cores · 0-7,16"
                  value={rule.actions.affinity ?? ''}
                  onChange={(e) => setAction('affinity', e.target.value)}
                />
              </label>
              <label className="action">
                <span>Efficiency mode (EcoQoS)</span>
                <select
                  value={rule.actions.efficiencyMode == null ? '' : rule.actions.efficiencyMode ? 'on' : 'off'}
                  onChange={(e) => setAction('efficiencyMode', e.target.value === '' ? undefined : e.target.value === 'on')}
                >
                  <option value="">unchanged</option>
                  <option value="on">on</option>
                  <option value="off">off</option>
                </select>
              </label>
              <label className="action">
                <span>Memory cap, hard (MB)</span>
                <input
                  type="number"
                  min={16}
                  placeholder="unchanged"
                  value={rule.actions.memoryLimitMb ?? ''}
                  onChange={(e) => setAction('memoryLimitMb', e.target.value === '' ? undefined : Number(e.target.value))}
                />
                <small>Allocations above the cap fail; the app may crash.</small>
              </label>
              <label className="action">
                <span>RAM cap, soft (MB)</span>
                <input
                  type="number"
                  min={4}
                  placeholder="unchanged"
                  value={rule.actions.workingSetLimitMb ?? ''}
                  onChange={(e) => setAction('workingSetLimitMb', e.target.value === '' ? undefined : Number(e.target.value))}
                />
                <small>Windows pages memory out instead of failing.</small>
              </label>
            </div>
          </section>
        </>
      )}
    </div>
  )
}

const ACTION_LABELS: Record<keyof typeof LEVELS, string> = {
  cpuPriority: 'CPU priority',
  ioPriority: 'I/O priority',
  gpuPriority: 'GPU priority',
  memoryPriority: 'Memory priority',
}

function ActionSelect(props: { label: string; value: string; options: string[]; onChange: (v: string) => void }) {
  return (
    <label className="action">
      <span>{props.label}</span>
      <select value={props.value} onChange={(e) => props.onChange(e.target.value)}>
        <option value="">unchanged</option>
        {props.options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    </label>
  )
}
