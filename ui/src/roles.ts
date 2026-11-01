/**
 * Who owns a setting, from the id of the rule that set it. The same colours on every page (PM-35): Autopilot's
 * parts keep their Autopilot colours, the user's own rules, quick settings and imports get theirs.
 */
export type Owner = 'game' | 'build' | 'background' | 'balance' | 'rule' | 'quick' | 'lasso' | 'example'

export function ownerOf(ruleId: string | undefined): Owner {
  const id = ruleId ?? ''
  if (id === 'autopilot-game' || id === 'autopilot-voice') return 'game'
  if (id === 'autopilot-build-tools') return 'build'
  if (id.startsWith('autopilot-background') || id === 'autopilot-game-background') return 'background'
  if (id.startsWith('autopilot-')) return 'balance'
  if (id.startsWith('quick-')) return 'quick'
  if (id.startsWith('lasso-')) return 'lasso'
  if (id.startsWith('example-')) return 'example'
  return 'rule'
}

/** Same, from a rule's display name (activity events carry the name only). */
export function ownerOfName(name: string | undefined): Owner {
  const n = name ?? ''
  if (n === 'Autopilot: game' || n.startsWith('Autopilot: keep voice')) return 'game'
  if (n.startsWith('Autopilot: compilers')) return 'build'
  if (n.startsWith('Autopilot: background')) return 'background'
  if (n.startsWith('Autopilot:')) return 'balance'
  if (n.startsWith('Quick:')) return 'quick'
  if (n.startsWith('Process Lasso:')) return 'lasso'
  return 'rule'
}

export const isAutopilot = (o: Owner) => o === 'game' || o === 'build' || o === 'background' || o === 'balance'

export const OWNER_LABELS: Record<Owner, string> = {
  game: 'Game mode',
  build: 'Build tools',
  background: 'Background placement',
  balance: 'Balancing',
  rule: 'Your rules',
  quick: 'Quick settings',
  lasso: 'Imported from Process Lasso',
  example: 'Examples',
}
