import type { ProcessRow } from './types'

/** Windows services in the process list (PM-46). Most services run in an svchost.exe of their own. */

export const isServiceHost = (p: ProcessRow) => p.name.toLowerCase() === 'svchost.exe'

/** "Service Host: Windows Update", "Service Host: Plug and Play (+5)"; undefined for other processes. */
export function serviceHostLabel(p: ProcessRow): string | undefined {
  const s = p.services
  if (!s?.length || !isServiceHost(p)) return undefined
  return s.length === 1 ? `Service Host: ${s[0].display}` : `Service Host: ${s[0].display} (+${s.length - 1})`
}

/** Text the search box matches for a process's services. */
export const serviceSearchText = (p: ProcessRow) => (p.services ?? []).map((s) => `${s.name} ${s.display}`).join(' ').toLowerCase()

/** One line per service for tooltips: "Windows Update (wuauserv)". */
export const serviceLines = (p: ProcessRow) => (p.services ?? []).map((s) => `${s.display} (${s.name})`)
