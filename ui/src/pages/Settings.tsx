import { useEffect, useState } from 'react'
import { PRESETS, getAppearance, setAppearance, windowsAccent, type Scheme } from '../appearance'
import { api } from '../api'
import { PageHeader } from '../components/PageHeader'
import { Card, Toggle } from '../components/SettingsControls'
import { isHost, post } from '../host'
import { UpdatesCard } from '../components/UpdatesCard'
import { usePoll } from '../hooks'

/** App settings. Autopilot has its own page; this one is for how Corewright itself starts and runs. */
export function Settings() {
  const status = usePoll(api.status, 5000)
  const s = status.data

  return (
    <div className="page dense settings">
      <PageHeader title="Settings">
        How Corewright itself starts and runs. Game mode, balancing and core placement are on the Autopilot page.
      </PageHeader>

      <section className="section">
        <h3>Startup and window</h3>
        <div className="card-group">
          <Autostart />
          <WindowCards />
        </div>
      </section>
      <UpdatesCard />

      <section className="section">
        <h3>Appearance</h3>
        <AppearanceCards />
      </section>

      <section className="section">
        <h3>About</h3>
        <div className="card-group">
          <Card glyph={'\uE946'} title="Version" desc="Corewright">
            <span>{s?.version}</span>
          </Card>
          <Card glyph={'\uE912'} title="Service" desc={s?.mode === 'service' ? 'Installed Windows service, starts with Windows.' : 'Running as an app (start.bat).'}>
            <span>{s ? (s.elevated || s.mode === 'service' ? 'Full access' : 'Limited to your own processes') : ''}</span>
          </Card>
          <Card glyph={'\uE8B7'} title="Data folder" desc={<span className="mono selectable">{s?.dataDirectory}</span>} />
        </div>
      </section>
    </div>
  )
}

/** Start with Windows: the dashboard window writes the per-user Run entry (the same one the installer uses). */
function Autostart() {
  const [state, setState] = useState<{ enabled: boolean; available: boolean; note?: string }>()
  useEffect(() => {
    const wv = window.chrome?.webview
    if (!wv) return
    const on = (e: { data: unknown }) => {
      const d = e.data as { type?: string; enabled?: boolean; available?: boolean; note?: string }
      if (d?.type === 'autostart') setState({ enabled: !!d.enabled, available: !!d.available, note: d.note })
    }
    wv.addEventListener('message', on)
    post({ type: 'autostart-get' })
    return () => wv.removeEventListener('message', on)
  }, [])
  return (
    <Card
      glyph={'\uE7E8'}
      title="Start Corewright when I sign in"
      desc={!isHost ? 'Available in the Corewright window.' : (state?.note ?? 'Starts the tray icon when you sign in. The service itself starts with Windows.')}
    >
      <Toggle value={!!state?.enabled} disabled={!state?.available} onChange={(v) => post({ type: 'autostart-set', enabled: v })} />
    </Card>
  )
}

/** Theme and accent colour, applied at once and remembered for this user. */
function AppearanceCards() {
  const [a, setA] = useState(getAppearance)
  const change = (next: Partial<typeof a>) => {
    const v = { ...a, ...next }
    setA(v)
    setAppearance(v)
  }
  const win = windowsAccent()
  const custom = a.accent !== 'windows' && !PRESETS.some((p) => p.color === a.accent)
  return (
    <div className="card-group">
      <Card glyph={'\uE790'} title="Theme" desc={isHost ? 'Light, dark, or the same as Windows.' : 'In a browser the page follows the browser theme.'}>
        <div className="seg">
          {(['system', 'light', 'dark'] as Scheme[]).map((s) => (
            <button key={s} className={a.scheme === s ? 'on' : ''} onClick={() => change({ scheme: s })}>
              {s === 'system' ? 'Windows' : s === 'light' ? 'Light' : 'Dark'}
            </button>
          ))}
        </div>
      </Card>
      <div className="setting-row accent-row">
        <span className="setting-icon glyph">{'\uE771'}</span>
        <div className="setting-text">
          <div className="setting-title">Accent colour</div>
          <div className="setting-desc">Buttons, switches, selection and charts. Autopilot's colours stay the same so they always mean the same thing.</div>
        </div>
        <div className="swatches" role="radiogroup" aria-label="Accent colour">
          <button
            role="radio"
            aria-checked={a.accent === 'windows'}
            className={`swatch windows ${a.accent === 'windows' ? 'on' : ''}`}
            style={{ '--sw': win ?? 'var(--accent)' } as React.CSSProperties}
            title="Windows accent colour"
            onClick={() => change({ accent: 'windows' })}
          >
            <span className="glyph">{'\uE7F4'}</span>
          </button>
          {PRESETS.map((p) => (
            <button
              key={p.color}
              role="radio"
              aria-checked={a.accent === p.color}
              className={`swatch ${a.accent === p.color ? 'on' : ''}`}
              style={{ '--sw': p.color } as React.CSSProperties}
              title={p.name}
              onClick={() => change({ accent: p.color })}
            />
          ))}
          <label className={`swatch custom ${custom ? 'on' : ''}`} title="Custom colour" style={custom ? ({ '--sw': a.accent } as React.CSSProperties) : undefined}>
            <span className="glyph">{'\uE790'}</span>
            <input type="color" value={a.accent === 'windows' ? (win ?? '#0078d4') : a.accent} onChange={(e) => change({ accent: e.target.value })} aria-label="Custom accent colour" />
          </label>
        </div>
      </div>
    </div>
  )
}

interface WindowPrefs {
  openOnStart: boolean
  closeToTray: boolean
  minimizeToTray: boolean
}

/** Start minimized, close to tray, minimize to tray (owner, 2026-11-02). Stored per user by the window host. */
function WindowCards() {
  const [prefs, setPrefs] = useState<WindowPrefs>()
  useEffect(() => {
    const wv = window.chrome?.webview
    if (!wv) return
    const on = (e: { data: unknown }) => {
      const d = e.data as { type?: string } & WindowPrefs
      if (d?.type === 'window-prefs') setPrefs({ openOnStart: d.openOnStart, closeToTray: d.closeToTray, minimizeToTray: d.minimizeToTray })
    }
    wv.addEventListener('message', on)
    post({ type: 'window-prefs-get' })
    return () => wv.removeEventListener('message', on)
  }, [])
  const set = (next: Partial<WindowPrefs>) => {
    if (prefs) setPrefs({ ...prefs, ...next })
    post({ type: 'window-prefs-set', ...next })
  }
  const off = !isHost || !prefs
  return (
    <>
      <Card glyph={'\uE737'} title="Open the window when Corewright starts" desc="Off: Corewright starts minimized as an icon in the notification area.">
        <Toggle value={!!prefs?.openOnStart} disabled={off} onChange={(v) => set({ openOnStart: v })} />
      </Card>
      <Card glyph={'\uE711'} title="Close button" desc="What the window's close button does. The service keeps applying your rules either way.">
        <div className="seg">
          <button className={prefs?.closeToTray !== false ? 'on' : ''} disabled={off} onClick={() => set({ closeToTray: true })}>
            Keep in the tray
          </button>
          <button className={prefs?.closeToTray === false ? 'on' : ''} disabled={off} onClick={() => set({ closeToTray: false })}>
            Exit Corewright
          </button>
        </div>
      </Card>
      <Card glyph={'\uE921'} title="Minimize to the tray" desc="Minimizing hides the window in the notification area instead of the taskbar.">
        <Toggle value={!!prefs?.minimizeToTray} disabled={off} onChange={(v) => set({ minimizeToTray: v })} />
      </Card>
    </>
  )
}
