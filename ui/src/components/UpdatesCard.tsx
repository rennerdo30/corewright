import { useEffect, useState } from 'react'
import { isHost, post } from '../host'

interface UpdateStatus {
  type: 'update-status'
  currentVersion: string
  latestVersion?: string
  lastChecked?: string
  available: boolean
  canInstall: boolean
  busy: boolean
  phase: string
  message: string
  releaseUrl: string
  channel: 'stable' | 'beta'
}

export function UpdatesCard() {
  const [status, setStatus] = useState<UpdateStatus>()
  useEffect(() => {
    const webview = window.chrome?.webview
    if (!webview) return
    const receive = (event: { data: unknown }) => {
      const data = event.data as UpdateStatus | undefined
      if (data?.type === 'update-status') setStatus(data)
    }
    webview.addEventListener('message', receive)
    post({ type: 'update-status' })
    return () => webview.removeEventListener('message', receive)
  }, [])
  const available = status?.available
  return (
    <section className="section updates-card" aria-label="Corewright updates">
      <h3>Updates</h3>
      <div className="setting-card">
        <span className="setting-icon glyph" aria-hidden="true">{'\uE895'}</span>
        <div className="setting-text">
          <div className="setting-title">Corewright updates</div>
          <div className="setting-desc">Current version: {status?.currentVersion ?? '—'} · Latest: {status?.latestVersion ?? '—'}</div>
          <div className="setting-desc">Last checked: {status?.lastChecked ? new Date(status.lastChecked).toLocaleString() : 'Never'}</div>
        </div>
        <button className="btn" disabled={!isHost || !!status?.busy} onClick={() => post({ type: 'update-check' })}>Check now</button>
      </div>
      <div className="setting-row">
        <div className="setting-text">
          <div className="setting-title">Update channel</div>
          <div className="setting-desc">Beta gets new features first and may have rough edges. Stable gets only finished releases. A beta install starts on Beta.</div>
        </div>
        <div className="seg">
          {(['stable', 'beta'] as const).map((c) => (
            <button key={c} className={status?.channel === c ? 'on' : ''} disabled={!isHost || !!status?.busy} onClick={() => post({ type: 'update-channel', channel: c })}>
              {c === 'stable' ? 'Stable' : 'Beta'}
            </button>
          ))}
        </div>
      </div>
      <div className="setting-row">
        <div className="setting-text" role="status" aria-live="polite">
          <div className={`setting-title ${status?.phase === 'error' ? 'updates-error' : ''}`}>
            {!isHost ? 'Open the Corewright window to check for updates.' : status?.busy ? status.message : available ? `Corewright ${status.latestVersion} is available.` : status?.message || 'Checks once a day when the dashboard opens.'}
          </div>
          {available && !status?.busy && status?.phase === 'error' && <div className="setting-desc updates-error">{status.message}</div>}
          {available && !status?.busy && <div className="setting-desc">{status?.canInstall ? 'The verified setup requests administrator approval and closes Corewright during installation.' : 'Update available: download from GitHub.'}</div>}
        </div>
        {available && status?.canInstall && <button className="btn primary" disabled={status.busy} onClick={() => post({ type: 'update-install' })}>Install update</button>}
        {available && !status?.canInstall && <a className="btn" href={status?.releaseUrl} target="_blank" rel="noreferrer">Download from GitHub</a>}
      </div>
    </section>
  )
}
