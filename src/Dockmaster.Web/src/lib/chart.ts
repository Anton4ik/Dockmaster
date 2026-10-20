// Shared by the charts.

import { useCallback, useRef, useState } from 'react'
import { api, errorText } from './api'
import { bytes } from './fmt'
import type { ChartSeries } from '../components/LineChart'
import { usePoll, useStoredState } from './hooks'

// categorical slots 1 and 2 of the chart palette, stepped for the dark panel (validated together)
export const SERIES = ['#3987e5', '#d95926']

/**
 * The memory chart's lines: the minute's average, and its peak when the servers report one — the most it held in
 * the minute, spikes between two readings too (what the OOM killer measures against the limit). `running`: the
 * legend says the last minute's peak.
 */
export function memorySeries(average: number[], peak: (number | undefined)[], running: boolean): ChartSeries[] {
  const peaks = peak.map(v => v && v > 0 ? v : null)
  if (!peaks.some(v => v !== null)) return [{ name: 'memory', color: SERIES[0], values: average }]
  const last = peaks[peaks.length - 1]
  return [{ name: 'average', color: SERIES[0], values: average },
    { name: 'peak', color: SERIES[1], values: peaks, legend: running && last ? `peak ${bytes(last)}` : 'peak' }]
}

/** The ranges a metrics history can show, in hours. */
export const RANGES = [1, 6, 24] as const
const HOURS = { parse: (s: string) => Number(s) || 6, stringify: String }

/**
 * The metrics history at `path` for the last hours the viewer picked (remembered, the same for every chart page),
 * again every minute.
 */
export function useMetricsHistory<T>(path: string) {
  const [hours, setHours] = useStoredState<number>('dm.metrics.hours', 6, HOURS)
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  // only the answer to the latest request counts: a slow one for another range must not win
  const latest = useRef(0)
  const load = useCallback(() => {
    const n = ++latest.current
    api<T>(`${path}?hours=${hours}`)
      .then(d => { if (n === latest.current) { setData(d); setError(null) } })
      .catch(e => { if (n === latest.current) setError(errorText(e)) })
  }, [path, hours])
  usePoll(load, 60_000)
  return { hours, setHours, data, error }
}

/** A number of cores: "0.25", "1.5", "12". */
export const cores = (v: number) => v >= 10 ? `${Math.round(v)}` : v >= 1 ? v.toFixed(1) : v.toFixed(2)

/** "1.2 MB/s", or "0". */
export const rate = (v: number) => `${bytes(v)}/s`.replace(/^0\/s$/, '0')

/** "14:05" in the viewer's time. */
export const clock = (minute: number) => new Date(minute * 60_000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
