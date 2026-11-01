import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles.css'
import './styles-r2.css'
import './styles-r3.css'
import './styles-r4.css'
import './styles-updates.css'
import './styles-r5.css'

// The dashboard host passes the Windows accent palette (light 3..1, base, dark 1..3); use the shades WinUI uses.
const palette = window.__COREWRIGHT__?.accent
const scheme = matchMedia('(prefers-color-scheme: dark)')
function applyAccent() {
  if (!palette || palette.length < 7) return
  const s = document.documentElement.style
  if (scheme.matches) {
    s.setProperty('--accent', palette[1])
    s.setProperty('--accent-hover', palette[2])
    s.setProperty('--on-accent', '#000000')
  } else {
    s.setProperty('--accent', palette[4])
    s.setProperty('--accent-hover', palette[3])
    s.setProperty('--on-accent', '#ffffff')
  }
}
applyAccent()
scheme.addEventListener('change', applyAccent)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
