import { isHost, post } from './host'

/**
 * Appearance (owner, 2026-11-02: "we also need some theme settings! at least some colors, light mode, and custom").
 * Light/dark is applied by the window host (WebView2 preferred colour scheme), so every stylesheet's
 * prefers-color-scheme rules follow it. The accent is applied here: the Windows accent, a preset or a custom colour.
 */
export type Scheme = 'system' | 'light' | 'dark'

export interface Appearance {
  scheme: Scheme
  /** "windows" or a #rrggbb colour. */
  accent: string
}

export const PRESETS: { name: string; color: string }[] = [
  { name: 'Blue', color: '#0078d4' },
  { name: 'Teal', color: '#038387' },
  { name: 'Green', color: '#107c10' },
  { name: 'Purple', color: '#8764b8' },
  { name: 'Orchid', color: '#b146c2' },
  { name: 'Pink', color: '#e3008c' },
  { name: 'Red', color: '#d13438' },
  { name: 'Orange', color: '#ca5010' },
  { name: 'Gold', color: '#c19c00' },
  { name: 'Graphite', color: '#69797e' },
]

const KEY = 'corewright.appearance'
const listeners = new Set<() => void>()

export function getAppearance(): Appearance {
  let stored: Partial<Appearance> = {}
  try {
    stored = JSON.parse(localStorage.getItem(KEY) ?? '{}') as Partial<Appearance>
  } catch {
    /* defaults */
  }
  const hostScheme = window.__COREWRIGHT__?.scheme as Scheme | undefined
  return {
    scheme: hostScheme ?? stored.scheme ?? 'system',
    accent: typeof stored.accent === 'string' && (stored.accent === 'windows' || /^#[0-9a-f]{6}$/i.test(stored.accent)) ? stored.accent : 'windows',
  }
}

export function setAppearance(next: Appearance) {
  localStorage.setItem(KEY, JSON.stringify(next))
  if (window.__COREWRIGHT__) window.__COREWRIGHT__.scheme = next.scheme
  if (isHost) post({ type: 'theme-set', scheme: next.scheme })
  applyAccent()
  listeners.forEach((l) => l())
}

export function onAppearanceChange(l: () => void) {
  listeners.add(l)
  return () => listeners.delete(l)
}

const dark = matchMedia('(prefers-color-scheme: dark)')

type Rgb = [number, number, number]
const hex = (c: string): Rgb => [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16)) as Rgb
const css = ([r, g, b]: Rgb) => `#${[r, g, b].map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0')).join('')}`
const mix = (a: Rgb, b: Rgb, t: number): Rgb => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]
const luminance = ([r, g, b]: Rgb) => {
  const f = (v: number) => {
    const s = v / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)
}

/** Windows-like shades of one colour: dark mode uses a lighter shade (Light 2), light mode a darker one (Dark 1). */
function shades(color: string, isDark: boolean) {
  const base = hex(color)
  const white: Rgb = [255, 255, 255]
  const black: Rgb = [0, 0, 0]
  const accent = isDark ? mix(base, white, 0.38) : luminance(base) > 0.25 ? mix(base, black, 0.3) : base
  const hover = isDark ? mix(base, white, 0.25) : mix(base, black, 0.15)
  return { accent: css(accent), hover: css(hover), on: luminance(accent) > 0.4 ? '#000000' : '#ffffff' }
}

export function applyAccent() {
  const s = document.documentElement.style
  const { accent } = getAppearance()
  const palette = window.__COREWRIGHT__?.accent
  if (accent === 'windows') {
    if (!palette || palette.length < 7) {
      s.removeProperty('--accent')
      s.removeProperty('--accent-hover')
      s.removeProperty('--on-accent')
      return
    }
    // The host passes the Windows accent palette (light 3..1, base, dark 1..3); use the shades WinUI uses.
    s.setProperty('--accent', dark.matches ? palette[1] : palette[4])
    s.setProperty('--accent-hover', dark.matches ? palette[2] : palette[3])
    s.setProperty('--on-accent', dark.matches ? '#000000' : '#ffffff')
    return
  }
  const sh = shades(accent, dark.matches)
  s.setProperty('--accent', sh.accent)
  s.setProperty('--accent-hover', sh.hover)
  s.setProperty('--on-accent', sh.on)
}

/** The colour a swatch shows for the Windows accent. */
export const windowsAccent = () => window.__COREWRIGHT__?.accent?.[3]

dark.addEventListener('change', applyAccent)
