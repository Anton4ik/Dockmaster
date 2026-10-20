import type { JSX } from 'react'
import { Link } from 'react-router-dom'
import type { Agent, Container, Perm, ProjectListItem } from '../../lib/api'
import { allowed, useCan } from '../../lib/access'
import { bytes, hasUpdate, pct, restartPolicyText, restartRecency, restartVerdict, shortImage, stateInfo, uptime } from '../../lib/fmt'
import { useSetToggle, useVisibleColumns } from '../../lib/hooks'
import RowMenu from '../../components/RowMenu'
import { ContainerDot, ImageDot, Ports, RestartMark, StateDot } from '../../components/Status'
import { groupContainers } from './groupContainers'
import { CpuCell, MemoryCell, MemPct } from './LimitCell'
import { useLimitEdit, type LimitEditor } from './useLimitEdit'

interface TableActions {
  onLogs: (c: Container) => void
  onAct: (c: Container, op: string) => void
  /** pull & redeploy one container (in a Dockmaster project: its whole service) */
  onRedeploy: (c: Container) => void
  /** pull & redeploy the replicas of a service */
  onRedeployService: (cs: Container[], service: string, managed: boolean) => void
  onUpdateProject: (projectId: number, name: string, cs: Container[]) => void
  onUpdateOutdated: (label: string, cs: Container[]) => void
  onImport: (project: string) => void
}

interface RowState {
  isBusy: (c: Container) => boolean
  isSelected: (c: Container) => boolean
  /** what the user may do on this server: actions they may not are not offered */
  may: (perm: Perm) => boolean
}

/** Containers grouped by compose project; replicas of one service fold into a summary row. */
export default function ContainerTable({ agent, containers, projectNamed, isBusy, isSelected, actions }: Omit<RowState, 'may'> & {
  agent: Agent
  containers: Container[]
  projectNamed: (name: string) => ProjectListItem | undefined
  actions: TableActions
}) {
  const [expanded, toggleExpanded] = useSetToggle<string>()
  const limits = useLimitEdit(agent.id)
  // 9 columns: Image, Restarts and Up are hide-sm, Net is hide-md, Ports is hide-lg
  const columns = useVisibleColumns(9, { lg: 1, md: 1, sm: 3 })
  const can = useCan()
  const state: RowState = { isBusy, isSelected, may: perm => can(agent.id, perm) }

  const rows: JSX.Element[] = []
  for (const { key, containers: cs, services } of groupContainers(containers)) {
    const project = key ? projectNamed(key) : undefined
    rows.push(<GroupRow key={'g' + key} name={key} project={project} cs={cs} columns={columns} state={state} actions={actions} />)
    for (const [svc, replicas] of services) {
      if (replicas.length === 1) { rows.push(<ContainerRow key={replicas[0].id} c={replicas[0]} agent={agent} limits={limits} state={state} actions={actions} />); continue }
      const setKey = key + '/' + svc
      const open = expanded.has(setKey)
      rows.push(<ReplicaSetRow key={setKey} service={svc} cs={replicas} open={open} managed={!!project} agent={agent} state={state}
        onToggle={() => toggleExpanded(setKey)} actions={actions} />)
      if (open) for (const c of replicas) rows.push(<ContainerRow key={c.id} c={c} replica agent={agent} limits={limits} state={state} actions={actions} />)
    }
  }

  return (
    <div className="table-wrap">
      <table>
        <thead><tr><th>Name</th><th className="hide-sm">Image</th><th>CPU</th><th>Memory</th><th className="hide-md">Net rx / tx</th><th className="hide-sm">Restarts</th><th className="hide-sm">Up</th><th className="hide-lg">Ports</th><th><span className="sr-only">actions</span></th></tr></thead>
        <tbody>
          {rows}
          {containers.length === 0 && <tr><td colSpan={columns} className="muted">no containers match the filter</td></tr>}
        </tbody>
      </table>
    </div>
  )
}

/** A compose project (or the standalone containers): its name, how many run, and updating all of it. */
function GroupRow({ name, project, cs, columns, state, actions }: {
  /** the compose project, empty for the standalone containers */
  name: string
  /** the Dockmaster project of that name, if it is one */
  project: ProjectListItem | undefined
  cs: Container[]
  columns: number
  state: RowState
  actions: TableActions
}) {
  const outdated = cs.filter(c => hasUpdate(c.update))
  const label = name || 'standalone containers'
  const menu = allowed([
    project
      ? { label: 'update project (pull & redeploy changed)', need: 'deploy' as Perm, onClick: () => actions.onUpdateProject(project.id, name, cs) }
      : { label: `pull & redeploy outdated${outdated.length ? ` (${outdated.length})` : ''}`, need: 'deploy' as Perm, disabled: outdated.length === 0,
          title: outdated.length ? undefined : 'nothing is marked outdated — run "check for updates" first', onClick: () => actions.onUpdateOutdated(label, outdated) },
  ], state.may)
  return (
    <tr className="group">
      <td colSpan={columns - 1}>
        {project ? <Link to={`/projects/${project.id}`} title="open the project">{label}</Link> : label}
        {(() => {
          // jobs waiting for their next run are neither running nor stopped: counted apart
          const jobs = cs.filter(x => x.jobInfo).length, total = cs.filter(x => !x.jobInfo || x.state === 'running').length
          return <span className="group-count" title={jobs ? 'running / services, and the jobs on a schedule' : undefined}>{cs.filter(x => x.state === 'running').length} / {total}{jobs ? ` · ${jobs} job${jobs > 1 ? 's' : ''}` : ''}</span>
        })()}
        {!project && name && state.may('edit') && <>{' '}<button className="sm" title="Take this compose project over: view and edit its compose, update the whole stack" onClick={() => actions.onImport(name)}>import</button></>}
      </td>
      <td className="menu-cell">{cs.some(state.isBusy) ? <Busy /> : menu.length > 0 && <RowMenu title={`actions for ${label}`} items={menu} />}</td>
    </tr>
  )
}

/** One container; `replica` marks a row shown under its folded service row. */
function ContainerRow({ c, replica = false, agent, limits, state, actions }: { c: Container; replica?: boolean; agent: Agent; limits: LimitEditor; state: RowState; actions: TableActions }) {
  return (
    <tr className={(state.isSelected(c) ? 'selected' : '') + (replica ? ' replica' : '')}>
      <td className="name"><span className="name-line"><ContainerDot c={c} /><Link className={c.state === 'running' ? '' : 'muted'} to={`/agents/${agent.id}/containers/${encodeURIComponent(c.name)}`} title="open the container page: state, env, network, mounts, logs">{replica ? c.name.slice(c.name.lastIndexOf('-') + 1) : c.name}</Link><RestartMark c={c} /></span></td>
      <td className="muted trunc hide-sm" title={c.imageRef || c.image}><ImageDot status={c.update} detail={c.updateDetail} />{shortImage(c.imageRef || c.image)}</td>
      <td className="num"><CpuCell c={c} agent={agent} limits={limits} /></td>
      <td className="num"><MemoryCell c={c} agent={agent} limits={limits} /></td>
      <td className="num muted hide-md">{c.state === 'running' ? `${bytes(c.netRx)} / ${bytes(c.netTx)}` : ''}</td>
      <td className="num hide-sm"><Restarts c={c} /></td>
      <td className="num muted hide-sm">{c.state === 'running' ? uptime(c.startedUnix) : ''}</td>
      <td className="hide-lg"><Ports ports={c.ports} /></td>
      <td className="menu-cell">{state.isBusy(c) ? <Busy /> : <RowMenu title={`actions for ${c.name}`} items={containerMenuItems(c, agent, limits, actions, state.may)} />}</td>
    </tr>
  )
}

/** The ⋯ menu of a container: what fits the state it is in. */
function containerMenuItems(c: Container, agent: Agent, limits: LimitEditor, actions: TableActions, may: (perm: Perm) => boolean) {
  return allowed<{ label: string; onClick: () => void; need?: Perm; danger?: boolean; disabled?: boolean; title?: string }>(c.state === 'running' ? [
    { label: 'logs', onClick: () => actions.onLogs(c) },
    { label: 'memory limit', need: 'edit', onClick: () => limits.start(c, 'memory') },
    { label: 'CPU limit', need: 'edit', onClick: () => limits.start(c, 'cpus'), disabled: !agent.cpuLimits, title: agent.cpuLimits ? undefined : 'this host cannot limit CPU' },
    { label: 'pull & redeploy', need: 'deploy', onClick: () => actions.onRedeploy(c) },
    { label: 'restart', need: 'control', onClick: () => actions.onAct(c, 'restart') },
    { label: 'stop', need: 'control', onClick: () => actions.onAct(c, 'stop') },
    { label: 'kill', need: 'control', danger: true, onClick: () => { if (confirm(`Kill ${c.name}?`)) actions.onAct(c, 'kill') } },
  ] : [
    { label: 'logs', onClick: () => actions.onLogs(c) },
    { label: 'start', need: 'control', onClick: () => actions.onAct(c, 'start') },
    { label: 'pull & redeploy', need: 'deploy', onClick: () => actions.onRedeploy(c) },
    { label: 'remove', need: 'control', danger: true, onClick: () => { if (confirm(`Remove container ${c.name}?`)) actions.onAct(c, 'remove') } },
  ], may)
}

const STATE_RANK: Record<string, number> = { bad: 3, warn: 2, muted: 1, ok: 0 }

/** Summary of a service with several replicas: the worst state, sums, the newest uptime. */
function ReplicaSetRow({ service, cs, open, managed, agent, state, onToggle, actions }: {
  service: string
  cs: Container[]
  open: boolean
  managed: boolean
  agent: Agent
  state: RowState
  onToggle: () => void
  actions: TableActions
}) {
  const running = cs.filter(c => c.state === 'running')
  const rank = (c: Container) => STATE_RANK[stateInfo(c.state, c.health).cls]
  const worst = cs.reduce((a, b) => rank(b) > rank(a) ? b : a)
  const sum = (list: Container[], f: (c: Container) => number) => list.reduce((n, c) => n + f(c), 0)
  const limitsAgree = (f: (c: Container) => number) => cs.every(c => f(c) === f(cs[0]))
  const memLimit = limitsAgree(c => c.memoryLimit) ? cs[0].memoryLimit * cs.length : 0
  const cpuLimit = limitsAgree(c => c.cpuLimit) ? cs[0].cpuLimit * cs.length : 0
  const restarts = sum(cs, c => c.restartCount)
  const recentRestart = cs.some(c => restartRecency(c).recent)
  return (
    <tr className={'replica-set' + (cs.some(state.isSelected) ? ' selected' : '')}>
      <td className="name"><span className="name-line">
        <StateDot state={worst.state} health={worst.health} />
        <button className="link" onClick={onToggle} aria-expanded={open} title={open ? 'collapse' : 'show each replica'}>
          <span className="chev">{open ? '▾' : '▸'}</span>{service} <span className="muted">×{cs.length}</span>{running.length < cs.length && <span className="muted mini"> ({running.length} running)</span>}
        </button>
      </span></td>
      <td className="muted trunc hide-sm" title={cs[0].imageRef || cs[0].image}><ImageDot status={cs.find(c => hasUpdate(c.update))?.update ?? cs[0].update} detail={cs[0].updateDetail} />{shortImage(cs[0].imageRef || cs[0].image)}</td>
      <td className="num" title="sum of the replicas">{running.length ? pct(sum(running, c => c.cpuPercent)) : ''}{cpuLimit > 0 ? <span className="muted"> / {cpuLimit}</span> : ''}</td>
      <td className="num" title="sum of the replicas">{running.length > 0 && (
        <span className="mem-cell"><span>{bytes(sum(running, c => c.memoryUsage))}{memLimit > 0 && <span className="muted"> / {bytes(memLimit)}</span>}</span><MemPct used={sum(running, c => c.memoryUsage)} limit={memLimit} host={agent.memoryTotal} /></span>
      )}</td>
      <td className="num muted hide-md">{running.length ? `${bytes(sum(running, c => c.netRx))} / ${bytes(sum(running, c => c.netTx))}` : ''}</td>
      <td className={'num hide-sm' + (recentRestart ? ' error' : ' muted')}
        title={restarts > 0 ? cs.map(c => `${c.name}: ${restartRecency(c).text}`).join('\n') : undefined}>{restarts}</td>
      <td className="num muted hide-sm" title="newest replica">{running.length ? uptime(Math.max(...running.map(c => c.startedUnix))) : ''}</td>
      <td className="hide-lg"><Ports ports={[...new Set(cs.flatMap(c => c.ports))]} /></td>
      <td className="menu-cell">
        {cs.some(state.isBusy) ? <Busy /> : <RowMenu title={`actions for ${service}`} items={allowed([
          { label: 'pull & redeploy', need: 'deploy' as Perm, onClick: () => actions.onRedeployService(cs, service, managed) },
          { label: open ? 'collapse replicas' : 'show replicas', onClick: onToggle },
          { label: 'logs of the first', onClick: () => actions.onLogs(cs[0]) },
        ], state.may)} />}
      </td>
    </tr>
  )
}

const Busy = () => <span className="mini muted" aria-label="working">…</span>

/** Restart count; the tooltip names the restart policy, what it means and whether Docker brings this one back. */
function Restarts({ c }: { c: Container }) {
  const verdict = restartVerdict(c)
  const recency = restartRecency(c)
  const title = [c.restartCount > 0 && recency.text, c.restartPolicy && `${c.restartPolicy}: ${restartPolicyText(c.restartPolicy, c.restartMax)}`, verdict && `Now: ${verdict.text}`]
    .filter(Boolean).join('\n')
  return (
    <span title={title || undefined}>
      <span className={recency.recent ? 'error' : 'muted'}>{c.restartCount}</span>
    </span>
  )
}
