import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, errorText, type Agent, type Container, type ContainerDetails } from '../../lib/api'
import { useCan } from '../../lib/access'
import { bytes, pct, restartRecency, uptime } from '../../lib/fmt'
import { LOG_TAIL, useFollowLogs } from '../../lib/live'
import { withToast } from '../../lib/toast'
import LogView from '../../components/LogView'
import RowMenu from '../../components/RowMenu'
import { StatTile } from '../../components/StatTile'
import { ContainerDot, ImageDot, JobMark, StateDot } from '../../components/Status'
import { Tab, Tabs } from '../../components/Tabs'
import TaskDrawer from '../../components/TaskDrawer'
import { useAgentData } from '../agent/useAgentData'
import { useTask } from '../agent/useTask'
import EnvTab from './EnvTab'
import KeyValues from './KeyValues'
import MetricsTab from './MetricsTab'
import MountsTab from './MountsTab'
import NetworksTab from './NetworksTab'
import OverviewTab from './OverviewTab'

// the terminal (xterm.js) is a third of the app's size: loaded when the Console tab opens
const TerminalView = lazy(() => import('../../components/TerminalView'))

type TabId = 'overview' | 'metrics' | 'env' | 'network' | 'mounts' | 'labels' | 'logs' | 'console'

/** Route entry: keyed by agent and name, so nothing carries over from the previous container. */
export default function ContainerRoute({ agents }: { agents: Agent[] }) {
  const { id, name } = useParams()
  return <ContainerPage key={`${id}/${name}`} agentId={Number(id)} name={name ?? ''} agents={agents} />
}

/**
 * One container: live state and usage, actions, and everything `docker inspect` knows (env, networks, mounts,
 * labels, health log), plus its log. Addressed by name, so the page survives a pull & redeploy.
 */
function ContainerPage({ agentId, name, agents }: { agentId: number; name: string; agents: Agent[] }) {
  const navigate = useNavigate()
  const data = useAgentData(agentId)
  const task = useTask(agentId)
  const live = data.containers.find(c => c.name === name)
  const agent = agents.find(a => a.id === agentId)
  const project = live ? data.projectOf(live) : undefined
  const [d, setD] = useState<ContainerDetails | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<TabId>('overview')
  const [busy, setBusy] = useState(false)
  // a new number opens a new shell
  const [shell, setShell] = useState(0)
  const can = useCan()
  const [mayControl, mayDeploy, mayShell] = [can(agentId, 'control'), can(agentId, 'deploy'), can(agentId, 'terminal')]

  const load = useCallback(() => {
    api<ContainerDetails>(`/agents/${agentId}/containers/${encodeURIComponent(name)}/inspect`)
      .then(x => { setD(x); setError(null) })
      .catch(e => setError(errorText(e)))
  }, [agentId, name])
  // again whenever the container is re-created, starts, stops or changes health
  useEffect(load, [load, live?.id, live?.state, live?.health])

  async function act(op: string) {
    if (!live) return
    if (op === 'kill' && !confirm(`Kill ${name}?`)) return
    if (op === 'remove' && !confirm(`Remove container ${name}?`)) return
    setBusy(true)
    const ok = await withToast(`${name}: ${op}…`, () => api(`/agents/${agentId}/containers/${live.id}/${op}`, { method: 'POST' }),
      `${name}: ${op} done`, message => `${name}: ${message}`)
    if (ok && op === 'remove') navigate(`/agents/${agentId}`)
    setBusy(false)
  }

  /** A job's run outside its schedule: through the scheduler, so the run is followed and listed as "run now". */
  async function runJob() {
    if (!live || !project) return
    setBusy(true)
    await withToast(`${name}: starting…`, () => api<{ message: string }>(`/projects/${project.id}/services/${encodeURIComponent(live.service)}/run`, { method: 'POST' }),
      r => `${name}: ${r.message}`, message => `${name}: ${message}`)
    setBusy(false)
  }

  const running = live?.state === 'running'
  const disabled = busy || task.running || !live
  const title = (
    <div className="toolbar">
      <h1>
        <Link to={`/agents/${agentId}`}>{agent?.name ?? `server ${agentId}`}</Link><span className="muted"> /</span>
        <span className="container-name">{name}</span>
        {live && (live.jobInfo ? <><ContainerDot c={live} label /><JobMark c={live} /></> : <StateDot state={live.state} health={live.health} label />)}
      </h1>
      {(mayControl || mayDeploy) && <div className="actions push-right">
        {mayControl && (running
          ? <button className="sm" disabled={disabled} onClick={() => { void act('restart') }}>restart</button>
          : live?.jobInfo && project
            ? <button className="sm primary" disabled={disabled} onClick={() => { void runJob() }} title="starts the job now, outside its schedule">run now</button>
            : <button className="sm primary" disabled={disabled} onClick={() => { void act('start') }}>start</button>)}
        {mayControl && running && <button className="sm" disabled={disabled} onClick={() => { void act('stop') }}>stop</button>}
        {mayDeploy && <button className="sm" disabled={disabled} onClick={() => { if (live) task.redeployContainer(live, data.containers, !!project) }} title="pull the newest image and re-create">pull &amp; redeploy</button>}
        {mayControl && <RowMenu title="more actions" items={[
          { label: 'kill', danger: true, onClick: () => { void act('kill') }, disabled: disabled || !running },
          { label: 'remove', danger: true, onClick: () => { void act('remove') }, disabled },
        ]} />}
      </div>}
    </div>
  )

  if (!live && !d) return (
    <>
      {title}
      {data.detail || error ? <div className="card"><p>{error ?? `No container named ${name} on this server (any more).`} <Link to={`/agents/${agentId}`}>Back to the server</Link></p></div>
        : <p className="muted">Loading…</p>}
    </>
  )

  const own = d?.env.filter(e => !e.fromImage).length ?? 0
  return (
    <>
      {title}
      <div className="host-facts facts">
        <span className="muted" title={d?.imageId}>{live && <ImageDot status={live.update} detail={live.updateDetail} />}{d?.image ?? live?.image}</span>
        {project && <span>project <Link to={`/projects/${project.id}`}>{project.name}</Link>{live?.service && <span className="muted"> · {live.service}</span>}</span>}
        {!project && live?.project && <span className="muted">compose {live.project}{live.service && ` · ${live.service}`}</span>}
        {d && <span className="muted" title={d.id}>{d.id.slice(0, 12)}</span>}
      </div>
      {error && <p className="error">{error}</p>}

      {live && running && (
        <div className="stats">
          <StatTile label="CPU" value={pct(live.cpuPercent)} note={live.cpuLimit > 0 ? `limit ${live.cpuLimit} cores` : 'no limit'} />
          <StatTile label="Memory" value={bytes(live.memoryUsage)} note={live.memoryLimit > 0 ? `of ${bytes(live.memoryLimit)} · ${pct(100 * live.memoryUsage / live.memoryLimit)}` : 'no limit'} />
          <StatTile label="Network" value={`${bytes(live.netRx)} / ${bytes(live.netTx)}`} note="received / sent" />
          <StatTile label="Up" value={uptime(live.startedUnix)} note={<span title={restartRecency(live).text}>{restartRecency(live).short}</span>} bad={restartRecency(live).recent} />
        </div>
      )}

      <Tabs>
        <Tab value="overview" current={tab} onSelect={setTab}>Overview</Tab>
        <Tab value="metrics" current={tab} onSelect={setTab} title="CPU, memory and network over the last hours">Metrics</Tab>
        <Tab value="env" current={tab} onSelect={setTab}><span className="hide-sm">Environment</span><span className="show-sm">Env</span>{d && <span className="muted">{own}</span>}</Tab>
        <Tab value="network" current={tab} onSelect={setTab}>Network</Tab>
        <Tab value="mounts" current={tab} onSelect={setTab}>Mounts{d && <span className="muted">{d.mounts.length}</span>}</Tab>
        <Tab value="labels" current={tab} onSelect={setTab}>Labels</Tab>
        <Tab value="logs" current={tab} onSelect={setTab}>Logs</Tab>
        {mayShell && <Tab value="console" current={tab} onSelect={setTab} title="a shell inside the container: look at files, run a command">Console</Tab>}
      </Tabs>

      {!d ? (tab !== 'logs' && tab !== 'metrics' && <p className="muted">Loading…</p>) : (
        <>
          {tab === 'overview' && <OverviewTab d={d} agentId={agentId} lastExit={live?.lastExit} restarts={live?.restartCount} restartsRecent={live && restartRecency(live).recent} onChanged={load} />}
          {tab === 'env' && <EnvTab env={d.env} hidden={d.envHidden} />}
          {tab === 'network' && <NetworksTab d={d} agentId={agentId} onChanged={load} />}
          {tab === 'mounts' && <MountsTab d={d} agentId={agentId} />}
          {tab === 'labels' && <KeyValues rows={Object.entries(d.labels).sort(([a], [b]) => a.localeCompare(b))} empty="no labels" />}
        </>
      )}
      {tab === 'metrics' && <MetricsTab agentId={agentId} name={name} />}
      {tab === 'logs' && <Logs agentId={agentId} container={live} />}
      {tab === 'console' && mayShell && (running
        ? <>
          <div className="toolbar">
            <span className="mini">A shell inside <b>{name}</b> (bash, or sh). What you change here is lost when the container is re-created — except in its volumes.</span>
            <div className="spacer" />
            <button className="sm" onClick={() => setShell(n => n + 1)} title="close this shell and open a new one">new shell</button>
          </div>
          <Suspense fallback={<p className="muted">Loading the terminal…</p>}><TerminalView agentId={agentId} container={name} session={shell} /></Suspense>
        </>
        : <p className="muted">The container is not running — start it to open a shell.</p>)}

      {task.task && <TaskDrawer title={task.task.title} text={task.task.text} running={task.running} result={task.task.result} onClose={task.close} onCancel={task.cancel} />}
    </>
  )
}

/** The live log, inline; follows the new container after a re-create (same name, new id). */
function Logs({ agentId, container }: { agentId: number; container: Container | undefined }) {
  const text = useFollowLogs(agentId, container?.id, LOG_TAIL.page)
  return <LogView text={text} className="page-log" empty={container ? '…' : '[container is gone — waiting for it to come back]'} />
}
