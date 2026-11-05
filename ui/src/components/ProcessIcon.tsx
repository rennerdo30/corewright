import { useEffect, useState } from 'react'
import { iconUrl } from '../host'

/**
 * A process's icon. The window host serves the exe's own icon, or Windows' default application icon for programs that
 * have none (svchost.exe, most services). Without a path (System, Registry), outside the window or when loading fails,
 * a Fluent app glyph takes its place, never a broken image.
 */
export function ProcessIcon({ path, size = 16 }: { path?: string; size?: number }) {
  const url = iconUrl(path)
  const [failed, setFailed] = useState(false)
  useEffect(() => setFailed(false), [url])
  const box = size === 16 ? undefined : { width: size, height: size, fontSize: Math.round(size * 0.8) }
  if (!url || failed)
    return (
      <span className="picon glyph picon-default" style={box} aria-hidden>
        {''}
      </span>
    )
  return <img className="picon" src={url} width={size} height={size} style={box} alt="" onError={() => setFailed(true)} />
}
