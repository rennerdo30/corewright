import { api } from '../api'
import { levelLabel } from '../host'
import { usePoll } from '../hooks'

const SLOT_LABELS: Record<string, string> = {
  cpuPriority: 'CPU priority',
  affinity: 'cores',
  ioPriority: 'I/O priority',
  gpuPriority: 'GPU priority',
  memoryPriority: 'memory priority',
  efficiencyMode: 'efficiency mode',
  memoryLimitMb: 'memory cap',
  workingSetLimitMb: 'RAM cap',
}

function value(slot: string, v: string) {
  if (slot === 'memoryLimitMb' || slot === 'workingSetLimitMb') return `${v} MB`
  if (slot === 'affinity') return v
  return levelLabel(v) || v
}

export function ago(ms: number) {
  const s = Math.max(0, Math.round((Date.now() - ms) / 1000))
  if (s < 60) return `${s} s ago`
  if (s < 3600) return `${Math.round(s / 60)} min ago`
  if (s < 86400) return `${Math.round(s / 3600)} h ago`
  return new Date(ms).toLocaleDateString()
}

/**
 * The last processes this rule changed (owner, 2026-11-02: "show what processes were affected by the rule the last
 * 10 times or so"). Kept by the service since it started; "still applies" means the rule owns a setting right now.
 */
export function RuleHistory({ ruleId, activePids }: { ruleId: string; activePids: Set<number> }) {
  const hits = usePoll(() => api.ruleHistory(ruleId), 5000)
  const list = hits.data ?? []
  return (
    <section className="section">
      <div className="section-head">
        <h3>Recently applied to</h3>
        <span className="secondary tiny">{activePids.size ? `Controls ${activePids.size} running ${activePids.size === 1 ? 'process' : 'processes'} now` : 'Controls no running process now'}</span>
      </div>
      {list.length === 0 ? (
        <div className="empty-card">This rule has not changed a process since the service started.</div>
      ) : (
        <div className="card flush hits">
          {list.map((h, i) => {
            const parts = Object.entries(h.set).map(([slot, v]) => `${SLOT_LABELS[slot] ?? slot} ${value(slot, v)}`)
            const live = h.kind === 'applied' && activePids.has(h.pid)
            return (
              <div key={`${h.time}-${h.pid}-${i}`} className={`hit ${h.kind}`}>
                <span className="hit-time" title={new Date(h.time).toLocaleString()}>
                  {ago(h.time)}
                </span>
                <span className="hit-proc">
                  {h.process}
                  <span className="secondary tiny"> {h.pid}</span>
                </span>
                <span className="hit-what">
                  {h.kind === 'failed' ? <>Could not set {parts.join(', ')}{h.message ? `: ${h.message}` : ''}</> : <>Set {parts.join(', ')}</>}
                </span>
                <span className={`hit-state ${live ? 'live' : ''}`}>{h.kind === 'failed' ? 'failed' : live ? 'still applies' : 'ended'}</span>
              </div>
            )
          })}
        </div>
      )}
    </section>
  )
}
