import type { ApiCall, MemoryModule, RuleHit, AppSettings, BackgroundRow, DevicesInfo, EngineEvent, Placement, Explain, ImportResponse, ProcessRow, RuleSet, RulesResult, Status, SystemInfo, Topology } from './types'

declare global {
  interface Window {
    __COREWRIGHT__?: { token?: string; accent?: string[]; host?: string; scheme?: string }
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
  // Screenshot mode (VITE_DEMO=1): made-up data, no service. A compile-time constant, so normal builds drop demo.ts.
  if (import.meta.env.VITE_DEMO === '1') return (await import('./demo')).demoRequest(method, path, body) as Promise<T>
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
  devices: (history = '', includeHidden = false) => request<DevicesInfo>('GET', `/devices?history=${encodeURIComponent(history)}&includeHidden=${includeHidden}`),
  processes: (include?: string) => request<ProcessRow[]>('GET', `/processes${include ? `?include=${include}` : ''}`),
  topology: () => request<Topology>('GET', '/topology'),
  memory: () => request<MemoryModule[]>('GET', '/memory'),
  settings: () => request<AppSettings>('GET', '/settings'),
  saveSettings: (s: AppSettings) => request<{ ok: boolean }>('PUT', '/settings', s),
  background: () => request<BackgroundRow[]>('GET', '/autopilot/background'),
  placements: () => request<Placement[]>('GET', '/autopilot/placements'),
  autopilotPreview: () => request<{ pid: number; process: string; rule: string; slot: string; value: string; reason: string }[]>('GET', '/autopilot/preview'),
  explain: (pid: number) => request<Explain>('GET', `/processes/${pid}/explain`),
  rules: () => request<RuleSet>('GET', '/rules'),
  saveRules: (set: RuleSet) => request<RulesResult>('PUT', '/rules', set),
  apiCalls: () => request<ApiCall[]>('GET', '/api-calls'),
  ruleHistory: (id: string) => request<RuleHit[]>('GET', `/rules/${encodeURIComponent(id)}/history`),
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
