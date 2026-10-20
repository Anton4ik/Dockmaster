import { useCallback, useState } from 'react'
import { Link } from 'react-router-dom'
import { agentOutdated, api, type Agent, type ProjectListItem } from '../lib/api'
import { ago, bytes, duration, limitShareClass, limitShareHint, pct, USAGE } from '../lib/fmt'
import { POLL, usePoll } from '../lib/hooks'
import { canAnywhere, useMe } from '../lib/access'
import ProjectTable from '../components/ProjectTable'
import { StatTile, UsageBar } from '../components/StatTile'
import { OnlineBadge } from '../components/Status'
import EventList from '../components/EventList'

/** The overview: totals over all servers, a card per server with its usage, and every project. */
export default function Dashboard({ agents }: { agents: Agent[] }) {
  const [projects, setProjects] = useState<ProjectListItem[]>([])
  const me = useMe()
  usePoll(useCallback(() => { api<ProjectListItem[]>('/projects').then(setProjects).catch(() => {}) }, []), POLL.dashboard)

  const online = agents.filter(a => a.online)
  const running = online.reduce((n, a) => n + a.running, 0)
  const total = online.reduce((n, a) => n + a.total, 0)
  const problems = online.reduce((n, a) => n + a.unhealthy, 0)
  const failed = projects.filter(p => p.lastDeployOk === false)

  return (
    <>
      <div className="toolbar">
        <h1>Dashboard</h1>
        {agents.length > 0 && me?.isAdmin && <Link className="btn primary push-right" to="/agents/new" title="run the agent on another machine and manage it here">+ Add server</Link>}
      </div>
      {agents.length > 0 && (
        <div className="stats">
          <StatTile label="Servers online" value={online.length} of={agents.length} tone={online.length < agents.length ? 'bad' : 'ok'} />
          <StatTile label="Containers running" value={running} of={total} />
          <StatTile label="Container problems" value={problems} tone={problems > 0 ? 'bad' : 'ok'} />
          <StatTile label="Projects" value={projects.length} note={failed.length > 0 ? <FailedDeploys projects={failed} /> : undefined} bad />
        </div>
      )}
      {agents.length === 0 && (
        <div className="card">
          {me?.isAdmin
            ? <p>No servers yet. <Link to="/agents/new">Add the first server</Link> — you get a one-line <code>docker run</code> command to run on it.</p>
            : <p>No servers are given to you yet. An admin gives you a role on servers on the Users page.</p>}
        </div>
      )}
      <div className="cards">
        {agents.map(a => <AgentCard key={a.id} a={a} />)}
      </div>

      <h2>Warnings <Link className="mini" to="/events">all events</Link></h2>
      <div className="card"><EventList warnings hours={24} limit={8} empty="No warnings in the last 24 hours." /></div>

      <h2>Projects</h2>
      {projects.length === 0 ? <p className="muted">No projects.{canAnywhere(me, 'edit') && <> <Link to="/projects/new">Create one</Link>.</>}</p> : (
        <ProjectTable projects={projects} agents={agents} />
      )}
    </>
  )
}

function AgentCard({ a }: { a: Agent }) {
  const s = a.stats
  const memPct = s && s.memoryTotal > 0 ? 100 * s.memoryUsed / s.memoryTotal : 0
  const diskPct = s && s.diskTotal > 0 ? 100 * s.diskUsed / s.diskTotal : 0
  return (
    <div className={'card' + (!a.online || a.unhealthy > 0 ? ' card-bad' : '')}>
      <h3><Link to={`/agents/${a.id}`}>{a.name}</Link> <span>{agentOutdated(a) && <span className="badge warn" title="a newer agent image is available — update it from its page">outdated</span>} <OnlineBadge online={a.online} /></span></h3>
      <div className="meta">
        <span className="resource" title="CPU cores">{a.cpus} CPU</span>
        <span className="resource" title="memory">{bytes(a.memoryTotal)}</span>
        <span>{a.hostname} · {a.agentIp} · docker {a.dockerVersion}</span>
      </div>
      {a.online && s ? (
        <>
          <div className="kv"><span>CPU</span><span>{pct(s.cpuPercent)} · load {s.load1.toFixed(2)}</span></div>
          <UsageBar percent={s.cpuPercent} limits={USAGE.cpu} />
          <div className="kv"><span>Memory</span><span>{bytes(s.memoryUsed)} · {pct(memPct)}</span></div>
          <UsageBar percent={memPct} limits={USAGE.memory} />
          <div className="kv"><span>Disk</span><span>{bytes(s.diskUsed)} / {bytes(s.diskTotal)} · {pct(diskPct)}</span></div>
          <UsageBar percent={diskPct} limits={USAGE.disk} />
          <div className="kv"><span>Containers</span><span>{a.running} running / {a.total}{a.jobs ? <span className="muted" title="jobs on a schedule: they run at their time and exit"> · {a.jobs} job{a.jobs > 1 ? 's' : ''}</span> : null}{a.unhealthy > 0 && <> · <Link className="error" to={`/agents/${a.id}?problems=1`} title="show them">{a.unhealthy} problem{a.unhealthy > 1 ? 's' : ''}</Link></>}</span></div>
          <LimitsKv a={a} />
          <div className="kv"><span>Uptime</span><span>{duration(s.uptimeSeconds)}</span></div>
        </>
      ) : <div className="muted">last seen {ago(a.lastSeenAt)}</div>}
    </div>
  )
}

/** Container limits added up against the host: above 100 % the limits promise more than the machine has. */
function LimitsKv({ a }: { a: Agent }) {
  if (a.limitMemory <= 0 && a.limitCpus <= 0) return null
  const memShare = a.memoryTotal > 0 ? 100 * a.limitMemory / a.memoryTotal : 0
  const cpuShare = a.cpus > 0 ? 100 * a.limitCpus / a.cpus : 0
  return (
    <div className="kv" title={`limits of the running containers added up, against what the host has\n${limitShareHint('memory')}\n${limitShareHint('cpu')}`}>
      <span>Limits</span>
      <span>
        {a.limitMemory > 0 && <span className={limitShareClass(memShare, 'memory')}>{bytes(a.limitMemory)} ({Math.round(memShare)}%)</span>}
        {a.limitMemory > 0 && a.limitCpus > 0 && ' · '}
        {a.limitCpus > 0 && <span className={limitShareClass(cpuShare, 'cpu')}>{+a.limitCpus.toFixed(2)} cores ({Math.round(cpuShare)}%)</span>}
      </span>
    </div>
  )
}

/** The projects whose last deploy failed, each a link to its page (where the history tab has the log). */
function FailedDeploys({ projects }: { projects: ProjectListItem[] }) {
  return (
    <>
      last deploy failed:{' '}
      {projects.slice(0, 3).map((p, i) => <span key={p.id}>{i > 0 && ', '}<Link to={`/projects/${p.id}`}>{p.name}</Link></span>)}
      {projects.length > 3 && ` and ${projects.length - 3} more`}
    </>
  )
}
