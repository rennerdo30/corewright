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
  /** An optional second series drawn over the first (usage history: one program's share). */
  values2?: (number | null)[]
  colorVar2?: string
  /** Bars instead of an area: for hourly buckets. */
  bars?: boolean
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
export function Chart({ time, values, colorVar, height = 120, max, format, values2, colorVar2, bars }: Props) {
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
        {
          stroke: color,
          width: bars ? 0 : 2,
          fill: withAlpha(color, bars ? 0.45 : 0.16),
          points: { show: false },
          paths: bars ? uPlot.paths.bars!({ size: [0.8, 40] }) : undefined,
          value: (_u, v) => (v == null ? '' : formatRef.current(v)),
        },
        ...(colorVar2
          ? [{
              stroke: css(colorVar2) || '#f7630c',
              width: bars ? 0 : 2,
              fill: withAlpha(css(colorVar2) || '#f7630c', bars ? 0.9 : 0.2),
              points: { show: false },
              paths: bars ? uPlot.paths.bars!({ size: [0.5, 24] }) : undefined,
              value: (_u: uPlot, v: number | null) => (v == null ? '' : formatRef.current(v)),
            }]
          : []),
      ],
    }
    plot.current = new uPlot(opts, colorVar2 ? [[], [], []] : [[], []], el)
    const ro = new ResizeObserver(() => plot.current?.setSize({ width: el.clientWidth, height }))
    ro.observe(el)
    return () => {
      ro.disconnect()
      plot.current?.destroy()
      plot.current = null
    }
  }, [colorVar, colorVar2, bars, height, max, dark])

  useEffect(() => {
    const t = time.map((x) => x / 1000)
    plot.current?.setData(colorVar2 ? [t, values, values2 ?? values.map(() => null)] : [t, values])
  }, [time, values, values2, colorVar2, dark])

  return <div ref={host} className="chart" />
}

/** 1, 2 or 5 × 10^n at or above v, so the top line is a round number with some headroom. */
export function niceCeil(v: number): number {
  if (v <= 0) return 1
  const p = 10 ** Math.floor(Math.log10(v))
  for (const m of [1, 2, 5, 10]) if (m * p >= v) return m * p
  return 10 * p
}

interface SparkProps {
  time: number[]
  values: (number | null)[]
  colorVar: string
  /** The top of the scale: the real maximum (100 %, total memory) or a rounded peak. */
  top: number
  /** Labels for the top and middle guide lines, e.g. "100 %" / "50 %" or "61 GB" / "31 GB". */
  topLabel: string
  midLabel: string
  format: (v: number) => string
  height?: number
}

/**
 * The overview chart (owner, 2026-11-02: "with these graphs you dont get a real feeling what the maximum is and how
 * much is used"): the whole card width, a fixed scale from 0 to a labelled top line, a 50 % guide, no axis clutter.
 */
export function Spark({ time, values, colorVar, top, topLabel, midLabel, format, height = 84 }: SparkProps) {
  const host = useRef<HTMLDivElement>(null)
  const plot = useRef<uPlot | null>(null)
  const topRef = useRef(top)
  topRef.current = top
  const formatRef = useRef(format)
  formatRef.current = format
  const dark = useScheme()

  useEffect(() => {
    const el = host.current
    if (!el) return
    const color = css(colorVar) || '#60cdff'
    const opts: uPlot.Options = {
      width: el.clientWidth,
      height,
      padding: [1, 0, 0, 0],
      legend: { show: false },
      cursor: { points: { size: 6 }, drag: { x: false, y: false }, y: false },
      scales: { x: { time: true }, y: { range: () => [0, topRef.current] } },
      axes: [{ show: false }, { show: false }],
      series: [{}, { stroke: color, width: 1.5, fill: withAlpha(color, 0.22), points: { show: false }, value: (_u, v) => (v == null ? '' : formatRef.current(v)) }],
    }
    plot.current = new uPlot(opts, [[], []], el)
    const ro = new ResizeObserver(() => plot.current?.setSize({ width: el.clientWidth, height }))
    ro.observe(el)
    return () => {
      ro.disconnect()
      plot.current?.destroy()
      plot.current = null
    }
  }, [colorVar, height, dark])

  useEffect(() => {
    plot.current?.setData([time.map((t) => t / 1000), values])
  }, [time, values, top, dark])

  return (
    <div className="spark" style={{ height }}>
      <div ref={host} className="spark-plot" />
      <div className="spark-line top">
        <span>{topLabel}</span>
      </div>
      <div className="spark-line mid">
        <span>{midLabel}</span>
      </div>
    </div>
  )
}
