import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { PageHeader, plural } from '../components/PageHeader'
import { Card } from '../components/SettingsControls'
import { isHost, post } from '../host'
import { usePoll } from '../hooks'
import type { Condition, Rule } from '../types'

type Method = 'GET' | 'PUT' | 'POST' | 'DELETE'

const ENDPOINTS: { group: string; items: [Method[], string, string][] }[] = [
  {
    group: 'Read what is happening',
    items: [
      [['GET'], '/status', 'Service state, mode, pause flag, the running game'],
      [['GET'], '/system', 'CPU, memory, disk, GPU and network with 5 minutes of history'],
      [['GET'], '/topology', 'CCDs, cores, threads, V-Cache and the core ranking'],
      [['GET'], '/processes?include=…', 'Every process with the settings Corewright applied'],
      [['GET'], '/processes/{pid}/explain', 'Which rules match a process and which settings they win'],
      [['GET'], '/events?after={seq}', 'Changes Corewright made, for polling'],
      [['GET'], '/autopilot/placements', 'Where Autopilot placed each process, and why'],
    ],
  },
  {
    group: 'Tell Corewright about your app',
    items: [
      [['GET'], '/facts', 'All facts other apps have set'],
      [['PUT', 'DELETE'], '/facts/{key}', 'Set a fact such as {"value":"true"}, or remove it'],
    ],
  },
  {
    group: 'Change rules and settings',
    items: [
      [['GET', 'PUT'], '/rules', 'Read or replace the rule set; invalid rules are rejected'],
      [['POST'], '/rules/validate', 'Check a rule set without saving it'],
      [['GET', 'PUT'], '/settings', 'Autopilot and app settings'],
      [['POST'], '/engine/pause', 'Pause; every change is put back'],
      [['POST'], '/engine/resume', 'Resume the rules'],
    ],
  },
]

/** Every fact key a rule reads, with the rules that read it. */
function factUsers(rules: Rule[]): Map<string, string[]> {
  const out = new Map<string, string[]>()
  const walk = (c: Condition, rule: Rule) => {
    if (c.type === 'cond' && c.field === 'fact' && c.key) out.set(c.key, [...(out.get(c.key) ?? []), rule.name])
    c.children?.forEach((x) => walk(x, rule))
  }
  rules.filter((r) => r.enabled).forEach((r) => walk(r.when, r))
  return out
}

export function Integrations() {
  const facts = usePoll(api.facts, 2000)
  const status = usePoll(api.status, 10_000)
  const [rules, setRules] = useState<Rule[]>([])
  const [key, setKey] = useState('')
  const [value, setValue] = useState('true')
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    api.rules().then((r) => setRules(r.rules)).catch(() => {})
  }, [])

  const users = useMemo(() => factUsers(rules), [rules])
  const entries = Object.entries(facts.data ?? {})
  const waiting = [...users.keys()].filter((k) => !(k in (facts.data ?? {})))
  const origin = location.port === '5173' ? 'http://127.0.0.1:47830' : location.origin
  const endpointFile = status.data ? `${status.data.dataDirectory}\\endpoint.json` : undefined
  const example = `curl -X PUT ${origin}/api/v1/facts/obs.recording \\
  -H "Authorization: Bearer <token>" \\
  -H "Content-Type: application/json" -d '{"value":"true"}'`

  const add = async () => {
    if (!key.trim()) return
    await api.setFact(key.trim(), value)
    setKey('')
    facts.reload()
  }

  return (
    <div className="page dense integrations">
      <PageHeader title="Integrations">
        Other apps steer Corewright through a local API that only this PC can reach.{' '}
        {entries.length === 0 ? (
          <>No app has told Corewright anything yet.</>
        ) : (
          <>
            {plural(entries.length, 'fact is', 'facts are')} set right now
            {users.size > 0 ? <>, and {plural(users.size, 'rule condition', 'rule conditions')} react to facts</> : null}.
          </>
        )}
      </PageHeader>

      <div className="int-grid">
        <section>
          <h3>Facts</h3>
          <p className="section-desc">
            A fact is a small piece of context from another app, such as <code>obs.recording = true</code>. Rules react to it with the
            “Fact” condition, and it disappears when the service restarts.
          </p>
          <div className="card-group">
            {entries.map(([k, v]) => {
              const by = users.get(k) ?? []
              return (
                <div key={k} className="setting-row fact-row">
                  <div className="setting-text">
                    <div className="setting-title mono">{k}</div>
                    <div className="setting-desc">{by.length ? `Read by ${by.join(', ')}` : 'No rule reads this fact.'}</div>
                  </div>
                  <span className="fact-value mono">{v}</span>
                  <button className="btn subtle icon-btn" onClick={() => api.deleteFact(k).then(facts.reload)} title={`Remove ${k}`}>
                    <span className="glyph">{''}</span>
                  </button>
                </div>
              )
            })}
            {waiting.map((k) => (
              <div key={k} className="setting-row fact-row waiting">
                <div className="setting-text">
                  <div className="setting-title mono">{k}</div>
                  <div className="setting-desc">Not set. Read by {users.get(k)!.join(', ')}.</div>
                </div>
                <span className="fact-value secondary">waiting</span>
              </div>
            ))}
            {entries.length === 0 && waiting.length === 0 && (
              <div className="setting-row">
                <div className="setting-text">
                  <div className="setting-title">No facts yet</div>
                  <div className="setting-desc">Set one below to try a rule, or let an app push it through the API.</div>
                </div>
              </div>
            )}
            <div className="setting-row fact-add">
              <input placeholder="Key, for example obs.recording" value={key} onChange={(e) => setKey(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && add()} />
              <input className="fact-value-input" placeholder="Value" value={value} onChange={(e) => setValue(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && add()} />
              <button className="btn primary" onClick={add} disabled={!key.trim()}>
                Set fact
              </button>
            </div>
          </div>
        </section>

        <section>
          <h3>Connect an app</h3>
          <p className="section-desc">Three things an app needs. The token changes every time the service starts, so read it from the file each time.</p>
          <div className="card-group">
            <Card glyph={''} title="Address" desc={<span className="mono selectable">{origin}/api/v1</span>}>
              <span className="secondary tiny">127.0.0.1 only</span>
            </Card>
            <Card glyph={''} title="Token" desc={<>Send <code>Authorization: Bearer &lt;token&gt;</code>. The token is the <code>token</code> value in <span className="mono selectable">{endpointFile ?? 'endpoint.json'}</span>.</>}>
              {isHost && endpointFile && (
                <button className="btn" onClick={() => post({ type: 'reveal', path: endpointFile })}>
                  Show file
                </button>
              )}
            </Card>
            <div className="setting-row code-row">
              <div className="setting-text">
                <div className="setting-title">Try it</div>
                <div className="setting-desc">Tell Corewright that OBS is recording.</div>
              </div>
              <button
                className="btn subtle"
                onClick={() =>
                  navigator.clipboard.writeText(example).then(() => {
                    setCopied(true)
                    window.setTimeout(() => setCopied(false), 1500)
                  })
                }
              >
                <span className="glyph">{copied ? '' : ''}</span> {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <pre className="code int-code">{example}</pre>
          </div>
        </section>
      </div>

      <section>
        <h3>Endpoints</h3>
        <div className="card flush">
          <table className="ap-table endpoints">
            <tbody>
              {ENDPOINTS.map((g) => [
                <tr key={g.group} className="ep-group">
                  <td colSpan={2}>{g.group}</td>
                </tr>,
                ...g.items.map(([methods, path, desc]) => (
                  <tr key={path}>
                    <td className="ep-path">
                      {methods.map((m) => (
                        <span key={m} className={`method m-${m.toLowerCase()}`}>
                          {m}
                        </span>
                      ))}
                      <span className="mono">/api/v1{path}</span>
                    </td>
                    <td className="secondary">{desc}</td>
                  </tr>
                )),
              ])}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}
