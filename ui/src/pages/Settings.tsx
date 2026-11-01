import { useEffect, useState } from 'react'
import { api } from '../api'
import { PageHeader } from '../components/PageHeader'
import { Card, Toggle } from '../components/SettingsControls'
import { isHost, post } from '../host'
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
        <h3>Startup</h3>
        <Autostart />
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
