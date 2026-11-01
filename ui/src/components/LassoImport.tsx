import { useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../api'
import type { ImportResponse, Rule } from '../types'

function summary(r: Rule): string {
  const a = r.actions
  return [
    a.cpuPriority && `CPU ${a.cpuPriority}`,
    a.ioPriority && `I/O ${a.ioPriority}`,
    a.gpuPriority && `GPU ${a.gpuPriority}`,
    a.memoryPriority && `memory ${a.memoryPriority}`,
    a.affinity && `cores ${a.affinity}`,
    a.efficiencyMode && 'efficiency mode',
  ]
    .filter(Boolean)
    .join(' · ')
}

/** Preview first, then one click imports everything. Re-importing replaces the previous import. */
export function LassoImport({ onClose, onImported }: { onClose: () => void; onImported: () => void }) {
  const [preview, setPreview] = useState<ImportResponse>()
  const [iniText, setIniText] = useState<string | null>(null)
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const file = useRef<HTMLInputElement>(null)

  const load = async (text: string | null) => {
    setBusy(true)
    setError(undefined)
    try {
      setIniText(text)
      setPreview(await api.importLasso(text, false))
    } catch (e) {
      setPreview(undefined)
      setError(
        e instanceof ApiError && e.status === 404
          ? 'No Process Lasso configuration found on this PC. Choose prolasso.ini manually.'
          : e instanceof Error
            ? e.message
            : String(e),
      )
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    load(null)
  }, [])

  const importAll = async () => {
    setBusy(true)
    try {
      const r = await api.importLasso(iniText, true)
      if (r.applied) {
        onImported()
        onClose()
      } else setError(r.errors.map((e) => e.message).join('; '))
    } catch (e) {
      const body = e instanceof ApiError ? (e.body as ImportResponse | undefined) : undefined
      setError(body?.errors?.map((x) => `${x.ruleName}: ${x.message}`).join('; ') || (e instanceof Error ? e.message : String(e)))
    } finally {
      setBusy(false)
    }
  }

  const readFile = (f: File) => {
    // FileReader honours the UTF-16 byte order mark that Process Lasso writes.
    const reader = new FileReader()
    reader.onload = () => load(String(reader.result ?? ''))
    reader.readAsText(f)
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="panel-head">
          <div>
            <h2>Import from Process Lasso</h2>
            <div className="muted tiny">{preview?.source ?? 'Reading the configuration…'}</div>
          </div>
          <button className="icon" onClick={onClose} aria-label="Close">
            {'\uE711'}
          </button>
        </div>

        {error && <div className="banner danger">{error}</div>}

        {preview && (
          <>
            <h3>{preview.rules.length} rules will be created</h3>
            <ul className="import-list">
              {preview.rules.map((r) => (
                <li key={r.id}>
                  <span className="rule-name">{r.name.replace('Process Lasso: ', '')}</span>
                  <span className="muted tiny">{summary(r)}</span>
                </li>
              ))}
            </ul>
            {preview.notes.filter((n) => n.kind !== 'info').length > 0 && (
              <>
                <h3>Not imported</h3>
                <ul className="notes">
                  {preview.notes
                    .filter((n) => n.kind !== 'info')
                    .map((n, i) => (
                      <li key={i}>
                        <span className={`tag ${n.kind === 'skipped' ? 'drift' : ''}`}>{n.kind}</span> {n.message}
                      </li>
                    ))}
                </ul>
              </>
            )}
            <div className="banner warn">
              Imported rules replace any earlier import; your own rules stay. Quit Process Lasso afterwards, otherwise both tools set the
              same processes.
            </div>
          </>
        )}

        <div className="savebar flush">
          <input ref={file} type="file" accept=".ini" hidden onChange={(e) => e.target.files?.[0] && readFile(e.target.files[0])} />
          <button className="btn" onClick={() => file.current?.click()} disabled={busy}>
            Choose prolasso.ini…
          </button>
          <div className="spacer" />
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
          <button className="btn primary" disabled={busy || !preview || preview.rules.length === 0} onClick={importAll}>
            {busy ? 'Working…' : 'Import everything'}
          </button>
        </div>
      </div>
    </div>
  )
}
