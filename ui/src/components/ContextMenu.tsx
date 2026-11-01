import { useEffect, useLayoutEffect, useRef, useState } from 'react'

export type MenuEntry =
  | { type: 'item'; label: string; glyph?: string; onClick?: () => void; disabled?: boolean; checked?: boolean; submenu?: MenuEntry[] }
  | { type: 'sep' }
  | { type: 'head'; label: string }

/** A Windows 11 MenuFlyout: keyboard Escape, click outside or scroll closes it; submenus open on hover. */
export function ContextMenu({ x, y, items, onClose, keepOpen }: { x: number; y: number; items: MenuEntry[]; onClose: () => void; keepOpen?: boolean }) {
  useEffect(() => {
    const close = (e: Event) => {
      if (e instanceof KeyboardEvent && e.key !== 'Escape') return
      onClose()
    }
    window.addEventListener('mousedown', close)
    window.addEventListener('keydown', close)
    window.addEventListener('blur', close)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('mousedown', close)
      window.removeEventListener('keydown', close)
      window.removeEventListener('blur', close)
      window.removeEventListener('resize', close)
    }
  }, [onClose])

  return <MenuPanel x={x} y={y} items={items} onClose={onClose} keepOpen={keepOpen} />
}

function MenuPanel({ x, y, items, onClose, alignRight, keepOpen }: { x: number; y: number; items: MenuEntry[]; onClose: () => void; alignRight?: number; keepOpen?: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ left: x, top: y })
  const [open, setOpen] = useState<number>()
  const [subAt, setSubAt] = useState<{ x: number; y: number; flip: number }>()

  // Keep the flyout inside the window, flipping like Windows does.
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const r = el.getBoundingClientRect()
    let left = x
    let top = y
    if (left + r.width > innerWidth - 4) left = alignRight !== undefined ? alignRight - r.width : innerWidth - r.width - 4
    if (top + r.height > innerHeight - 4) top = Math.max(4, innerHeight - r.height - 4)
    setPos({ left: Math.max(4, left), top })
  }, [x, y, alignRight])

  return (
    <div ref={ref} className="menu" style={pos} onMouseDown={(e) => e.stopPropagation()} onContextMenu={(e) => e.preventDefault()}>
      {items.map((it, i) => {
        if (it.type === 'sep') return <div key={i} className="menu-sep" />
        if (it.type === 'head') return <div key={i} className="menu-head">{it.label}</div>
        const hasSub = !!it.submenu?.length
        return (
          <button
            key={i}
            className={`menu-item ${open === i ? 'open' : ''}`}
            disabled={it.disabled}
            onMouseEnter={(e) => {
              if (hasSub && !it.disabled) {
                const r = e.currentTarget.getBoundingClientRect()
                setSubAt({ x: r.right + 2, y: r.top - 5, flip: r.left - 2 })
                setOpen(i)
              } else setOpen(undefined)
            }}
            onClick={() => {
              if (hasSub || it.disabled) return
              it.onClick?.()
              if (!keepOpen) onClose()
            }}
          >
            <span className="glyph">{it.glyph ?? ''}</span>
            {it.label}
            {it.checked && <span className="check-mark">{'\uE73E'}</span>}
            {hasSub && <span className="chev">{'\uE76C'}</span>}
          </button>
        )
      })}
      {open !== undefined && subAt && items[open]?.type === 'item' && (
        <MenuPanel x={subAt.x} y={subAt.y} alignRight={subAt.flip} items={(items[open] as { submenu: MenuEntry[] }).submenu} onClose={onClose} />
      )}
    </div>
  )
}
