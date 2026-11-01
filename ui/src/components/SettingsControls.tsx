import { useState } from 'react'
import { levelLabel } from '../host'
import { ChipInput } from './ConditionBuilder'

/** Windows Settings building blocks, shared by the Autopilot and Settings pages so both feel the same. */

export function Card({ glyph, title, desc, children }: { glyph: string; title: string; desc: React.ReactNode; children?: React.ReactNode }) {
  return (
    <div className="setting-card">
      <span className="setting-icon glyph">{glyph}</span>
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      {children && <div className="setting-control">{children}</div>}
    </div>
  )
}

export function Row({ title, desc, children }: { title: string; desc: string; children: React.ReactNode }) {
  return (
    <div className="setting-row">
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      <div className="setting-control">{children}</div>
    </div>
  )
}

export function ListRow({ title, desc, value, onChange }: { title: string; desc: string; value: string[]; onChange: (v: string[]) => void }) {
  return (
    <div className="setting-row list-row">
      <div className="setting-text">
        <div className="setting-title">{title}</div>
        <div className="setting-desc">{desc}</div>
      </div>
      <div className="setting-list">
        <ChipInput value={value.join(', ')} onChange={(v) => onChange(v.split(',').map((x) => x.trim()).filter(Boolean))} />
      </div>
    </div>
  )
}

export function Toggle({ value, onChange, disabled }: { value: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <label className="toggle-label" onClick={(e) => e.stopPropagation()}>
      {value ? 'On' : 'Off'}
      <span className="switch">
        <input type="checkbox" checked={value} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
        <span />
      </span>
    </label>
  )
}

export function Levels({ levels, value, onChange }: { levels: string[]; value: string; onChange: (v: string) => void }) {
  return (
    <div className="seg levels">
      {levels.map((l) => (
        <button key={l} className={value === l ? 'on' : ''} onClick={() => onChange(l)}>
          {levelLabel(l)}
        </button>
      ))}
    </div>
  )
}

export function Unit({ unit, children }: { unit: string; children: React.ReactNode }) {
  return (
    <span className="unit-wrap">
      {children}
      <span className="unit">{unit}</span>
    </span>
  )
}

export function NumberBox({ value, onChange, min, max, step, unit }: { value: number; onChange: (v: number) => void; min?: number; max?: number; step?: number; unit?: string }) {
  const input = <input type="number" className="num-input" min={min} max={max} step={step} value={value} onChange={(e) => onChange(Number(e.target.value))} />
  return unit ? <Unit unit={unit}>{input}</Unit> : input
}

/**
 * A feature group like the Windows Settings expander: the header row carries the feature's switch and a one-line
 * summary of its state; the detail rows open below it.
 */
export function Group({
  glyph,
  title,
  summary,
  enabled,
  onToggle,
  role,
  children,
  defaultOpen = false,
}: {
  glyph: string
  title: string
  summary: React.ReactNode
  enabled?: boolean
  onToggle?: (v: boolean) => void
  role?: 'game' | 'build' | 'background' | 'balance'
  children: React.ReactNode
  defaultOpen?: boolean
}) {
  const [open, setOpen] = useState(defaultOpen)
  return (
    <div className={`group ${open ? 'open' : ''} ${enabled === false ? 'off' : ''}`}>
      <div className="group-head" role="button" tabIndex={0} onClick={() => setOpen((o) => !o)} onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && setOpen((o) => !o)}>
        <span className={`group-icon glyph ${role ? `role-${role}` : ''}`}>{glyph}</span>
        <div className="setting-text">
          <div className="setting-title">{title}</div>
          <div className="setting-desc">{summary}</div>
        </div>
        {onToggle && enabled !== undefined && <Toggle value={enabled} onChange={onToggle} />}
        <span className="group-chevron glyph">{open ? '\uE70E' : '\uE70D'}</span>
      </div>
      {open && <div className="group-body">{children}</div>}
    </div>
  )
}
