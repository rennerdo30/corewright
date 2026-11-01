import { useEffect, useRef, useState } from 'react'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'

interface Props {
  time: number[]
  values: number[]
  /** A CSS custom property such as "--chart-1"; resolved against the current theme. */
  colorVar: string
  height?: number
  max?: number
  format: (v: number) => string
}

const css = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim()

function withAlpha(color: string, alpha: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(color)
  if (!m) return color
  const n = parseInt(m[1], 16)
  return `rgba(${n >> 16}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`
}

/** Re-renders charts when Windows switches between light and dark. */
function useScheme(): boolean {
  const q = matchMedia('(prefers-color-scheme: dark)')
  const [dark, setDark] = useState(q.matches)
  useEffect(() => {
    const on = () => setDark(q.matches)
    q.addEventListener('change', on)
    return () => q.removeEventListener('change', on)
  }, [q])
  return dark
}

/** A small live area chart. uPlot is created once per theme and only gets new data afterwards. */
export function Chart({ time, values, colorVar, height = 120, max, format }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const plot = useRef<uPlot | null>(null)
  const formatRef = useRef(format)
  formatRef.current = format
  const dark = useScheme()

  useEffect(() => {
    const el = host.current
    if (!el) return
    const color = css(colorVar) || '#60cdff'
    const axis = css('--text-2')
    const grid = css('--divider')
    const opts: uPlot.Options = {
      width: el.clientWidth,
      height,
      legend: { show: false },
      cursor: { points: { size: 6 }, drag: { x: false, y: false } },
      scales: { x: { time: true }, y: { range: (_u, _min, dataMax) => [0, max ?? Math.max(dataMax * 1.15, 1)] } },
      axes: [
        { stroke: axis, grid: { stroke: grid, width: 1 }, ticks: { show: false }, font: '12px "Segoe UI Variable Text", "Segoe UI"' },
        {
          stroke: axis,
          grid: { stroke: grid, width: 1 },
          ticks: { show: false },
          size: 64,
          font: '12px "Segoe UI Variable Text", "Segoe UI"',
          values: (_u, splits) => splits.map((v) => formatRef.current(v)),
        },
      ],
      series: [
        {},
        { stroke: color, width: 2, fill: withAlpha(color, 0.16), points: { show: false }, value: (_u, v) => (v == null ? '' : formatRef.current(v)) },
      ],
    }
    plot.current = new uPlot(opts, [[], []], el)
    const ro = new ResizeObserver(() => plot.current?.setSize({ width: el.clientWidth, height }))
    ro.observe(el)
    return () => {
      ro.disconnect()
      plot.current?.destroy()
      plot.current = null
    }
  }, [colorVar, height, max, dark])

  useEffect(() => {
    plot.current?.setData([time.map((t) => t / 1000), values])
  }, [time, values, dark])

  return <div ref={host} className="chart" />
}
