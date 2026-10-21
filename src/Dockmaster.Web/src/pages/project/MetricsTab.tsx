import { Link } from 'react-router-dom'
import type { Container, ProjectMetrics, ServicePoint, ServiceSummary } from '../../lib/api'
import { bytes } from '../../lib/fmt'
import { LineChart } from '../../components/LineChart'
import { RangePicker } from '../../components/MetricsRange'
import { cores, memorySeries, rate, SERIES, useMetricsHistory } from '../../lib/chart'
import EventList from '../../components/EventList'

/**
 * The last hours of every service: CPU against its limit (where the autoscaler starts one more replica), memory
 * against its limit, traffic, and how many replicas ran — all servers added up. Then what happened in the project.
 */
export default function MetricsTab({ projectId, services, containers }: { projectId: number; services: ServiceSummary[]; containers: Container[] }) {
  const { hours, setHours, data, error } = useMetricsHistory<ProjectMetrics>(`/projects/${projectId}/metrics`)

  return (
    <>
      <RangePicker hours={hours} onChange={setHours} note="one point a minute, all servers added up · kept for 24 h" />
      {error && <p className="error">{error}</p>}
      {!data && !error && <p className="muted">…</p>}
      {data && data.services.length === 0 && <p className="muted">No data yet: points are collected every minute while the services run.</p>}
      {data && data.services.map(s => (
        <ServiceCharts key={s.service} name={s.service} points={s.points} from={data.from} to={data.to} spec={services.find(x => x.name === s.service)}
          containers={containers.filter(c => c.service === s.service)} />
      ))}
      <h2>Events</h2>
      <EventList projectId={projectId} compact more empty="Nothing happened in this project yet." />
    </>
  )
}

function ServiceCharts({ name, points, from, to, spec, containers }: { name: string; points: ServicePoint[]; from: number; to: number; spec?: ServiceSummary; containers: Container[] }) {
  const minutes = points.map(p => p[0])
  const newest = points[points.length - 1]
  // running now: its last minute is one of the last two
  const last = newest && newest[0] >= to - 2 ? newest : undefined
  // against the limit it has now; minutes from before it had one stay out of that chart
  const limited = !!newest && newest[3] > 0
  const memLimit = newest && newest[5] > 0 ? newest[5] : 0
  const auto = spec?.autoscale
  const cpu = limited
    ? { values: points.map(p => p[3] > 0 ? p[2] / p[3] : null), format: (v: number) => `${Math.round(v)}%`, max: 100, marks: auto ? [{ value: auto.cpu, label: `one more at ${auto.cpu}%` }] : [{ value: 100, label: 'limit' }] }
    : { values: points.map(p => p[2] / 100), format: (v: number) => cores(v), max: undefined, marks: [] }
  const cpuNow = last ? cpu.values[cpu.values.length - 1] : null
  return (
    <div className="card metrics-card">
      <h3>
        <ServiceName name={name} containers={containers} />
        <span className="mini">
          {last ? `${fmtReplicas(last[1])} replica${last[1] === 1 ? '' : 's'} now` : 'not running'}
          {auto && ` · autoscale ${auto.mode}, ${spec!.replicas}–${auto.max}`}
        </span>
      </h3>
      <div className="chart-grid-4">
        <LineChart title={limited ? 'CPU, % of the limit' : 'CPU, cores (no limit)'} note={cpuNow != null ? cpu.format(cpuNow) : undefined}
          minutes={minutes} from={from} to={to} series={[{ name: 'CPU', color: SERIES[0], values: cpu.values }]} marks={cpu.marks} max={cpu.max} format={cpu.format} />
        <LineChart title={memLimit ? 'Memory and its limit' : 'Memory (no limit)'} note={last ? bytes(last[4]) + (memLimit ? ' of ' + bytes(memLimit) : '') : undefined} binary
          minutes={minutes} from={from} to={to} series={memorySeries(points.map(p => p[4]), points.map(p => p[8]), !!last)}
          marks={memLimit ? [{ value: memLimit, label: 'limit' }] : []} format={v => bytes(v)} />
        <LineChart title="Network" note={last ? `in ${rate(last[6])}, out ${rate(last[7])}` : undefined} minutes={minutes} from={from} to={to}
          series={[{ name: 'in', color: SERIES[0], values: points.map(p => p[6]) }, { name: 'out', color: SERIES[1], values: points.map(p => p[7]) }]} format={rate} binary />
        <LineChart title="Replicas" note={last ? fmtReplicas(last[1]) : undefined} minutes={minutes} from={from} to={to} step
          series={[{ name: 'replicas', color: SERIES[0], values: points.map(p => p[1]) }]} max={auto ? auto.max : undefined}
          marks={auto ? [{ value: auto.max, label: 'max' }] : []} format={fmtReplicas} />
      </div>
    </div>
  )
}

/** The service's name as a link to its container page; with several replicas, a link to each of them after it. */
function ServiceName({ name, containers }: { name: string; containers: Container[] }) {
  const running = containers.filter(c => c.state === 'running')
  const shown = running.length > 0 ? running : containers
  const href = (c: Container) => `/agents/${c.agentId}/containers/${encodeURIComponent(c.name)}`
  if (shown.length === 1) return <Link to={href(shown[0])} title="open the container: logs, console, its own metrics">{name}</Link>
  return (
    <span className="replica-links">
      <span>{name}</span>
      {shown.map(c => <Link key={`${c.agentId}/${c.id}`} className="mini" to={href(c)} title={`open ${c.name}`}>{c.name.slice(c.name.lastIndexOf('-') + 1)}</Link>)}
    </span>
  )
}

const fmtReplicas = (v: number) => Number.isInteger(v) ? String(v) : v.toFixed(1)
