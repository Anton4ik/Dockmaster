import { useCallback, useEffect, useState } from 'react'
import { usePoll } from '../../lib/hooks'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, errorText, type Agent, type AutoscaleStatus, type JobStatus, type Mesh, type MountEdit, type Perm, type PlacementResult, type Project, type ProjectPlan, type ProjectVolumes, type ServiceSummary } from '../../lib/api'
import { toast, withToast } from '../../lib/toast'
import { can, canAll, useMe } from '../../lib/access'
import CodeEditor from '../../components/CodeEditor'
import { Tab, Tabs } from '../../components/Tabs'
import TaskDrawer from '../../components/TaskDrawer'
import AutomateTab from './AutomateTab'
import ComposeTab from './ComposeTab'
import DeleteProject from './DeleteProject'
import MoveToSecrets, { type SecretMove } from './MoveToSecrets'
import FilesTab from './FilesTab'
import { DEFAULT_COMPOSE } from './defaultCompose'
import HistoryTab from './HistoryTab'
import MetricsTab from './MetricsTab'
import ProjectPlacement from './ProjectPlacement'
import ProjectSecrets from './ProjectSecrets'
import ProjectServices, { type LimitChange, type Limits } from './ProjectServices'
import type { ServiceFormValue } from './ServiceForm'
import type { AutoscaleBody } from './AutoscaleEditor'
import ProjectToolbar, { type ProjectOp } from './ProjectToolbar'
import ProjectUsage from './ProjectUsage'
import RunNotice from './RunNotice'
import { useProjectDeploy } from './useProjectDeploy'
import { useProjectHosts } from './useProjectHosts'

type TabId = 'services' | 'compose' | 'env' | 'files' | 'secrets' | 'placement' | 'metrics' | 'history' | 'automate'

/** What is edited on the page and saved together. */
/** `version`: the project's updatedAt as loaded, so a save does not run over a change made elsewhere meanwhile */
export interface ProjectForm { name: string; agentId: number; compose: string; env: string; timeout: number; version?: string }

/**
 * Route entry: a fresh page when another project is opened, so nothing (tab, log, history, plan) carries over. A new
 * project that was just created keeps its page: its first deploy may already be running in it.
 */
export default function ProjectRoute({ agents }: { agents: Agent[] }) {
  const id = useParams().id ?? null
  const [page, setPage] = useState({ id, key: 0 })
  if (page.id !== id) setPage({ id, key: page.id === null ? page.key : page.key + 1 })
  return <ProjectEdit key={page.key} agents={agents} />
}

/**
 * A project: its compose file, .env and secrets, the services with their limits and servers, how it is spread over
 * servers, and its deploy history. Every action saves first, so nothing typed is lost to a deploy.
 */
function ProjectEdit({ agents }: { agents: Agent[] }) {
  const params = useParams()
  const id = params.id ? Number(params.id) : null
  const isNew = id === null
  const navigate = useNavigate()

  const [form, setForm] = useState<ProjectForm>({ name: '', agentId: 0, compose: DEFAULT_COMPOSE, env: '', timeout: 180 })
  const change = (patch: Partial<ProjectForm>) => setForm(f => ({ ...f, ...patch }))
  const [services, setServices] = useState<ServiceSummary[]>([])
  // a project opens on its services; a new one has only its compose file yet
  const [tab, setTab] = useState<TabId>(isNew ? 'compose' : 'services')
  const [plan, setPlan] = useState<ProjectPlan | null>(null)
  const [error, setError] = useState<string | null>(null)
  // an instance of a template: its files come from the template whenever the API deploys it
  const [fromTemplate, setFromTemplate] = useState(false)
  const deploy = useProjectDeploy()
  // the .env was left out: the user has no Secrets permission on every server of the project
  const [envHidden, setEnvHidden] = useState(false)
  // what the server says the user may do with it: it knows the servers the page cannot (hidden ones, deployed to before)
  const [serverMay, setServerMay] = useState<Perm[] | null>(null)
  const me = useMe()

  // load once per project: agent heartbeats change `agents` every few seconds and must not reset what is being edited
  useEffect(() => {
    if (isNew) return
    api<Project>(`/projects/${id}`).then(p => {
      setForm({ name: p.name, agentId: p.agentId, compose: p.composeYaml, env: p.envFile, timeout: p.healthTimeoutSeconds, version: p.updatedAt })
      setEnvHidden(!!p.envHidden)
      setServerMay(p.may ?? null)
      setServices(p.services)
      setFromTemplate(p.templateId != null)
    }).catch(e => setError(errorText(e)))
  }, [id, isNew])

  // a new project on a setup with a single agent: that is where it goes
  const agentId = form.agentId || (isNew && agents.length === 1 ? agents[0].id : 0)
  const hosts = useProjectHosts(agents, agentId, services, form.name)
  // what the user may do with it: on every server it is on (a new one: on the server picked for it), and what the
  // server said when it was loaded, which also counts the servers this page does not know of
  const servers = isNew ? [agentId] : hosts.hostIds.length > 0 ? hosts.hostIds : [agentId]
  const allows = (perm: Perm) => canAll(me, servers, perm) && (isNew || !serverMay || serverMay.includes(perm))
  const may = { deploy: allows('deploy'), control: allows('control'), edit: allows('edit'), secrets: allows('secrets') }

  // the servers a service may go to: the ones sharing a network with the project's server (read when the tab opens)
  const [meshPeers, setMeshPeers] = useState<ReadonlySet<number>>()
  useEffect(() => {
    if (tab !== 'services' || !agentId) return
    api<Mesh>('/mesh').then(m => setMeshPeers(new Set(m.networks.filter(n => n.members.includes(agentId)).flatMap(n => n.members)))).catch(() => {})
  }, [tab, agentId])

  // the autoscaler's view of the services, while their tab is open
  const [autoscale, setAutoscale] = useState<AutoscaleStatus[]>([])
  const loadAutoscale = useCallback(() => {
    if (tab === 'services' && id !== null && services.some(s => s.autoscale)) api<AutoscaleStatus[]>(`/projects/${id}/autoscale`).then(setAutoscale).catch(() => {})
  }, [tab, id, services])
  usePoll(loadAutoscale, 30_000)

  // the jobs (services with a schedule): next run and last runs, while the services tab is open
  const [jobs, setJobs] = useState<JobStatus[]>([])
  const loadJobs = useCallback(() => {
    if (tab === 'services' && id !== null && services.some(s => s.schedule)) api<JobStatus[]>(`/projects/${id}/jobs`).then(setJobs).catch(() => {})
  }, [tab, id, services])
  usePoll(loadJobs, 15_000)
  useEffect(loadJobs, [loadJobs, deploy.finished])

  // the plan shows on the Servers tab, fresh after every deploy; its warnings on the Services tab too
  useEffect(() => {
    if ((tab === 'placement' || tab === 'services') && id !== null) api<ProjectPlan>(`/projects/${id}/plan`).then(setPlan).catch(() => {})
  }, [tab, id, deploy.finished])

  // the volumes of the project's servers and the images' data paths, for the Data column and its editor
  const [volumes, setVolumes] = useState<ProjectVolumes>()
  const [fileNames, setFileNames] = useState<string[]>([])
  const loadVolumes = useCallback(() => {
    if (tab !== 'services' || id === null) return
    api<ProjectVolumes>(`/projects/${id}/volumes`).then(setVolumes).catch(() => {})
    api<{ name: string }[]>(`/projects/${id}/files`).then(fs => setFileNames(fs.map(f => f.name))).catch(() => {})
  }, [tab, id])
  useEffect(loadVolumes, [loadVolumes, deploy.finished])

  /** The server's answer to a change written into the compose file: the file as it is now, and its services. */
  const applyProject = (p: Project) => { change({ compose: p.composeYaml, version: p.updatedAt }); setServices(p.services) }
  const fail = (message: string) => { setError(message); return message }

  /** Writes the project as edited here. Silent: it also runs before every service change, to keep compose edits made meanwhile. */
  async function save(patch: Partial<ProjectForm> = {}): Promise<number | null> {
    setError(null)
    const f = { ...form, ...patch }
    if (Object.keys(patch).length > 0) change(patch)
    const body = { name: f.name.trim().toLowerCase(), agentId, composeYaml: f.compose, envFile: f.env, healthTimeoutSeconds: f.timeout, updatedAt: f.version }
    try {
      const p = isNew
        ? await api<Project>('/projects', { method: 'POST', json: body })
        : await api<Project>(`/projects/${id}`, { method: 'PUT', json: body })
      setServices(p.services)
      change({ version: p.updatedAt })
      if (isNew) navigate(`/projects/${p.id}`, { replace: true })
      return p.id
    } catch (e) { setError(errorText(e)); return null }
  }

  // the names of the project's secrets: ${VARIABLES} a service may use besides the .env
  const [secretNames, setSecretNames] = useState<string[]>([])
  const loadSecretNames = useCallback(() => {
    if (id !== null) api<{ name: string }[]>(`/projects/${id}/secrets`).then(l => setSecretNames(l.map(s => s.name))).catch(() => {})
  }, [id])
  useEffect(loadSecretNames, [loadSecretNames, tab])

  /** Values moved into Secrets: each stored as a secret, its .env line taken out, a service's own value made ${the secret}. */
  async function moveToSecrets(moves: SecretMove[]): Promise<boolean> {
    if (id === null) return false
    try {
      for (const m of moves) await api(`/projects/${id}/secrets/${encodeURIComponent(m.secret)}`, { method: 'PUT', json: { value: m.value } })
      const gone = new Set(moves.filter(m => !m.service).map(m => m.name))
      const env = form.env.split('\n').filter(l => !gone.has(/^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.-]*)\s*=/.exec(l)?.[1] ?? '')).join('\n')
      if (await save({ env }) === null) return false
      for (const service of new Set(moves.flatMap(m => m.service ? [m.service] : []))) {
        const own = moves.filter(m => m.service === service)
        const environment = (services.find(s => s.name === service)?.environment ?? []).map(e => {
          const m = own.find(x => x.name === e.name)
          return m ? { name: e.name, value: '${' + m.secret + '}' } : e
        })
        const r = await api<PlacementResult>(`/projects/${id}/services/${encodeURIComponent(service)}/settings`, { method: 'PUT', json: { image: null, ports: null, environment, restart: null } })
        applyProject(r.project)
      }
      loadSecretNames()
      toast(`${moves.length} moved to Secrets — the next deploy fills them in`, 'ok')
      return true
    } catch (e) { setError(errorText(e)); return false }
  }

  /** Saves first (keeps unsaved compose edits), then acts on the saved project; false when either failed. */
  async function withSaved(action: (pid: number) => Promise<boolean | void>) {
    const pid = await save()
    return pid !== null && await action(pid) !== false
  }

  async function start(pid: number, method: string, args: unknown[]) {
    setError(null)
    const r = await deploy.start(pid, method, args)
    if (r && !r.ok) setError(r.message)
  }

  const run = (method: string, args: unknown[]) => withSaved(pid => start(pid, method, args))
  const deployServices = (svcs: string[] | null, pull: boolean) => run('Deploy', [svcs, pull])
  const action = (op: ProjectOp, svcs: string[] | null = null) => {
    if (op === 'down' && !confirm('Stop and remove all containers of this project?')) return
    return run('ProjectAction', [op, svcs])
  }

  /** Writes a service's limits into the compose file; `live` applies them to its running replicas as well. */
  const putLimits = (pid: number, service: string, body: unknown, live = false) =>
    api<{ live: boolean; message: string; project: Project }>(`/projects/${pid}/services/${encodeURIComponent(service)}/limits${live ? '?live=true' : ''}`, { method: 'PUT', json: body })

  // the services table can be long: progress and outcome of its changes show in the corner, wherever the page is scrolled.
  // Each resolves true once the change is saved: only then the table drops what was typed.

  const applyLive = (service: string, memory: string | null, cpus: string | null) => withSaved(pid =>
    withToast(`${service}: applying limits…`, async () => {
      const r = await putLimits(pid, service, { memory, cpus }, true)
      applyProject(r.project)
      if (!r.live) setError(`${service}: ${r.message}`)
      return r
    }, r => ({ text: `${service}: ${r.message}`, ok: r.live }), fail))

  /** Several services at once: limit changes live, one after another; new replica counts in a single deploy. */
  const applyAll = (changes: LimitChange[]) => withSaved(async pid => {
    const n = changes.length, s = n > 1 ? 's' : ''
    const redeploy: string[] = []
    const ok = await withToast(`applying ${n} change${s}…`, async () => {
      for (const c of changes) {
        const r = c.live ? await putLimits(pid, c.service, { memory: c.memory, cpus: c.cpus }, true) : await putLimits(pid, c.service, c.limits)
        applyProject(r.project)
        if (!c.live) redeploy.push(c.service)
      }
    }, () => redeploy.length ? `saved — deploying ${redeploy.join(', ')}` : `${n} service${s} updated live`, fail)
    if (ok && redeploy.length) void start(pid, 'Deploy', [redeploy, false])
    return ok
  })

  /** New replica count (or a removed limit): written into the compose file, then the service is redeployed. */
  const applyLimits = (service: string, limits: Limits) => withSaved(async pid => {
    try { applyProject((await putLimits(pid, service, limits)).project) }
    catch (e) { setError(errorText(e)); return false }
    void start(pid, 'Deploy', [[service], false])
  })

  /** Server / exposed ports of a service: written into the compose file, the plan tells what it means. */
  const placement = (service: string, what: 'placement' | 'expose', body: unknown) => withSaved(pid =>
    withToast(`${service}: saving…`, async () => {
      const r = await api<PlacementResult>(`/projects/${pid}/services/${encodeURIComponent(service)}/${what}`, { method: 'PUT', json: body })
      applyProject(r.project); setPlan(r.plan)
      return r.plan.problems.length
    }, problems => problems
      ? { text: `${service}: saved, but ${problems} problem${problems > 1 ? 's' : ''} stop a deploy (Placement tab)`, ok: false }
      : `${service}: saved — deploy to apply`, fail))

  /** Autoscaling of a service: written into the compose file; mode auto changes the layout, which the next deploy applies. */
  const setAutoscaleOf = (service: string, body: AutoscaleBody) => withSaved(pid =>
    withToast(`${service}: saving…`, async () => {
      const r = await api<PlacementResult>(`/projects/${pid}/services/${encodeURIComponent(service)}/autoscale`, { method: 'PUT', json: body })
      applyProject(r.project); setPlan(r.plan); loadAutoscale()
      return r.plan.problems.length
    }, problems => problems
      ? { text: `${service}: saved, but ${problems} problem${problems > 1 ? 's' : ''} stop a deploy (Servers tab)`, ok: false }
      : body.max === null ? `${service}: autoscaling off` : body.mode === 'auto' ? `${service}: saved — deploy to lay it out for the balancers` : `${service}: saved — watching, see Events`, fail))

  /** A new service from the form (or a ready-made app); `redeploy` starts it (pulls its image). */
  const addService = (v: ServiceFormValue, redeploy: boolean) => withSaved(pid =>
    withToast(`${v.name}: adding…`, async () => {
      const r = await api<PlacementResult>(`/projects/${pid}/services`, { method: 'POST', json: v })
      applyProject(r.project); setPlan(r.plan)
      if (redeploy) void start(pid, 'Deploy', [[v.name], true])
    }, redeploy ? `${v.name}: added — deploying` : `${v.name}: added — deploy to start it`, fail))

  /** Image, ports, environment and restart policy of a service; `redeploy` pulls and re-creates it with them. */
  const saveSettings = (service: string, v: ServiceFormValue, redeploy: boolean) => withSaved(pid =>
    withToast(`${service}: saving…`, async () => {
      const r = await api<PlacementResult>(`/projects/${pid}/services/${encodeURIComponent(service)}/settings`,
        { method: 'PUT', json: { image: v.image, ports: v.ports, environment: v.environment, restart: v.restart, schedule: v.schedule === undefined ? null : v.schedule ?? { cron: null } } }) // null: left as it is; a schedule without cron takes it off
      applyProject(r.project); setPlan(r.plan)
      if (redeploy) void start(pid, 'Deploy', [[service], true])
    }, redeploy ? `${service}: saved — deploying` : `${service}: saved — deploy to apply`, fail))

  /** Starts a job now, outside its schedule; the scheduler follows the run from the heartbeats. */
  const runJob = (service: string) => withSaved(pid =>
    withToast(`${service}: starting…`, async () => {
      const r = await api<{ ok: boolean; message: string }>(`/projects/${pid}/services/${encodeURIComponent(service)}/run`, { method: 'POST' })
      setTimeout(loadJobs, 3000)
      return r.message
    }, message => `${service}: ${message}`, fail))

  /** Takes the service's containers down, then the service out of the compose file (its data stays on the server). */
  const removeService = (service: string) => {
    if (!confirm(`Remove service ${service}?\n\nIts containers are stopped and removed, and it goes out of the compose file. Its volumes and their data stay on the server (the server's Volumes page removes them).`)) return
    void withSaved(async pid => {
      const r = await deploy.start(pid, 'ProjectAction', ['down', [service]])
      if (r && !r.ok && !confirm(`Taking ${service} down failed: ${r.message}\n\nRemove it from the compose file anyway?`)) return false
      try { applyProject(await api<Project>(`/projects/${pid}/services/${encodeURIComponent(service)}`, { method: 'DELETE' })); toast(`${service} removed`) }
      catch (e) { setError(errorText(e)); return false }
    })
  }

  /** The secrets a service reads: its `secrets:` list in the compose file. */
  const useSecrets = (service: string, names: string[]) => withSaved(pid =>
    withToast(`${service}: saving…`, async () => {
      applyProject(await api<Project>(`/projects/${pid}/services/${encodeURIComponent(service)}/secrets`, { method: 'PUT', json: { names } }))
    }, `${service}: saved — deploy it to apply`, fail))

  /** Where a service keeps its data: written into the compose file; `redeploy` re-creates the service with it. */
  const setMountsOf = (service: string, mounts: MountEdit[], redeploy: boolean) => withSaved(pid =>
    withToast(`${service}: saving…`, async () => {
      const r = await api<PlacementResult>(`/projects/${pid}/services/${encodeURIComponent(service)}/mounts`, { method: 'PUT', json: { mounts } })
      applyProject(r.project); setPlan(r.plan)
      if (redeploy) void start(pid, 'Deploy', [[service], false])
      return r.plan.warnings.filter(w => w.startsWith(`service ${service}`))
    }, warnings => warnings.length ? { text: `${service}: saved — ⚠ ${warnings.join(' · ')}`, ok: false }
      : redeploy ? `${service}: saved — deploying` : `${service}: saved — deploy to apply`, fail))

  /** Puts an earlier deploy's files back and deploys them; the page then shows the files as they are now. */
  const rollback = (depId: number) => withSaved(async pid => {
    await start(pid, 'Rollback', [depId])
    const p = await api<Project>(`/projects/${pid}`)
    change({ compose: p.composeYaml, env: p.envFile, version: p.updatedAt })
    setServices(p.services)
  })

  async function checkUpdates() {
    try { await api('/updates/check', { method: 'POST' }); await hosts.refresh() }
    catch (e) { setError(errorText(e)) }
  }

  /** Deletes the project; `down` takes its containers off the servers first, so none are left behind unmanaged. */
  async function remove(down: boolean) {
    if (id === null) return
    setError(null)
    if (down) {
      const r = await deploy.start(id, 'ProjectAction', ['down', null])
      if (r && !r.ok && !confirm(`Taking the containers down failed: ${r.message}\n\nDelete the project anyway? They keep running on the server.`)) {
        setError(r.message); setDeleting(false); return
      }
    }
    try { await api(`/projects/${id}`, { method: 'DELETE' }); navigate('/projects') }
    catch (e) { setError(errorText(e)); setDeleting(false) }
  }

  const { running } = deploy
  const [deleting, setDeleting] = useState(false)
  return (
    <>
      <ProjectToolbar name={form.name} isNew={isNew} hosts={hosts.hostIds.flatMap(h => agents.filter(a => a.id === h))} containers={hosts.containers} running={running} may={may}
        onDeploy={pull => { void deployServices(null, pull) }} onAction={op => { void action(op) }} onDelete={() => setDeleting(true)} />
      {deleting && <DeleteProject name={form.name} containers={hosts.containers.length} onDelete={remove} onClose={() => setDeleting(false)} />}
      {!isNew && <ProjectUsage containers={hosts.containers} />}
      {fromTemplate && (
        <p className="hint">An instance of a template (<Link to="/templates">Templates</Link>): a deploy through the API or the Templates page writes the
          template's compose file and .env over what is edited here — change the template instead.</p>
      )}
      {!isNew && !may.edit && (
        <p className="hint">You can look at this project{may.deploy || may.control ? ' and run it' : ''}, not change it: editing takes the Edit projects permission{hosts.hostIds.length > 1 ? ' on all its servers' : ''}.</p>
      )}
      {id !== null && <RunNotice projectId={id} version={deploy.finished} watching={running} onLog={deploy.showLog} onFollow={depId => { void deploy.follow(id, depId) }} onError={setError} />}

      <Tabs>
        {!isNew && <Tab value="services" current={tab} onSelect={setTab}>Services</Tab>}
        <Tab value="compose" current={tab} onSelect={setTab}>Compose</Tab>
        <Tab value="env" current={tab} onSelect={setTab}>Environment<span className="muted hide-sm">.env</span></Tab>
        {!isNew && <>
          <Tab value="files" current={tab} onSelect={setTab} title="config files the services mount: nginx.conf, appsettings.json…">Files</Tab>
          {may.secrets && <Tab value="secrets" current={tab} onSelect={setTab}>Secrets</Tab>}
          <Tab value="placement" current={tab} onSelect={setTab}>Servers{hosts.hostIds.length > 1 && <span className="muted">{hosts.hostIds.length}</span>}{plan && plan.problems.length > 0 && <span className="error"> ⚠</span>}</Tab>
          <Tab value="metrics" current={tab} onSelect={setTab}>Metrics</Tab>
          <Tab value="history" current={tab} onSelect={setTab}>History</Tab>
          {may.secrets && may.deploy && <Tab value="automate" current={tab} onSelect={setTab} title="deploy from CI with a webhook">Automate</Tab>}
        </>}
      </Tabs>

      {tab === 'compose' && !isNew && envHidden && <p className="muted">The values under <code>environment:</code> show as <code>(hidden)</code>: seeing them takes the
        Secrets permission. Leave them as they are and saving keeps what is stored.</p>}
      {tab === 'compose' && <ComposeTab form={form} agentId={agentId} isNew={isNew} agents={agents.filter(a => can(me, a.id, 'edit') || a.id === agentId)} onChange={change} />}

      {tab === 'env' && envHidden && <p className="muted">The .env is hidden: seeing and changing it takes the Secrets permission. Saving the project keeps it as it is.</p>}
      {tab === 'env' && !envHidden && (
        <>
          <label htmlFor="p-env">.env (KEY=value per line, referenced from compose as ${'{'}KEY{'}'})</label>
          <MoveToSecrets env={form.env} services={services} secretNames={secretNames} running={running} onMove={moveToSecrets} />
          <CodeEditor textareaId="p-env" language="properties" value={form.env} onChange={env => change({ env })} minHeight={240} />
        </>
      )}

      {tab === 'files' && id !== null && <FilesTab projectId={id} services={services} running={running} onMount={setMountsOf} onDeploy={svcs => { void deployServices(svcs, false) }} />}

      {tab === 'secrets' && id !== null && may.secrets && <ProjectSecrets projectId={id} services={services} compose={form.compose} onUse={useSecrets} />}

      {tab === 'services' && (
        <ProjectServices project={form.name} envFile={form.env} secretNames={secretNames} services={services} containers={hosts.containers} agents={agents} defaultAgentId={agentId} running={running} cpuLimits={agents.find(a => a.id === agentId)?.cpuLimits ?? true}
          onApply={applyLimits} onApplyLive={applyLive} onApplyAll={applyAll} onDeploy={(svc, pull) => { void deployServices([svc], pull) }} onAction={(svc, op) => { void action(op, [svc]) }} onCheckUpdates={checkUpdates}
          meshPeers={meshPeers} onPlace={(svc, ids) => placement(svc, 'placement', { agentIds: ids })} onExpose={(svc, ports) => placement(svc, 'expose', { ports })}
          autoscale={autoscale} onAutoscale={setAutoscaleOf} jobs={jobs} onRunNow={runJob} volumes={volumes} files={fileNames} onMounts={setMountsOf} warnings={plan?.warnings ?? []}
          onAddService={addService} onSettings={saveSettings} onRemoveService={removeService} may={may} />
      )}

      {tab === 'placement' && <ProjectPlacement plan={plan} running={running} onDeploy={() => { void deployServices(null, true) }} />}

      {tab === 'automate' && id !== null && may.secrets && <AutomateTab projectId={id} services={services} />}

      {tab === 'metrics' && id !== null && <MetricsTab projectId={id} services={services} containers={hosts.containers} />}

      {tab === 'history' && id !== null && <HistoryTab projectId={id} version={deploy.finished} running={running} onRollback={may.deploy ? depId => { void rollback(depId) } : undefined} onLog={deploy.showLog} onFollow={depId => { void deploy.follow(id, depId) }} onError={setError} />}

      <div className="toolbar form-actions">
        {(isNew || may.edit) && <button className="primary" disabled={running} onClick={() => { void save().then(pid => { if (pid !== null) toast('saved', 'ok') }) }}>Save</button>}
        {error && <span className="error">{error}</span>}
      </div>

      {/* a side drawer, not the bottom of the page: a deploy is started from anywhere up the page */}
      {deploy.log !== null && (
        <TaskDrawer title={running ? `${form.name}: running…` : `${form.name}: log`} text={deploy.log} running={running} result={deploy.outcome} onClose={deploy.close} onCancel={deploy.following ? undefined : deploy.cancel} />
      )}
    </>
  )
}
