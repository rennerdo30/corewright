import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api'
import { describeCondition } from '../components/ConditionBuilder'
import { LassoImport } from '../components/LassoImport'
import { RuleEditor, summarizeActions } from '../components/RuleEditor'
import { newId } from '../format'
import type { Rule, RuleError, RuleSet, RulesResult } from '../types'

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

/** Where a rule came from, shown as a small badge in the list. */
function sourceOf(r: Rule): string | undefined {
  if (r.id.startsWith('lasso-')) return 'Lasso'
  if (r.id.startsWith('quick-')) return 'Quick'
  if (r.id.startsWith('example-')) return 'Example'
  return undefined
}

export function Rules({ draft, onDraftConsumed }: { draft?: Rule; onDraftConsumed: () => void }) {
  const [saved, setSaved] = useState<RuleSet>()
  const [set, setSet] = useState<RuleSet>()
  const [selectedId, setSelectedId] = useState<string>()
  const [errors, setErrors] = useState<RuleError[]>([])
  const [message, setMessage] = useState<string>()
  const [saving, setSaving] = useState(false)
  const [importing, setImporting] = useState(false)
  const [filter, setFilter] = useState('')

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

  const q = filter.trim().toLowerCase()
  const ordered = [...set.rules]
    .filter((r) => !q || r.name.toLowerCase().includes(q) || describeCondition(r.when).toLowerCase().includes(q))
    .sort((a, b) => b.priority - a.priority)
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

  const reloadAfterImport = async () => {
    const r = await api.rules()
    setSaved(r)
    setSet(structuredClone(r))
    setMessage('Process Lasso rules imported and active.')
  }

  return (
    <div className="page split">
      <div className="rules-list">
        <div className="toolbar">
          <h1>Rules</h1>
          <div className="spacer" />
          <button className="btn subtle" onClick={() => setImporting(true)} disabled={dirty} title={dirty ? 'Save or discard your changes first' : 'Import from Process Lasso'}>
            <span className="glyph">{'\uE8B5'}</span> Import
          </button>
          <button className="btn primary" onClick={add}>
            <span className="glyph">{'\uE710'}</span> New rule
          </button>
        </div>
        <div className="list-search">
          <div className="search-box">
            <span className="glyph">{'\uE721'}</span>
            <input placeholder="Find a rule" value={filter} onChange={(e) => setFilter(e.target.value)} />
          </div>
        </div>
        {importing && <LassoImport onClose={() => setImporting(false)} onImported={reloadAfterImport} />}
        <ul className="rule-items">
          {ordered.map((r) => {
            const errs = errors.filter((e) => e.ruleId === r.id)
            const source = sourceOf(r)
            return (
              <li key={r.id} className={`${r.id === selectedId ? 'sel' : ''} ${r.enabled ? '' : 'off'}`} onClick={() => setSelectedId(r.id)}>
                <div className="rule-text">
                  <div className="rule-name">
                    {r.name || 'Untitled rule'}
                    {source && <span className="tag">{source}</span>}
                    {errs.length > 0 && <span className="tag danger">{errs.length} error{errs.length > 1 ? 's' : ''}</span>}
                  </div>
                  <div className="rule-desc">{summarizeActions(r.actions) || 'no settings'}</div>
                </div>
                <label className="switch" onClick={(e) => e.stopPropagation()} title={r.enabled ? 'Turn off' : 'Turn on'}>
                  <input type="checkbox" checked={r.enabled} onChange={(e) => updateRule({ ...r, enabled: e.target.checked })} />
                  <span />
                </label>
              </li>
            )
          })}
        </ul>
        {(dirty || message) && (
          <div className="savebar">
            {message && !dirty && <span className="secondary tiny">{message}</span>}
            {dirty && <span className="secondary tiny">Unsaved changes</span>}
            <div className="spacer" />
            <button className="btn" disabled={!dirty || saving} onClick={() => saved && setSet(structuredClone(saved))}>
              Discard
            </button>
            <button className="btn primary" disabled={!dirty || saving || errors.length > 0} onClick={save}>
              {saving ? 'Saving…' : 'Save'}
            </button>
          </div>
        )}
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
