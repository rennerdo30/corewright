import type { AppSettings, BackgroundRow, EngineEvent, Placement, Explain, ImportResponse, ProcessRow, RuleSet, RulesResult, Status, SystemInfo, Topology } from './types'

declare global {
  interface Window {
    __COREWRIGHT__?: { token?: string; accent?: string[]; host?: string }
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void
        addEventListener: (type: 'message', listener: (e: { data: unknown }) => void) => void
        removeEventListener: (type: 'message', listener: (e: { data: unknown }) => void) => void
      }
    }
  }
}

export class ApiError extends Error {
  constructor(
    public status: number,
    public body: unknown,
  ) {
    super(status === 401 ? 'Not authorized: open Corewright from the tray.' : `Request failed (${status})`)
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  const token = window.__COREWRIGHT__?.token
  if (token) headers.Authorization = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const res = await fetch(`/api/v1${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? JSON.parse(text) : undefined
  if (!res.ok) throw new ApiError(res.status, data)
  return data as T
}

export const api = {
  status: () => request<Status>('GET', '/status'),
  system: () => request<SystemInfo>('GET', '/system'),
  processes: (include?: string) => request<ProcessRow[]>('GET', `/processes${include ? `?include=${include}` : ''}`),
  topology: () => request<Topology>('GET', '/topology'),
  settings: () => request<AppSettings>('GET', '/settings'),
  saveSettings: (s: AppSettings) => request<{ ok: boolean }>('PUT', '/settings', s),
  background: () => request<BackgroundRow[]>('GET', '/autopilot/background'),
  placements: () => request<Placement[]>('GET', '/autopilot/placements'),
  explain: (pid: number) => request<Explain>('GET', `/processes/${pid}/explain`),
  rules: () => request<RuleSet>('GET', '/rules'),
  saveRules: (set: RuleSet) => request<RulesResult>('PUT', '/rules', set),
  validateRules: (set: RuleSet) => request<RulesResult>('POST', '/rules/validate', set),
  pause: () => request<void>('POST', '/engine/pause'),
  resume: () => request<void>('POST', '/engine/resume'),
  facts: () => request<Record<string, string>>('GET', '/facts'),
  setFact: (key: string, value: string) => request<void>('PUT', `/facts/${encodeURIComponent(key)}`, { value }),
  deleteFact: (key: string) => request<void>('DELETE', `/facts/${encodeURIComponent(key)}`),
  importLasso: (iniText: string | null, apply: boolean) =>
    request<ImportResponse>('POST', '/import/process-lasso', { iniText, apply }),
  events: (after: number) => request<{ last: number; events: EngineEvent[] }>('GET', `/events?after=${after}`),
}
