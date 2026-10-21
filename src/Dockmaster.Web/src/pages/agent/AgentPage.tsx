import { useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api, errorText, type Agent, type Container } from '../../lib/api'
import { ago, containerKey, hasUpdate, isProblem } from '../../lib/fmt'
import { LOG_TAIL, useFollowLogs } from '../../lib/live'
import { toast } from '../../lib/toast'
import Drawer from '../../components/Drawer'
import LogView from '../../components/LogView'
import { StateDot } from '../../components/Status'
import TaskDrawer from '../../components/TaskDrawer'
import AgentHeader from './AgentHeader'
import ContainerTable from './ContainerTable'
import HostStats from './HostStats'
import ServerNav from './ServerNav'
import UncheckedImages from './UncheckedImages'
import { useAgentData } from './useAgentData'
import { useTask } from './useTask'

/** Route entry: a fresh page per agent, so nothing (filters, a running task, errors) leaks from the previous one. */
export default function AgentRoute({ agents, onChanged }: { agents: Agent[]; onChanged: () => void }) {
  const id = Number(useParams().id)
  return <AgentPage key={id} id={id} agents={agents} onChanged={onChanged} />
}

/**
 * One server: its usage, its containers grouped by project with their actions, and the log / task drawer. Outcomes
 * go to the corner of the screen: the table is long and the top of the page is often scrolled away.
 */
function AgentPage({ id, agents, onChanged }: { id: number; agents: Agent[]; onChanged: () => void }) {
  const navigate = useNavigate()
  const [search] = useSearchParams()
  const data = useAgentData(id)
  const task = useTask(id)
  const [busy, setBusy] = useState<Set<string>>(new Set())
  const [logKey, setLogKey] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [onlyProblems, setOnlyProblems] = useState(search.get('problems') === '1')
  const [onlyUpdates, setOnlyUpdates] = useState(false)
  const [checking, setChecking] = useState(false)

  const agent = agents.find(a => a.id === id) ?? data.detail?.agent
  if (!agent) return data.error ? <p className="error">{data.error}</p> : null
  const { containers } = data

  // the log drawer follows the container by name/service: a re-created container keeps its drawer
  const logContainer = logKey ? containers.find(c => containerKey(c) === logKey) : undefined
  const openLogs = (c: Container) => { task.close(); setLogKey(containerKey(c)) }

  async function act(c: Container, op: string) {
    setBusy(b => new Set(b).add(c.id))
    try { await api(`/agents/${id}/containers/${c.id}/${op}`, { method: 'POST' }) }
    catch (e) { toast(`${c.name}: ${errorText(e)}`, 'error') }
    finally { setBusy(b => { const n = new Set(b); n.delete(c.id); return n }) }
  }

  async function importProject(key: string) {
    if (!confirm(`Import compose project "${key}" into Dockmaster?\n\nThe agent reads its compose files and .env from this host. Running containers are not touched; later deploys from Dockmaster take the project over.`)) return
    try {
      const r = await api<{ project: { id: number }; warnings: string[] }>(`/agents/${id}/import`, { method: 'POST', json: { project: key } })
      if (r.warnings.length) alert(r.warnings.join('\n'))
      navigate(`/projects/${r.project.id}`)
    } catch (e) { toast(`import ${key}: ${errorText(e)}`, 'error') }
  }

  async function checkUpdates() {
    setChecking(true)
    try { await api('/updates/check', { method: 'POST' }); await data.refresh() }
    catch (e) { toast(errorText(e), 'error') }
    finally { setChecking(false) }
  }

  const q = filter.trim().toLowerCase()
  const visible = containers.filter(c => (!onlyProblems || isProblem(c)) && (!onlyUpdates || hasUpdate(c.update))
    && (!q || c.name.toLowerCase().includes(q) || c.image.toLowerCase().includes(q) || c.project.toLowerCase().includes(q)))
  const updates = containers.filter(c => hasUpdate(c.update)).length
  const drawerTask = task.task && !logKey ? task.task : null

  return (
    <>
      <AgentHeader agent={agent} onChanged={onChanged} />
      {agent.online && <HostStats agent={agent} containers={containers} />}
      <ServerNav agentId={id} />

      {!agent.online ? (
        <div className="card"><p>Server is offline (last seen {ago(agent.lastSeenAt)}). Make sure the agent container is running on it and can reach the server's agent port.</p></div>
      ) : containers.length === 0 ? <p className="muted">Waiting for the first heartbeat…</p> : (
        <>
          <div className="filters">
            <h2>Containers ({visible.length === containers.length ? containers.length : `${visible.length} / ${containers.length}`})</h2>
            <input type="search" aria-label="filter containers" placeholder="filter by name, image, project" value={filter} onChange={e => setFilter(e.target.value)} />
            <label><input type="checkbox" checked={onlyProblems} onChange={e => setOnlyProblems(e.target.checked)} /> only problems</label>
            <label><input type="checkbox" checked={onlyUpdates} onChange={e => setOnlyUpdates(e.target.checked)} /> only updates{updates > 0 ? ` (${updates})` : ''}</label>
            <button className="sm" disabled={checking} onClick={checkUpdates} title="Ask the registries for the current image of every tag">{checking ? 'checking…' : 'check for updates'}</button>
            <UncheckedImages containers={containers} onFixed={() => { data.refresh().catch(() => {}) }} />
          </div>
          <ContainerTable agent={agent} containers={visible} projectNamed={data.projectNamed}
            isBusy={c => busy.has(c.id) || (task.running && task.involves(c))}
            isSelected={c => (logKey != null && containerKey(c) === logKey) || (drawerTask != null && task.involves(c))}
            actions={{
              onLogs: openLogs, onAct: (c, op) => { void act(c, op) }, onImport: k => { void importProject(k) },
              onRedeploy: c => { setLogKey(null); task.redeployContainer(c, containers, !!data.projectOf(c)) },
              onRedeployService: (cs, service, managed) => { setLogKey(null); task.redeploy(cs, service, managed) },
              onUpdateProject: (pid, name, cs) => { setLogKey(null); task.updateProject(pid, name, cs) },
              onUpdateOutdated: (label, cs) => { setLogKey(null); task.updateOutdated(label, cs) },
            }} />
        </>
      )}

      {/* keyed by container id: a re-created container starts a fresh log */}
      {logKey && <LogDrawer key={logContainer?.id ?? 'gone'} agentId={id} container={logContainer} name={logKey} onClose={() => setLogKey(null)} />}
      {drawerTask && <TaskDrawer title={drawerTask.title} text={drawerTask.text} running={task.running} result={drawerTask.result} onClose={task.close} onCancel={task.cancel} />}
    </>
  )
}

/** Live logs of one container; when it is re-created the drawer switches to the new one. */
function LogDrawer({ agentId, container, name, onClose }: { agentId: number; container: Container | undefined; name: string; onClose: () => void }) {
  const text = useFollowLogs(agentId, container?.id, LOG_TAIL.drawer)
  return (
    <Drawer title={container?.name ?? name} onClose={onClose}
      icon={container ? <StateDot state={container.state} health={container.health} /> : <i className="dot muted" />}>
      <LogView text={text} empty={container ? '…' : '[container is gone — waiting for it to come back]'} />
    </Drawer>
  )
}
