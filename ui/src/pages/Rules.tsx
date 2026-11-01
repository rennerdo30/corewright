import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api'
import { describeCondition } from '../components/ConditionBuilder'
import { LassoImport } from '../components/LassoImport'
import { Dot, PageHeader, plural } from '../components/PageHeader'
import { RuleEditor, summarizeActions } from '../components/RuleEditor'
import { newId } from '../format'
import { usePoll } from '../hooks'
import { OWNER_LABELS, ownerOf, type Owner } from '../roles'
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

const GROUP_ORDER: Owner[] = ['rule', 'quick', 'lasso', 'example']

export function Rules({ draft, onDraftConsumed }: { draft?: Rule; onDraftConsumed: () => void }) {
  const [saved, setSaved] = useState<RuleSet>()
  const [set, setSet] = useState<RuleSet>()
  const [selectedId, setSelectedId] = useState<string>()
  const [errors, setErrors] = useState<RuleError[]>([])
  const [message, setMessage] = useState<string>()
  const [saving, setSaving] = useState(false)
  const [importing, setImporting] = useState(false)
  const [filter, setFilter] = useState('')
  const procs = usePoll(() => api.processes(), 3000)

  // How many running processes each rule shapes right now (it owns at least one of their settings).
  const live = useMemo(() => {
    const m = new Map<string, number>()
    for (const p of procs.data ?? []) for (const id of new Set((p.applied ?? []).map((a) => a.ruleId))) m.set(id, (m.get(id) ?? 0) + 1)
    return m
  }, [procs.data])

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

  const enabled = set.rules.filter((r) => r.enabled).length
  const activeRules = set.rules.filter((r) => live.has(r.id))
  const shaped = new Set((procs.data ?? []).filter((p) => p.applied?.some((a) => !a.ruleId.startsWith('autopilot-'))).map((p) => p.pid)).size
  const groups = GROUP_ORDER.map((o) => ({ owner: o, rules: ordered.filter((r) => ownerOf(r.id) === o) })).filter((g) => g.rules.length > 0)

  return (
    <div className="page split rules-page">
      <div className="rules-list">
        <PageHeader
          title="Rules"
          actions={
            <>
              <button className="btn subtle" onClick={() => setImporting(true)} disabled={dirty} title={dirty ? 'Save or discard your changes first' : 'Import from Process Lasso'}>
                <span className="glyph">{''}</span> Import
              </button>
              <button className="btn primary" onClick={add}>
                <span className="glyph">{''}</span> New rule
              </button>
            </>
          }
        >
          {set.rules.length === 0 ? (
            <>No rules yet. Create one, or import your Process Lasso settings.</>
          ) : (
            <>
              {enabled === set.rules.length ? <>{plural(enabled, 'rule')}, all on.</> : <>{enabled} of {plural(set.rules.length, 'rule')} on.</>}{' '}
              {activeRules.length === 0 ? (
                <>None of them matches a running process right now.</>
              ) : (
                <>
                  {plural(activeRules.length, 'is', 'are')} shaping {plural(shaped, 'process', 'processes')} right now.
                </>
              )}{' '}
              Autopilot adds its own rules below yours.
            </>
          )}
        </PageHeader>
        <div className="list-search">
          <div className="search-box">
            <span className="glyph">{''}</span>
            <input placeholder="Find a rule" value={filter} onChange={(e) => setFilter(e.target.value)} />
          </div>
        </div>
        {importing && <LassoImport onClose={() => setImporting(false)} onImported={reloadAfterImport} />}
        <div className="rule-items">
          {groups.map((g) => (
            <section key={g.owner} className={`rule-group role-${g.owner}`}>
              <div className="rule-group-head">
                <Dot role={g.owner}>{OWNER_LABELS[g.owner]}</Dot>
                <span className="secondary tiny">{g.rules.length}</span>
              </div>
              <ul>
                {g.rules.map((r) => {
                  const errs = errors.filter((e) => e.ruleId === r.id)
                  const n = live.get(r.id) ?? 0
                  return (
                    <li key={r.id} className={`${r.id === selectedId ? 'sel' : ''} ${r.enabled ? '' : 'off'}`} onClick={() => setSelectedId(r.id)}>
                      <div className="rule-text">
                        <div className="rule-name">
                          {r.name.replace(/^(Example|Quick|Process Lasso): /, '') || 'Untitled rule'}
                          {errs.length > 0 && <span className="tag danger">{errs.length} error{errs.length > 1 ? 's' : ''}</span>}
                        </div>
                        <div className="rule-desc">{summarizeActions(r.actions) || 'no settings'}</div>
                      </div>
                      <span className={`rule-live ${n ? 'on' : ''}`} title={n ? `Owns settings of ${plural(n, 'running process', 'running processes')}` : 'Matches no running process'}>
                        {n ? n : ''}
                      </span>
                      <span className="rule-prio" title="Priority: higher wins">
                        {r.priority}
                      </span>
                      <label className="switch" onClick={(e) => e.stopPropagation()} title={r.enabled ? 'Turn off' : 'Turn on'}>
                        <input type="checkbox" checked={r.enabled} onChange={(e) => updateRule({ ...r, enabled: e.target.checked })} />
                        <span />
                      </label>
                    </li>
                  )
                })}
              </ul>
            </section>
          ))}
          {groups.length === 0 && set.rules.length > 0 && <div className="ap-empty">No rule matches “{filter}”.</div>}
        </div>
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
