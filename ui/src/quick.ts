import { api } from './api'
import type { Condition, ProcessRow, Rule, RuleActions, RuleSet } from './types'

/** "Always" settings from the process context menu live in one rule per process name, above imported and example rules. */
export const QUICK_PRIORITY = 60

const slug = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
export const quickId = (name: string) => `quick-${slug(name)}`
/** Lossless and disjoint from exe ids (which never contain "~"): service names are case-insensitive, so lower case is exact. */
const serviceId = (name: string) =>
  `quick~svc~${[...name.toLowerCase()].map((c) => (/[a-z0-9]/.test(c) ? c : `_${c.charCodeAt(0).toString(16)}`)).join('')}`
/** Service quick rules rank just above exe quick rules, so a service setting beats an older "every svchost.exe" one. */
export const QUICK_SERVICE_PRIORITY = QUICK_PRIORITY + 1

/**
 * What a quick rule targets. An ordinary process: its exe name. A service process (PM-46): its service, so "always"
 * for one svchost.exe does not change every svchost.exe. A host shared by several services is targeted by its first
 * service; the setting then reaches the other services in that process too (priorities are per process).
 */
export function quickSubject(p: ProcessRow): { id: string; name: string; cond: Condition; label: string; priority: number } {
  const svc = p.services?.[0]
  if (svc)
    return {
      id: serviceId(svc.name),
      name: `Quick: ${svc.display} (service)`,
      cond: { type: 'cond', field: 'service', op: 'eq', value: svc.name },
      label: `the ${svc.display} service`,
      priority: QUICK_SERVICE_PRIORITY,
    }
  return { id: quickId(p.name), name: `Quick: ${p.name}`, cond: { type: 'cond', field: 'name', op: 'eq', value: p.name }, label: `every ${p.name}`, priority: QUICK_PRIORITY }
}

export function quickRule(set: RuleSet | undefined, p: ProcessRow): Rule | undefined {
  const id = quickSubject(p).id
  return set?.rules.find((r) => r.id === id)
}

/** Sets (value) or clears (null) slots of the quick rule for this process and saves the rule set. */
export async function setQuick(p: ProcessRow, patch: Partial<Record<keyof RuleActions, unknown>>): Promise<RuleSet> {
  const set = await api.rules()
  const subject = quickSubject(p)
  const id = subject.id
  let rule = set.rules.find((r) => r.id === id)
  if (!rule) {
    rule = {
      id,
      name: subject.name,
      enabled: true,
      priority: subject.priority,
      sustainSeconds: 0,
      releaseSeconds: 0,
      allowRealtime: false,
      when: { type: 'group', combinator: 'and', children: [subject.cond] },
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
