import { api } from './api'
import type { ProcessRow, Rule, RuleActions, RuleSet } from './types'

/** "Always" settings from the process context menu live in one rule per process name, above imported and example rules. */
export const QUICK_PRIORITY = 60

export const quickId = (name: string) => `quick-${name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}`

export function quickRule(set: RuleSet | undefined, name: string): Rule | undefined {
  return set?.rules.find((r) => r.id === quickId(name))
}

/** Sets (value) or clears (null) slots of the quick rule for this process name and saves the rule set. */
export async function setQuick(p: ProcessRow, patch: Partial<Record<keyof RuleActions, unknown>>): Promise<RuleSet> {
  const set = await api.rules()
  const id = quickId(p.name)
  let rule = set.rules.find((r) => r.id === id)
  if (!rule) {
    rule = {
      id,
      name: `Quick: ${p.name}`,
      enabled: true,
      priority: QUICK_PRIORITY,
      sustainSeconds: 0,
      releaseSeconds: 0,
      allowRealtime: false,
      when: { type: 'group', combinator: 'and', children: [{ type: 'cond', field: 'name', op: 'eq', value: p.name }] },
      actions: {},
    }
    set.rules.push(rule)
  }
  const actions = { ...rule.actions } as Record<string, unknown>
  for (const [k, v] of Object.entries(patch)) {
    if (v === null || v === undefined) delete actions[k]
    else actions[k] = v
  }
  rule.actions = actions as RuleActions
  rule.allowRealtime = actions.cpuPriority === 'realtime' || actions.gpuPriority === 'realtime'
  if (Object.keys(actions).length === 0) set.rules = set.rules.filter((r) => r.id !== id)
  await api.saveRules(set)
  return set
}
