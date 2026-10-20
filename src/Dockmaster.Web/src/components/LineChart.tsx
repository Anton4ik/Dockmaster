import { useEffect, useRef, useState, type PointerEvent } from 'react'
import { clock } from '../lib/chart'

/** One line of a chart: its values, one per minute of `minutes` (null = nothing then); `legend`: its name with more to it. */
export interface ChartSeries { name: string; color: string; values: (number | null)[]; legend?: string }
/** A horizontal line across the plot with a word at its right end: a limit, a threshold. */
export interface ChartMark { value: number; label: string }

const PAD = { top: 8, right: 8, bottom: 18, left: 44 }

/**
 * A small time chart: minutes along the bottom, 2px lines (a light wash under a single one), hairline grid, and a
 * crosshair with every series' value at the pointer. A gap of more than two minutes breaks the line.
 */
export function LineChart({ title, note, minutes, from, to, series, marks = [], min = 0, max, format, height = 120, step = false, binary = false }: {
  title: string
  /** under the title: the latest value, or what the chart shows */
  note?: string
  minutes: number[]
  /** the time range shown, in unix minutes */
  from: number; to: number
  series: ChartSeries[]
  marks?: ChartMark[]
  min?: number
  /** the top of the scale at least; the data or a mark may raise it */
  max?: number
  format: (v: number) => string
  height?: number
  /** a count that jumps (replicas): steps instead of slopes */
  step?: boolean
  /** bytes: the top of the scale is a power of two (512 MB, 1 GB), not of ten */
  binary?: boolean
}) {
  const box = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(300)
  // the minute under the pointer: an index would point elsewhere (or nowhere) once the data changes
  const [hoverMinute, setHoverMinute] = useState<number | null>(null)
  const found = hoverMinute === null ? -1 : minutes.indexOf(hoverMinute)
  const hover = found < 0 ? null : found
  useEffect(() => {
    const el = box.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(Math.max(120, el.clientWidth)))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const values = series.flatMap(s => s.values.filter((v): v is number => v !== null))
  const peak = Math.max(max ?? 0, ...values, ...marks.map(m => m.value))
  const top = peak > min ? niceMax(peak, binary) : min + (binary ? 1024 : 1)
  const plotW = width - PAD.left - PAD.right, plotH = height - PAD.top - PAD.bottom
  const x = (m: number) => PAD.left + ((m - from) / Math.max(1, to - from)) * plotW
  const y = (v: number) => PAD.top + plotH - ((v - min) / (top - min)) * plotH

  /** The stretches without a gap, as points. */
  function runs(vals: (number | null)[]) {
    const out: { m: number; v: number }[][] = []
    let cur: { m: number; v: number }[] | null = null
    vals.forEach((v, i) => {
      if (v === null) { cur = null; return }
      const m = minutes[i]
      if (!cur || m - cur[cur.length - 1].m > 2) out.push(cur = [])
      cur.push({ m, v })
    })
    return out
  }
  const line = (run: { m: number; v: number }[]) => run.map((p, i) =>
    i === 0 ? `M${x(p.m).toFixed(1)},${y(p.v).toFixed(1)}` : step ? `H${x(p.m).toFixed(1)}V${y(p.v).toFixed(1)}` : `L${x(p.m).toFixed(1)},${y(p.v).toFixed(1)}`).join('')
  const path = (vals: (number | null)[]) => runs(vals).map(line).join('')
  const area = (vals: (number | null)[]) => runs(vals).map(run =>
    `${line(run)}L${x(run[run.length - 1].m).toFixed(1)},${y(min).toFixed(1)}L${x(run[0].m).toFixed(1)},${y(min).toFixed(1)}Z`).join('')

  function onMove(e: PointerEvent<SVGSVGElement>) {
    if (minutes.length === 0) return
    const r = e.currentTarget.getBoundingClientRect()
    const m = from + ((e.clientX - r.left - PAD.left) / plotW) * (to - from)
    let best = 0
    for (let i = 1; i < minutes.length; i++) if (Math.abs(minutes[i] - m) < Math.abs(minutes[best] - m)) best = i
    setHoverMinute(Math.abs(minutes[best] - m) <= Math.max(2, (to - from) / 60) ? minutes[best] : null)
  }

  const ticks = [min, (min + top) / 2, top]
  const hoverX = hover !== null ? x(minutes[hover]) : 0
  return (
    <div className="chart" ref={box}>
      <div className="chart-head">
        <span className="chart-title">{title}</span>
        {note && <span className="mini">{note}</span>}
      </div>
      {series.length > 1 && (
        <div className="chart-legend">
          {series.map(s => <span key={s.name}><i style={{ background: s.color }} />{s.legend ?? s.name}</span>)}
        </div>
      )}
      <svg width={width} height={height} role="img" aria-label={`${title}${note ? ': ' + note : ''}`}
        onPointerMove={onMove} onPointerLeave={() => setHoverMinute(null)}>
        {ticks.map((t, i) => (
          <g key={i}>
            <line className="chart-grid" x1={PAD.left} x2={width - PAD.right} y1={y(t)} y2={y(t)} />
            <text className="chart-tick" x={PAD.left - 6} y={y(t) + 4} textAnchor="end">{format(t)}</text>
          </g>
        ))}
        <text className="chart-tick" x={PAD.left} y={height - 4}>{clock(from)}</text>
        <text className="chart-tick" x={width - PAD.right} y={height - 4} textAnchor="end">{clock(to)}</text>
        {marks.map(mk => (
          <g key={mk.label}>
            <line className="chart-mark" x1={PAD.left} x2={width - PAD.right} y1={y(mk.value)} y2={y(mk.value)} />
            <text className="chart-tick" x={width - PAD.right - 2} y={y(mk.value) - 3} textAnchor="end">{mk.label}</text>
          </g>
        ))}
        {series.length === 1 && <path d={area(series[0].values)} fill={series[0].color} opacity={0.1} />}
        {series.map(s => <path key={s.name} d={path(s.values)} fill="none" stroke={s.color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />)}
        {/* a point on its own has no line to show it */}
        {series.map(s => runs(s.values).filter(r => r.length === 1).map(r => <circle key={s.name + r[0].m} cx={x(r[0].m)} cy={y(r[0].v)} r={3} fill={s.color} />))}
        {hover !== null && (
          <g>
            <line className="chart-cross" x1={hoverX} x2={hoverX} y1={PAD.top} y2={PAD.top + plotH} />
            {series.map(s => s.values[hover] != null && (
              <circle key={s.name} cx={hoverX} cy={y(s.values[hover]!)} r={4} fill={s.color} className="chart-dot" />
            ))}
          </g>
        )}
      </svg>
      {hover !== null && (
        <div className="chart-tip" style={{ left: Math.min(hoverX + 10, width - 150), top: 30 }}>
          <div className="mini">{clock(minutes[hover])}</div>
          {series.map(s => (
            <div key={s.name} className="chart-tip-row">
              <i style={{ background: s.color }} /><b>{s.values[hover] == null ? '—' : format(s.values[hover]!)}</b>
              {series.length > 1 && <span className="muted">{s.name}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** A round number at or above the top value: 1, 2, 2.5, 5 × a power of ten, or the next power of two for bytes. */
function niceMax(v: number, binary: boolean) {
  if (v <= 0) return 1
  if (binary) return 2 ** Math.ceil(Math.log2(v))
  const p = 10 ** Math.floor(Math.log10(v))
  return ([1, 2, 2.5, 5, 10].find(f => f * p >= v) ?? 10) * p
}
