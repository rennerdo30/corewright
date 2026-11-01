import { useState } from 'react'
import { api } from '../api'
import { usePoll } from '../hooks'

const ENDPOINTS: [string, string][] = [
  ['GET /api/v1/status', 'Service state, mode, pause flag'],
  ['GET /api/v1/system', 'CPU, memory, I/O and 5 minutes of history'],
  ['GET /api/v1/processes', 'All processes with the settings Corewright applied'],
  ['GET /api/v1/processes/{pid}/explain', 'Which rules match a process and which settings they win'],
  ['GET | PUT /api/v1/rules', 'Read or replace the rule set (PUT rejects invalid rules)'],
  ['PUT /api/v1/facts/{key}', 'Push context, e.g. {"value":"true"} for obs.recording'],
  ['DELETE /api/v1/facts/{key}', 'Remove a fact'],
  ['POST /api/v1/engine/pause | resume', 'Pause (reverts everything) or resume rules'],
  ['GET /api/v1/events?after={seq}', 'Changes Corewright made, for polling'],
]

export function Integrations() {
  const facts = usePoll(api.facts, 2000)
  const [key, setKey] = useState('')
  const [value, setValue] = useState('true')

  const add = async () => {
    if (!key.trim()) return
    await api.setFact(key.trim(), value)
    setKey('')
    facts.reload()
  }

  return (
    <div className="page">
      <h1>Integrations</h1>
      <div className="grid2">
        <section className="card">
          <div className="card-head">
            <span>Facts from other apps</span>
          </div>
          <p className="muted tiny">
            Apps push facts such as <code>obs.recording = true</code> or <code>discord.call = true</code>. Rules read them with the
            “Fact” condition.
          </p>
          <table className="mini">
            <tbody>
              {Object.entries(facts.data ?? {}).map(([k, v]) => (
                <tr key={k}>
                  <td className="mono">{k}</td>
                  <td className="mono">{v}</td>
                  <td className="num">
                    <button className="icon" onClick={() => api.deleteFact(k).then(facts.reload)} aria-label={`Remove ${k}`}>
                      {'\uE711'}
                    </button>
                  </td>
                </tr>
              ))}
              {Object.keys(facts.data ?? {}).length === 0 && (
                <tr>
                  <td className="muted">No facts yet.</td>
                </tr>
              )}
            </tbody>
          </table>
          <div className="form-row">
            <input placeholder="key, e.g. obs.recording" value={key} onChange={(e) => setKey(e.target.value)} />
            <input placeholder="value" value={value} onChange={(e) => setValue(e.target.value)} />
            <button className="btn" onClick={add}>
              Set fact
            </button>
          </div>
        </section>
        <section className="card">
          <div className="card-head">
            <span>Local HTTP API</span>
          </div>
          <p className="muted tiny">
            Listens on <code>127.0.0.1:47830</code> only. Every call needs <code>Authorization: Bearer &lt;token&gt;</code>; the token is
            in <code>endpoint.json</code> in Corewright's data folder and changes at every service start.
          </p>
          <table className="mini">
            <tbody>
              {ENDPOINTS.map(([e, d]) => (
                <tr key={e}>
                  <td className="mono">{e}</td>
                  <td className="muted tiny">{d}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <pre className="code">{`curl -X PUT http://127.0.0.1:47830/api/v1/facts/obs.recording \\
  -H "Authorization: Bearer <token>" \\
  -H "Content-Type: application/json" -d '{"value":"true"}'`}</pre>
        </section>
      </div>
    </div>
  )
}
