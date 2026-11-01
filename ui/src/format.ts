export function bytes(n: number, digits = 1): string {
  if (!Number.isFinite(n) || n <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024
    i++
  }
  return `${n.toFixed(i === 0 ? 0 : digits)} ${units[i]}`
}

export function rate(n: number): string {
  return n < 1 ? '–' : `${bytes(n)}/s`
}

export function percent(n: number, digits = 1): string {
  return `${n.toFixed(digits)}%`
}

export function time(ms: number): string {
  return new Date(ms).toLocaleTimeString(undefined, { hour12: false })
}

export function newId(): string {
  return `r-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`
}
