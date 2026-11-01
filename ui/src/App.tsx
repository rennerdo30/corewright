import { useState } from 'react'
import { api } from './api'
import { TitleBar } from './components/TitleBar'
import { usePoll } from './hooks'
import { Activity } from './pages/Activity'
import { Autopilot } from './pages/Autopilot'
import { Integrations } from './pages/Integrations'
import { Overview } from './pages/Overview'
import { Processes } from './pages/Processes'
import { Rules } from './pages/Rules'
import { Settings } from './pages/Settings'
import type { Rule } from './types'

export type Page = 'overview' | 'autopilot' | 'processes' | 'rules' | 'activity' | 'integrations' | 'settings'

// Segoe Fluent Icons code points: Home, LightningBolt, BulletedList, Filter, History, Link.
const NAV: { id: Page; label: string; glyph: string }[] = [
  { id: 'overview', label: 'Overview', glyph: '\uE80F' },
  { id: 'autopilot', label: 'Autopilot', glyph: '\uE945' },
  { id: 'processes', label: 'Processes', glyph: '\uE8FD' },
  { id: 'rules', label: 'Rules', glyph: '\uE71C' },
  { id: 'activity', label: 'Activity', glyph: '\uE81C' },
  { id: 'integrations', label: 'Integrations', glyph: '\uE71B' },
]

export function App() {
  const [page, setPageState] = useState<Page>(() => {
    const h = location.hash.slice(1) as Page
    return NAV.some((n) => n.id === h) || h === 'settings' ? h : 'overview'
  })
  const setPage = (p: Page) => {
    setPageState(p)
    history.replaceState(null, '', `#${p}`)
  }
  const [draftRule, setDraftRule] = useState<Rule>()
  // Navigation pane like Task Manager: collapses to an icon rail; the expanded width can be dragged.
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('corewright.nav.collapsed') === '1')
  const [navWidth, setNavWidth] = useState(() => Number(localStorage.getItem('corewright.nav.width')) || 200)
  const toggleNav = () =>
    setCollapsed((c) => {
      localStorage.setItem('corewright.nav.collapsed', c ? '0' : '1')
      return !c
    })
  const startNavResize = (e: React.PointerEvent) => {
    if (collapsed) return
    e.preventDefault()
    const startX = e.clientX
    const startW = navWidth
    document.body.classList.add('resizing')
    const move = (ev: PointerEvent) => setNavWidth(Math.min(320, Math.max(150, startW + ev.clientX - startX)))
    const up = (ev: PointerEvent) => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      document.body.classList.remove('resizing')
      localStorage.setItem('corewright.nav.width', String(Math.min(320, Math.max(150, startW + ev.clientX - startX))))
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }
  const status = usePoll(api.status, 3000)
  const s = status.data

  const togglePause = async () => {
    if (!s) return
    await (s.paused ? api.resume() : api.pause())
    status.reload()
  }

  const createRuleFor = (rule: Rule) => {
    setDraftRule(rule)
    setPage('rules')
  }

  return (
    <div className="app">
      <TitleBar />
      <div className="shell" style={{ gridTemplateColumns: `${collapsed ? 48 : navWidth}px 1fr` }}>
      <aside className={`nav ${collapsed ? 'collapsed' : ''}`}>
        <button className="nav-item hamburger" onClick={toggleNav} title={collapsed ? 'Expand navigation' : 'Collapse navigation'}>
          <span className="glyph">{'\uE700'}</span>
        </button>
        {NAV.map((n) => (
          <button key={n.id} className={`nav-item ${page === n.id ? 'active' : ''}`} onClick={() => setPage(n.id)} title={collapsed ? n.label : undefined}>
            <span className="glyph">{n.glyph}</span>
            <span className="nav-label">{n.label}</span>
          </button>
        ))}
        {!collapsed && <div className="nav-resize" onPointerDown={startNavResize} title="Drag to resize" />}
        <button className={`nav-item settings-item ${page === 'settings' ? 'active' : ''}`} onClick={() => setPage('settings')} title={collapsed ? 'Settings' : undefined}>
          <span className="glyph">{'\uE713'}</span>
          <span className="nav-label">Settings</span>
        </button>
        <div className="nav-footer">
          {collapsed ? (
            s && (
              <button className="nav-item" onClick={togglePause} title={s.paused ? 'Rules paused: resume' : `${s.activeRules} rules active: pause and revert all`}>
                <span className={`glyph ${s.paused ? 'warn-text' : 'ok-text'}`}>{s.paused ? '\uE768' : '\uE769'}</span>
              </button>
            )
          ) : status.error ? (
            <div className="pill danger">{status.error}</div>
          ) : s ? (
            <>
              <div className={`pill ${s.paused ? 'warn' : 'ok'}`}>{s.paused ? 'Rules paused' : `${s.activeRules} ${s.activeRules === 1 ? 'rule' : 'rules'} active`}</div>
              <button className="btn" onClick={togglePause}>
                {s.paused ? 'Resume rules' : 'Pause and revert all'}
              </button>
              <div className="muted tiny">
                v{s.version} · {s.mode}
                {s.elevated || s.mode === 'service' ? '' : ' · not elevated'}
              </div>
            </>
          ) : (
            <div className="muted tiny">Connecting…</div>
          )}
        </div>
      </aside>
      <main className="content">
        {s && s.mode === 'console' && !s.elevated && (
          <div className="banner">Development mode: the service runs without admin rights and can only change your own processes.</div>
        )}
        {page === 'overview' && <Overview onOpen={setPage} />}
        {page === 'autopilot' && <Autopilot />}
        {page === 'processes' && <Processes onCreateRule={createRuleFor} />}
        {page === 'rules' && <Rules draft={draftRule} onDraftConsumed={() => setDraftRule(undefined)} />}
        {page === 'activity' && <Activity />}
        {page === 'integrations' && <Integrations />}
        {page === 'settings' && <Settings />}
      </main>
      </div>
    </div>
  )
}
