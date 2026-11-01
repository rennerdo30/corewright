import { useCallback, useEffect, useRef, useState } from 'react'

/** True while the page is visible. Hidden pages do no work (hard CPU constraint). */
export function usePageVisible(): boolean {
  const [visible, setVisible] = useState(!document.hidden)
  useEffect(() => {
    const on = () => setVisible(!document.hidden)
    document.addEventListener('visibilitychange', on)
    return () => document.removeEventListener('visibilitychange', on)
  }, [])
  return visible
}

/**
 * Polls `load` every `intervalMs` while the page is visible. The next request starts only after the
 * previous one finished, so a slow service never gets a request pile-up.
 */
export function usePoll<T>(load: () => Promise<T>, intervalMs: number, enabled = true) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<string>()
  const visible = usePageVisible()
  const loadRef = useRef(load)
  loadRef.current = load
  const [nonce, setNonce] = useState(0)

  useEffect(() => {
    if (!visible || !enabled) return
    let stopped = false
    let timer: number | undefined
    const run = async () => {
      try {
        const value = await loadRef.current()
        if (!stopped) {
          setData(value)
          setError(undefined)
        }
      } catch (e) {
        if (!stopped) setError(e instanceof Error ? e.message : String(e))
      }
      if (!stopped) timer = window.setTimeout(run, intervalMs)
    }
    run()
    return () => {
      stopped = true
      window.clearTimeout(timer)
    }
  }, [visible, enabled, intervalMs, nonce])

  const reload = useCallback(() => setNonce((n) => n + 1), [])
  return { data, error, reload }
}
