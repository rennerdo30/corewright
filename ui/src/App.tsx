import { useState } from 'react'
import { api } from './api'
import { TitleBar } from './components/TitleBar'
import { usePoll } from './hooks'
import { Activity } from './pages/Activity'
import { Integrations } from './pages/Integrations'
import { Overview } from './pages/Overview'
import { Processes } from './pages/Processes'
import { Rules } from './pages/Rules'
import type { Rule } from './types'

export type Page = 'overview' | 'processes' | 'rules' | 'activity' | 'integrations'

// Segoe Fluent Icons code points: Home, BulletedList, Filter, History, Link.
const NAV: { id: Page; label: string; glyph: string }[] = [
  { id: 'overview', label: 'Overview', glyph: '\uE80F' },
  { id: 'processes', label: 'Processes', glyph: '\uE8FD' },
  { id: 'rules', label: 'Rules', glyph: '\uE71C' },
  { id: 'activity', label: 'Activity', glyph: '\uE81C' },
  { id: 'integrations', label: 'Integrations', glyph: '\uE71B' },
]

export function App() {
  const [page, setPageState] = useState<Page>(() => {
    const h = location.hash.slice(1) as Page
    return NAV.some((n) => n.id === h) ? h : 'overview'
  })
  const setPage = (p: Page) => {
    setPageState(p)
    history.replaceState(null, '', `#${p}`)
  }
  const [draftRule, setDraftRule] = useState<Rule>()
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
      <div className="shell">
      <aside className="nav">
        {NAV.map((n) => (
          <button key={n.id} className={`nav-item ${page === n.id ? 'active' : ''}`} onClick={() => setPage(n.id)}>
            <span className="glyph">{n.glyph}</span>
            {n.label}
          </button>
        ))}
        <div className="nav-footer">
          {status.error ? (
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
        {page === 'processes' && <Processes onCreateRule={createRuleFor} />}
        {page === 'rules' && <Rules draft={draftRule} onDraftConsumed={() => setDraftRule(undefined)} />}
        {page === 'activity' && <Activity />}
        {page === 'integrations' && <Integrations />}
      </main>
      </div>
    </div>
  )
}
