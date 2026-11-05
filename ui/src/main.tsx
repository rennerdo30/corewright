import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { applyAccent } from './appearance'
import './styles.css'
import './styles-r2.css'
import './styles-r3.css'
import './styles-r4.css'
import './styles-updates.css'
import './styles-r5.css'
import './styles-history.css'

applyAccent()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
