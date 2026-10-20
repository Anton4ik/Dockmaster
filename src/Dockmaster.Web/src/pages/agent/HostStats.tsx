import type { ReactNode } from 'react'
import type { Agent, Container } from '../../lib/api'
import { bytes, duration, isProblem, limitShareClass, limitShareHint, pct, USAGE, type Thresholds } from '../../lib/fmt'
import { StatTile, UsageBar } from '../../components/StatTile'

/** Host usage tiles: CPU, memory, disk with bars, then containers, problems and uptime. */
export default function HostStats({ agent, containers }: { agent: Agent; containers: Container[] }) {
  const s = agent.stats
  if (!s) return null
  const memPct = s.memoryTotal ? 100 * s.memoryUsed / s.memoryTotal : 0
  const diskPct = s.diskTotal ? 100 * s.diskUsed / s.diskTotal : 0
  const up = containers.filter(c => c.state === 'running')
  const problems = containers.filter(isProblem).length
  const sum = (f: (c: Container) => number) => up.reduce((n, c) => n + f(c), 0)
  return (
    <div className="stats">
      <Meter label="CPU" detail={`load ${s.load1.toFixed(2)}`} value={pct(s.cpuPercent)} percent={s.cpuPercent} limits={USAGE.cpu}>
        {agent.cpuLimits && <LimitsLine kind="cpu" total={sum(c => c.cpuLimit)} capacity={agent.cpus} unlimited={up.filter(c => !(c.cpuLimit > 0)).length} fmt={n => `${+n.toFixed(2)} cores`} of={`${agent.cpus} cores`} />}
      </Meter>
      {/* cores and memory size are in the header already: the tiles show how much of it is used */}
      <Meter label="Memory" detail={pct(memPct)} value={bytes(s.memoryUsed)} percent={memPct} limits={USAGE.memory}>
        <LimitsLine kind="memory" total={sum(c => c.memoryLimit)} capacity={s.memoryTotal} unlimited={up.filter(c => !(c.memoryLimit > 0)).length} fmt={bytes} of={bytes(s.memoryTotal)} />
      </Meter>
      <Meter label="Disk" detail={`${pct(diskPct)} of ${bytes(s.diskTotal)}`} value={bytes(s.diskUsed)} percent={diskPct} limits={USAGE.disk} />
      {/* jobs waiting for their next run are neither running nor stopped: counted apart */}
      <StatTile label="Containers" value={up.length} of={containers.filter(c => !c.jobInfo || c.state === 'running').length}
        note={containers.some(c => c.jobInfo) ? `running · ${containers.filter(c => c.jobInfo).length} jobs on a schedule` : 'running'} />
      <StatTile label="Problems" value={problems} tone={problems > 0 ? 'bad' : 'ok'} note="unhealthy / crash-looping / a job's run failed" />
      <StatTile label="Uptime" value={duration(s.uptimeSeconds)} note="host" />
    </div>
  )
}

/** A usage tile: the detail next to the label, a bar under the value, then `children` (the limits line). */
function Meter({ label, detail, value, percent, limits, children }: { label: string; detail: string; value: string; percent: number; limits: Thresholds; children?: ReactNode }) {
  return (
    <StatTile label={label} aside={<span>{detail}</span>} value={value}>
      <UsageBar percent={percent} limits={limits} />
      {children}
    </StatTile>
  )
}

/**
 * What the running containers' limits add up to against what the host has. Limits above the host are allowed by
 * docker (it is overcommit, not a reservation) but then they no longer protect the host from one greedy container.
 */
function LimitsLine({ kind, total, capacity, unlimited, fmt, of }: {
  kind: 'memory' | 'cpu'; total: number; capacity: number; unlimited: number; fmt: (n: number) => string; of: string
}) {
  if (total <= 0) return <div className="mini" title="none of the running containers has a limit">no limits</div>
  const share = capacity > 0 ? 100 * total / capacity : 0
  const over = total - capacity
  // the tile stays short; the numbers behind the percentage are in the tooltip
  const detail = `limits of the running containers add up to ${fmt(total)} of the host's ${of}` +
    (over > 0 ? ` — ${fmt(over)} more than it has` : '') + (unlimited > 0 ? `\n${unlimited} container(s) without a limit` : '') +
    `\n${limitShareHint(kind)}`
  return (
    <div className={'mini limits-line ' + limitShareClass(share, kind)} title={detail}>
      limits {Math.round(share)}%{over > 0 && ` (+${fmt(over)})`}
    </div>
  )
}
