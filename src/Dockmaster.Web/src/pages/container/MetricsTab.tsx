import type { ContainerMetrics } from '../../lib/api'
import { bytes } from '../../lib/fmt'
import { cores, memorySeries, rate, SERIES, useMetricsHistory } from '../../lib/chart'
import { LineChart } from '../../components/LineChart'
import { RangePicker } from '../../components/MetricsRange'

/**
 * The last hours of one container: CPU and memory against its limits, and traffic. By name, so the chart goes on over
 * a pull & redeploy; the minutes it was stopped are a gap.
 */
export default function MetricsTab({ agentId, name }: { agentId: number; name: string }) {
  const { hours, setHours, data, error } = useMetricsHistory<ContainerMetrics>(`/agents/${agentId}/containers/${encodeURIComponent(name)}/metrics`)
  const points = data?.points ?? []
  const minutes = points.map(p => p[0])
  const newest = points[points.length - 1]
  // running now: its last minute is one of the last two
  const last = data && newest && newest[0] >= data.to - 2 ? newest : undefined
  // against the limit it has now; minutes from before it had one stay out of that chart
  const limited = !!newest && newest[2] > 0
  const memLimit = newest && newest[4] > 0 ? newest[4] : 0
  const cpu = limited
    ? { values: points.map(p => p[2] > 0 ? p[1] / p[2] : null), format: (v: number) => `${Math.round(v)}%`, max: 100, marks: [{ value: 100, label: 'limit' }] }
    : { values: points.map(p => p[1] / 100), format: (v: number) => cores(v), max: undefined, marks: [] }
  const cpuNow = last ? cpu.values[cpu.values.length - 1] : null

  return (
    <>
      <RangePicker hours={hours} onChange={setHours} note="one point a minute · kept for 24 h" />
      {error && <p className="error">{error}</p>}
      {!data && !error && <p className="muted">…</p>}
      {data && points.length === 0 && <p className="muted">No data yet: a point is collected every minute while the container runs.</p>}
      {data && points.length > 0 && (
        <div className="card metrics-card">
          <div className="chart-grid-3">
            <LineChart title={limited ? 'CPU, % of the limit' : 'CPU, cores (no limit)'} note={cpuNow != null ? cpu.format(cpuNow) : undefined}
              minutes={minutes} from={data.from} to={data.to} series={[{ name: 'CPU', color: SERIES[0], values: cpu.values }]} marks={cpu.marks} max={cpu.max} format={cpu.format} />
            <LineChart title={memLimit ? 'Memory and its limit' : 'Memory (no limit)'} note={last ? bytes(last[3]) + (memLimit ? ' of ' + bytes(memLimit) : '') : undefined} binary
              minutes={minutes} from={data.from} to={data.to} series={memorySeries(points.map(p => p[3]), points.map(p => p[7]), !!last)}
              marks={memLimit ? [{ value: memLimit, label: 'limit' }] : []} format={v => bytes(v)} />
            <LineChart title="Network" note={last ? `in ${rate(last[5])}, out ${rate(last[6])}` : undefined} minutes={minutes} from={data.from} to={data.to}
              series={[{ name: 'in', color: SERIES[0], values: points.map(p => p[5]) }, { name: 'out', color: SERIES[1], values: points.map(p => p[6]) }]} format={rate} binary />
          </div>
        </div>
      )}
    </>
  )
}
