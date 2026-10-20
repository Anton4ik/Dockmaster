import type { Container } from '../../lib/api'
import { bytes, pct } from '../../lib/fmt'
import { StatTile } from '../../components/StatTile'

/** What the project's running containers use right now, added up over all its servers. */
export default function ProjectUsage({ containers }: { containers: Container[] }) {
  const running = containers.filter(c => c.state === 'running')
  if (running.length === 0) return null
  const sum = (f: (c: Container) => number) => running.reduce((n, c) => n + f(c), 0)
  const memory = sum(c => c.memoryUsage)
  const memLimit = sum(c => c.memoryLimit)
  const cpuLimit = sum(c => c.cpuLimit)
  const limited = (f: (c: Container) => number) => running.filter(c => f(c) > 0).length
  const servers = new Set(running.map(c => c.agentId)).size
  // jobs waiting for their next run are neither running nor stopped: counted apart
  const jobs = containers.filter(c => c.jobInfo).length
  const total = containers.filter(c => !c.jobInfo || c.state === 'running').length
  const stopped = total - running.length

  return (
    <div className="stats">
      <StatTile label="CPU" value={pct(sum(c => c.cpuPercent))}
        note={limitText(limited(c => c.cpuLimit), running.length, `limit ${Math.round(cpuLimit * 100) / 100} cores · 100% = one core`)} />
      <StatTile label="Memory" value={bytes(memory)}
        note={limitText(limited(c => c.memoryLimit), running.length, `of ${bytes(memLimit)} · ${pct(100 * memory / memLimit)}`)} />
      <StatTile label="Network" value={`${bytes(sum(c => c.netRx))} / ${bytes(sum(c => c.netTx))}`} note="received / sent" />
      <StatTile label="Containers" value={running.length} of={total}
        note={[servers > 1 ? `running on ${servers} servers` : stopped > 0 ? `${stopped} not running` : 'running', jobs ? `${jobs} job${jobs > 1 ? 's' : ''} on a schedule` : ''].filter(Boolean).join(' · ')} />
    </div>
  )
}

/** the limits' total when every running container has one, else how many do */
function limitText(limited: number, total: number, all: string) {
  if (limited === 0) return 'no limit'
  return limited === total ? all : `limit on ${limited} of ${total}`
}
