import { isHost, post, useWindowState } from '../host'
import { Logo } from './Logo'

/**
 * Windows 11 style title area drawn by the app: icon and name on the left, the rest is a drag region
 * (CSS app-region, handled by WebView2), Fluent caption buttons on the right. Double-click maximizes, as usual.
 */
export function TitleBar() {
  const { maximized, active } = useWindowState()
  return (
    <header className={`titlebar ${active ? '' : 'inactive'}`}>
      <div className="titlebar-brand">
        <Logo size={20} />
        <span>Corewright</span>
      </div>
      {isHost && (
        <div className="caption-buttons">
          <button aria-label="Minimize" onClick={() => post({ type: 'minimize' })}>
            {'\uE921'}
          </button>
          <button aria-label={maximized ? 'Restore' : 'Maximize'} onClick={() => post({ type: 'maximize' })}>
            {maximized ? '\uE923' : '\uE922'}
          </button>
          <button className="close" aria-label="Close" onClick={() => post({ type: 'close' })}>
            {'\uE8BB'}
          </button>
        </div>
      )}
    </header>
  )
}
