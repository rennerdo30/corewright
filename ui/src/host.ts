import { useEffect, useState } from 'react'

/** True inside the Corewright dashboard window (WebView2 host), false in a normal browser during development. */
export const isHost = !!window.chrome?.webview && window.__COREWRIGHT__?.host === 'dashboard'

export const post = (message: unknown) => window.chrome?.webview?.postMessage(message)

export interface WindowState {
  maximized: boolean
  active: boolean
}

/** Window state pushed by the host (maximized for the restore glyph, active to dim the title like Windows does). */
export function useWindowState(): WindowState {
  const [state, setState] = useState<WindowState>({ maximized: false, active: true })
  useEffect(() => {
    const wv = window.chrome?.webview
    if (!wv) return
    const on = (e: { data: unknown }) => {
      const d = e.data as { type?: string; maximized?: boolean; active?: boolean }
      if (d?.type === 'window') setState({ maximized: !!d.maximized, active: d.active !== false })
    }
    wv.addEventListener('message', on)
    return () => wv.removeEventListener('message', on)
  }, [])
  return state
}

export const iconUrl = (path?: string) =>
  isHost && path ? `https://corewright.host/icon?size=32&path=${encodeURIComponent(path)}` : undefined

const descriptions = new Map<string, string>()
const pending = new Set<string>()

/** File descriptions ("Google Chrome") for exe paths, fetched once per path from the host and cached for the session. */
export function useDescriptions(paths: (string | undefined)[]): Map<string, string> {
  const [, bump] = useState(0)
  useEffect(() => {
    if (!isHost) return
    const missing = [...new Set(paths.filter((p): p is string => !!p && !descriptions.has(p) && !pending.has(p)))].slice(0, 40)
    if (missing.length === 0) return
    let alive = true
    for (const p of missing) pending.add(p)
    Promise.all(
      missing.map((p) =>
        fetch(`https://corewright.host/describe?path=${encodeURIComponent(p)}`)
          .then((r) => (r.ok ? r.json() : { description: '' }))
          .then((j: { description?: string }) => descriptions.set(p, j.description ?? ''))
          .catch(() => descriptions.set(p, ''))
          .finally(() => pending.delete(p)),
      ),
    ).then(() => alive && bump((n) => n + 1))
    return () => {
      alive = false
    }
  })
  return descriptions
}

const LEVEL_LABELS: Record<string, string> = {
  idle: 'Idle',
  belowNormal: 'Below normal',
  normal: 'Normal',
  aboveNormal: 'Above normal',
  high: 'High',
  realtime: 'Realtime',
  veryLow: 'Very low',
  low: 'Low',
  medium: 'Medium',
}

/** "belowNormal" → "Below normal". */
export const levelLabel = (v?: string | null) => (v ? LEVEL_LABELS[v] ?? v : '')
