import { useState } from 'react'
import { Link } from 'react-router-dom'
import { agentByName, type Agent, type AutoscaleStatus, type Container, type JobStatus, type MountEdit, type Perm, type PortMode, type ProjectVolumes, type ServiceSummary } from '../../lib/api'
import { allowed } from '../../lib/access'
import { bytes, hasUpdate, shortImage } from '../../lib/fmt'
import Icon from '../../components/Icon'
import { ICON } from '../../components/icons'
import RowMenu from '../../components/RowMenu'
import AutoscaleEditor, { type AutoscaleBody } from './AutoscaleEditor'
import { needsAnything, needsOf, ReplicaDialog } from './ReplicaNeeds'
import { unkeptPaths } from './dataPaths'
import MountsEditor from './MountsEditor'
import ServiceForm, { type ServiceFormValue } from './ServiceForm'
import ServerPicker, { type ServerChoice } from './ServerPicker'
import JobPanel, { jobLine } from './JobPanel'
import { ImageDot, JobMark, RestartMark, StateDot } from '../../components/Status'

/** `ports`: how several replicas share a fixed host port, when the file does not say yet */
export interface Limits { replicas: number; memory: string; cpus: string; ports?: PortMode }
/** One service's pending change; `live` = only limits changed, applied to the running replicas without a redeploy. */
export interface LimitChange { service: string; live: boolean; limits: Limits; memory: string | null; cpus: string | null }

interface Props {
  project: string
  services: ServiceSummary[]
  containers: Container[]
  agents: Agent[]
  /** the project's server: services without a placement run there */
  defaultAgentId: number
  /** servers that share a network with the project's server (undefined while not known: none is held back) */
  meshPeers?: ReadonlySet<number>
  running: boolean
  cpuLimits: boolean
  // the change handlers resolve true when the change was saved; a failed one keeps what was typed
  onApply: (service: string, limits: Limits) => Promise<boolean>
  onApplyLive: (service: string, memory: string | null, cpus: string | null) => Promise<boolean>
  onApplyAll: (changes: LimitChange[]) => Promise<boolean>
  /** the servers a service runs on: one, or several to spread its replicas over */
  onPlace: (service: string, agentIds: number[]) => Promise<boolean>
  onExpose: (service: string, ports: string[]) => Promise<boolean>
  /** where the autoscaler stands with each autoscaled service */
  autoscale: AutoscaleStatus[]
  onAutoscale: (service: string, body: AutoscaleBody) => Promise<boolean>
  /** the jobs (services with a schedule) as the scheduler sees them: next run, last runs */
  jobs: JobStatus[]
  /** starts a job now, outside its schedule */
  onRunNow: (service: string) => Promise<boolean>
  /** volumes of the project's servers and the data paths of the images (loaded with the tab) */
  volumes?: ProjectVolumes
  /** the project's files, mountable into a service */
  files: string[]
  /** the project's .env, for the ${VARIABLES} the service form offers */
  envFile: string
  secretNames: string[]
  /** where a service keeps its data: written into the compose file, `deploy` re-creates the service with it */
  onMounts: (service: string, mounts: MountEdit[], deploy: boolean) => Promise<boolean>
  /** what the plan warns about (data left behind, copies per server…), shown over the table */
  warnings: string[]
  /** a new service from the form; `deploy` starts it */
  onAddService: (value: ServiceFormValue, deploy: boolean) => Promise<boolean>
  /** image, ports, environment, restart of a service */
  onSettings: (service: string, value: ServiceFormValue, deploy: boolean) => Promise<boolean>
  onRemoveService: (service: string) => void
  onDeploy: (service: string, pull: boolean) => void
  onAction: (service: string, op: 'start' | 'stop' | 'restart') => void
  onCheckUpdates: () => Promise<void>
  /** what the user may do with the project: without edit the table only shows, the actions they lack are not offered */
  may: { edit: boolean; deploy: boolean; control: boolean }
}

/** Services of a project with quick edit of replicas / limits / server / exposed ports (written into the compose file) and image freshness. */
export default function ProjectServices({ project, envFile, secretNames, services, containers, agents, defaultAgentId, meshPeers, running, cpuLimits, onApply, onApplyLive, onApplyAll, onPlace, onExpose, autoscale, onAutoscale, jobs, onRunNow, volumes, files, onMounts, warnings, onAddService, onSettings, onRemoveService, onDeploy, onAction, onCheckUpdates, may }: Props) {
  // fields the user may not change are shown, not editable
  const locked = running || !may.edit
  const [scaling, setScaling] = useState<string | null>(null)
  // the job panel (schedule and last runs) of a service
  const [jobOpen, setJobOpen] = useState<string | null>(null)
  const [mounting, setMounting] = useState<string | null>(null)
  // the service form: a service's name, or '' for a new one
  const [editing, setEditing] = useState<string | null>(null)
  const [edits, setEdits] = useState<Record<string, Limits>>({})
  const [placeEdits, setPlaceEdits] = useState<Record<string, number[]>>({})
  const [exposeEdits, setExposeEdits] = useState<Record<string, string>>({})
  const [checking, setChecking] = useState(false)
  // many services: narrowed by a part of the name or image
  const [find, setFind] = useState('')
  const needle = find.trim().toLowerCase()
  const shown = needle ? services.filter(s => s.name.toLowerCase().includes(needle) || (s.image ?? '').toLowerCase().includes(needle)) : services

  const current = (s: ServiceSummary): Limits => edits[s.name] ?? { replicas: s.replicas, memory: s.memoryLimit ?? '', cpus: s.cpuLimit ?? '' }
  const changed = (s: ServiceSummary) => {
    const e = edits[s.name]
    return !!e && (e.replicas !== s.replicas || e.memory !== (s.memoryLimit ?? '') || e.cpus !== (s.cpuLimit ?? ''))
  }
  const set = (s: ServiceSummary, patch: Partial<Limits>) => setEdits(prev => ({ ...prev, [s.name]: { ...current(s), ...patch } }))

  /** only limits changed and none removed: docker can apply them to the running replicas without a redeploy */
  const liveable = (s: ServiceSummary) => {
    const v = current(s)
    return v.replicas === s.replicas && !(v.memory === '' && s.memoryLimit) && !(v.cpus === '' && s.cpuLimit)
  }

  const change = (s: ServiceSummary, modes: Record<string, PortMode> = {}): LimitChange => {
    const v = { ...current(s), ports: modes[s.name] }
    return { service: s.name, live: liveable(s), limits: v, memory: v.memory !== (s.memoryLimit ?? '') ? v.memory : null, cpus: v.cpus !== (s.cpuLimit ?? '') ? v.cpus : null }
  }

  // several replicas where the file holds them to one: the dialog says what changes and asks how to share the ports
  const [asking, setAsking] = useState<{ items: { service: ServiceSummary; replicas: number }[]; modes: Record<string, PortMode>; done: (modes: Record<string, PortMode> | null) => void } | null>(null)
  /** The port choice of each service that needs one ({} when nothing needs asking); null when cancelled. */
  function askReplicas(items: { service: ServiceSummary; replicas: number }[]) {
    const needing = items.filter(i => needsAnything(i.service, i.replicas))
    if (needing.length === 0) return Promise.resolve<Record<string, PortMode> | null>({})
    return new Promise<Record<string, PortMode> | null>(resolve => setAsking({
      items: needing,
      modes: Object.fromEntries(needing.map(i => [i.service.name, 'balance' as PortMode])),
      done: modes => {
        setAsking(null)
        // only the services whose ports need the choice send one
        resolve(modes && Object.fromEntries(Object.entries(modes).filter(([name]) => needing.some(i => i.service.name === name && needsOf(i.service, i.replicas).ports))))
      },
    }))
  }
  const newReplicas = (rows: ServiceSummary[]) => rows.filter(s => current(s).replicas !== s.replicas).map(s => ({ service: s, replicas: current(s).replicas }))

  const without = <T,>(name: string) => (prev: Record<string, T>) => { const n = { ...prev }; delete n[name]; return n }
  function reset(s: ServiceSummary) { setEdits(without(s.name)); setPlaceEdits(without(s.name)); setExposeEdits(without(s.name)) }
  function undoAll() { setEdits({}); setPlaceEdits({}); setExposeEdits({}) }

  /** Server and exposed ports first, one after another (each is a write of the compose file); false when one failed. */
  async function applyPlacement(s: ServiceSummary) {
    if (placeDirty(s)) {
      if (!await onPlace(s.name, placeEdits[s.name])) return false
      setPlaceEdits(without(s.name))
    }
    if (exposeDirty(s)) {
      if (!await onExpose(s.name, exposeText(s).split(/[\s,]+/).filter(Boolean))) return false
      setExposeEdits(without(s.name))
    }
    return true
  }

  async function apply(s: ServiceSummary) {
    const modes = changed(s) ? await askReplicas(newReplicas([s])) : {}
    if (!modes || !await applyPlacement(s) || !changed(s)) return
    const c = change(s, modes)
    if (await (c.live ? onApplyLive(s.name, c.memory, c.cpus) : onApply(s.name, c.limits))) setEdits(without(s.name))
  }

  // several rows changed: one go, so the services that need a redeploy share one deploy
  async function applyAll() {
    const rows = pending
    const modes = await askReplicas(newReplicas(rows.filter(changed)))
    if (!modes) return
    for (const s of rows) if (!await applyPlacement(s)) return
    const limits = rows.filter(changed).map(s => change(s, modes))
    if (limits.length && await onApplyAll(limits)) setEdits({})
  }

  /** A file saved with several replicas that compose refuses (a container_name, a fixed port): fixed as chosen, then redeployed. */
  async function fix(s: ServiceSummary) {
    const modes = await askReplicas([{ service: s, replicas: s.replicas }])
    if (modes) await onApply(s.name, { replicas: s.replicas, memory: s.memoryLimit ?? '', cpus: s.cpuLimit ?? '', ports: modes[s.name] })
  }

  async function check() {
    setChecking(true)
    try { await onCheckUpdates() } finally { setChecking(false) }
  }

  // servers a service can be placed on: the project's own, and the ones in the mesh; one without a network in
  // common with the project's server is shown but cannot be picked (the two could not reach each other)
  const defaultName = agents.find(a => a.id === defaultAgentId)?.name ?? 'the project\'s server'
  const choices: ServerChoice[] = agents.filter(a => a.id === defaultAgentId || a.meshIp).map(a => ({
    agent: a,
    disabled: a.id === defaultAgentId || !meshPeers || meshPeers.has(a.id) ? undefined : `no network in common with ${defaultName}`,
  }))
  const multiHost = choices.length > 1
  const serversOf = (s: ServiceSummary) => {
    const ids = s.agentNames.map(n => agentByName(agents, n)?.id).filter((id): id is number => id != null)
    return ids.length > 0 ? ids : [defaultAgentId]
  }
  const unknownOf = (s: ServiceSummary) => s.agentNames.filter(n => !agentByName(agents, n))
  const same = (a: number[], b: number[]) => a.length === b.length && a.every((x, i) => x === b[i])
  const spanning = services.some(s => !same(serversOf(s), [defaultAgentId]))
  const nameOf = (id: number | undefined) => agents.find(a => a.id === id)?.name ?? ''

  const serversShown = (s: ServiceSummary) => placeEdits[s.name] ?? serversOf(s)
  const placeDirty = (s: ServiceSummary) => s.name in placeEdits && !same(placeEdits[s.name], serversOf(s))
  const exposeText = (s: ServiceSummary) => exposeEdits[s.name] ?? s.expose.join(', ')
  const exposeDirty = (s: ServiceSummary) => s.name in exposeEdits && exposeEdits[s.name] !== s.expose.join(', ')
  /** anything changed in the row: nothing is written until its ✓ */
  const rowDirty = (s: ServiceSummary) => changed(s) || placeDirty(s) || exposeDirty(s)
  const pending = services.filter(rowDirty)
  /** what the row's ✓ does, for its tooltip */
  const applyTitle = (s: ServiceSummary) => !changed(s) ? 'save: written into the compose file, the next deploy applies it'
    : liveable(s) ? 'apply live: update the running containers now, no restart' : 'apply & deploy: save and redeploy this service'

  /** start when nothing of the service runs, restart / stop when something does; a job: run now, or stop the run going on */
  const serviceMenu = (name: string, mine: Container[], job: boolean) => {
    const anyRunning = mine.some(c => c.state === 'running')
    const items: { label: string; onClick: () => void; need?: Perm; danger?: boolean; disabled?: boolean; title?: string }[] = [
      { label: job ? 'settings (image, environment, schedule)…' : 'settings (image, ports, environment)…', need: 'edit', onClick: () => setEditing(name), disabled: running },
      { label: 'data…', need: 'edit', onClick: () => setMounting(name), disabled: running },
      { label: 'pull & deploy', need: 'deploy', onClick: () => onDeploy(name, true), disabled: running },
      { label: 'deploy (no pull)', need: 'deploy', onClick: () => onDeploy(name, false), disabled: running },
      ...(job
        ? [{ label: 'runs & schedule…', onClick: () => setJobOpen(name) },
          anyRunning ? { label: 'stop the run', need: 'control' as Perm, onClick: () => onAction(name, 'stop'), disabled: running }
            : { label: 'run now', need: 'control' as Perm, onClick: () => { void onRunNow(name) }, disabled: running || mine.length === 0, title: mine.length ? 'starts it now, outside its schedule' : 'deploy first: no container on the server yet' }]
        : anyRunning
        ? [{ label: 'restart', need: 'control' as Perm, onClick: () => onAction(name, 'restart'), disabled: running }, { label: 'stop', need: 'control' as Perm, onClick: () => onAction(name, 'stop'), disabled: running }]
        : [mine.length
          ? { label: 'start', need: 'control' as Perm, onClick: () => onAction(name, 'start'), disabled: running }
          : { label: 'start (deploy first if it never ran)', need: 'deploy' as Perm, onClick: () => onDeploy(name, false), disabled: running }]),
      { label: 'remove service', need: 'edit', danger: true, onClick: () => onRemoveService(name), disabled: running || services.length < 2, title: services.length < 2 ? 'the last service: delete the project instead' : undefined },
    ]
    return allowed(items, perm => perm === 'edit' ? may.edit : perm === 'deploy' ? may.deploy : perm === 'control' ? may.control : true)
  }

  return (
    <>
      <div className="toolbar">
        <span className="muted">Values are written into the compose file. Memory / CPU changes are applied <b>live</b> to the running replicas (no restart); a new replica count only starts or removes replicas, the running ones stay.{multiHost && <> A service on another server is reached by the others through the ports it <b>exposes</b>.</>}</span>
        <div className="spacer" />
        {pending.length > 1 && (
          <>
            <button className="sm primary" disabled={running} onClick={() => { void applyAll() }}
              title="limit changes are applied live; services with a new replica count are redeployed together, in one deploy">
              apply {pending.length} changes
            </button>
            <button className="sm" onClick={undoAll}>undo all</button>
          </>
        )}
        {services.length > 5 && <input type="search" className="service-find" value={find} onChange={e => setFind(e.target.value)} placeholder={`find among ${services.length} services…`} aria-label="find a service" />}
        <button className="sm" disabled={checking} onClick={check}>{checking ? 'checking…' : 'check for updates'}</button>
        {may.edit && <button className="sm primary" disabled={running} onClick={() => setEditing('')} title="a ready-made app (Postgres, Redis, Nginx…) or any image">+ add service</button>}
      </div>
      {warnings.map(w => <p key={w} className="warn-text mini">⚠ {w}</p>)}
      <div className="table-wrap scroll-x"><table>
        <thead><tr>
          <th>Service</th><th className="hide-sm">Image</th><th title="where the service keeps files that must survive an update">Data</th>{multiHost && <th>Server</th>}{multiHost && <th>Exposes</th>}<th>Replicas</th><th title="more replicas while the running ones are busy; for a job: when it runs">Autoscale / schedule</th><th>Memory limit</th><th>CPU limit</th>
          <th className="hide-md">Strategy</th><th className="hide-lg">Ready</th><th>Containers</th><th></th>
        </tr></thead>
        <tbody>
          {needle && shown.length === 0 && <tr><td colSpan={99} className="muted">No service has “{find.trim()}” in its name or image.</td></tr>}
          {shown.map(s => {
            const mine = containers.filter(c => c.service === s.name)
            const upd = mine.find(c => hasUpdate(c.update)) ?? mine.find(c => c.update === 'latest') ?? mine[0]
            const v = current(s)
            const dirty = rowDirty(s)
            const servers = serversShown(s)
            const job = !!s.schedule
            const line = job ? jobLine(jobs.find(j => j.service === s.name)) : null
            return (
              <tr key={s.name}>
                <td className="nowrap">{may.edit
                  ? <button type="button" className="link inherit service-name" onClick={() => setEditing(s.name)} disabled={running} title="settings: image, ports, environment, restart"><b>{s.name}</b></button>
                  : <b className="service-name">{s.name}</b>}</td>
                <td className="muted trunc hide-sm" title={s.image}>{upd && <ImageDot status={upd.update} detail={upd.updateDetail} />}{shortImage(s.image)}</td>
                <td className="nowrap"><DataCell s={s} volumes={volumes} disabled={locked} onOpen={() => setMounting(s.name)} /></td>
                {multiHost && (
                  <td className="nowrap">
                    <ServerPicker label={`servers of ${s.name}`} choices={choices} selected={servers} replicas={v.replicas} unknown={unknownOf(s)} disabled={locked}
                      onChange={ids => setPlaceEdits(prev => ({ ...prev, [s.name]: ids }))} />
                  </td>
                )}
                {multiHost && (
                  <td className="nowrap">
                    <input className="cell expose" aria-label={`ports ${s.name} exposes`} value={exposeText(s)} placeholder="—" disabled={!may.edit}
                      onChange={e => setExposeEdits(prev => ({ ...prev, [s.name]: e.target.value }))}
                      onKeyDown={e => { if (e.key === 'Enter' && dirty && !running) void apply(s) }}
                      title="ports other services use, e.g. 5432 or 53/udp; on another server they are published on the mesh address" />
                  </td>
                )}
                <td>{job ? <span className="badge muted" title="a job runs one container at a time">job</span> : <>
                  <span className="stepper">
                    <button type="button" disabled={!may.edit || v.replicas <= 0} onClick={() => set(s, { replicas: Math.max(0, v.replicas - 1) })} title="one replica less">−</button>
                    <input className="cell" inputMode="numeric" aria-label={`replicas of ${s.name}`} value={v.replicas} disabled={!may.edit} onChange={e => { const n = Number(e.target.value.replace(/\D/g, '')); set(s, { replicas: Math.min(100, n) }) }} />
                    <button type="button" disabled={!may.edit || v.replicas >= 100} onClick={() => set(s, { replicas: Math.min(100, v.replicas + 1) })} title="one replica more">+</button>
                  </span>
                  <ReplicaHint s={s} replicas={v.replicas} disabled={locked} onFix={() => { void fix(s) }} />
                </>}</td>
                <td className="nowrap">{line
                  ? <button type="button" className="link job-line" onClick={() => setJobOpen(s.name)} title={line.title + '\nopen: last runs, run now'}>{line.text}</button>
                  : <AutoscaleCell s={s} status={autoscale.find(x => x.service === s.name)} disabled={locked} onOpen={() => setScaling(s.name)} />}</td>
                <td><input className="cell memory" aria-label={`memory limit of ${s.name}`} value={v.memory} placeholder="none" disabled={!may.edit} onChange={e => set(s, { memory: e.target.value })} title="e.g. 256M, 1G; empty = no limit" /></td>
                <td><input className="cell cpus" aria-label={`CPU limit of ${s.name}`} value={v.cpus} placeholder="none" onChange={e => set(s, { cpus: e.target.value })} disabled={!may.edit || !cpuLimits && !s.cpuLimit} title={cpuLimits ? 'cores, e.g. 0.5 or 2; empty = no limit' : 'this host cannot limit CPU (no CFS quota, e.g. Synology)'} /></td>
                <td className="hide-md nowrap">{job ? <span className="badge muted" title="created at the deploy, started by Dockmaster on its schedule">job</span> : s.rolling ? <span className="badge ok" title="zero-downtime rolling update">rolling{s.parallelism > 1 ? ` ×${s.parallelism}` : ''}{s.updateDelay ? `, delay ${s.updateDelay}` : ''}</span> : <span className="badge muted" title="container is recreated (short downtime)">recreate</span>}</td>
                <td className="mini hide-lg nowrap">{job ? 'exit code 0' : s.readiness}</td>
                <td>{mine.length === 0 ? <span className="muted">—</span> : <span className="replica-dots">{mine.map(c => (
                  <span key={c.id} className="replica-state">
                    <Link className="container-link" to={`/agents/${c.agentId ?? defaultAgentId}/containers/${encodeURIComponent(c.name)}`} title={`open ${c.name}: state, env, network, mounts, logs`}>
                      {c.jobInfo
                        ? <JobMark c={c} className="job-cell" />
                        : <StateDot state={c.state} health={c.health} label extra={(c.state === 'running' ? bytes(c.memoryUsage) : '') + (spanning && c.agentId ? ` @${nameOf(c.agentId)}` : '') || undefined} />}
                    </Link>
                    {!c.jobInfo && <RestartMark c={c} />}
                  </span>
                ))}</span>}</td>
                <td className="menu-cell"><div className="icon-actions">
                  {dirty ? (
                    <>
                      <button className="icon primary" disabled={running} onClick={() => { void apply(s) }} aria-label={applyTitle(s).split(':')[0]}
                        title={applyTitle(s)}><Glyph d={ICON.check} /></button>
                      <button className="icon" onClick={() => reset(s)} aria-label="undo changes" title="undo changes"><Glyph d={ICON.undo} /></button>
                    </>
                  ) : (
                    <>
                      {upd && hasUpdate(upd.update) && may.deploy && (
                        <button className="icon primary" disabled={running} onClick={() => onDeploy(s.name, true)} aria-label="pull and deploy" title="a newer image is available — pull & deploy"><Glyph d={ICON.download} /></button>
                      )}
                      <RowMenu title={`actions for ${s.name}`} items={serviceMenu(s.name, mine, job)} />
                    </>
                  )}
                </div></td>
              </tr>
            )
          })}
        </tbody>
      </table></div>
      <p className="mini">Memory: <code>512M</code>, <code>1.5G</code>. CPU: cores, <code>0.5</code> = half a core. Empty removes the limit. Values go to <code>deploy.replicas</code> and <code>deploy.resources.limits</code> (or the service's <code>mem_limit</code> / <code>cpus</code> if the file already uses them); comments in the compose file are kept.
        {multiHost && <> Servers go to <code>x-dockmaster.agent</code> (several: <code>agents</code>, the replicas are spread over them), exposed ports to <code>expose</code>. A service spread over servers is reached through a balancer on every server, also where replicas of it run, so all of them get their share (TCP; one replica on one server holds its port itself).</>}
        {' '}Autoscale goes to <code>x-dockmaster.autoscale</code>, a job's schedule to <code>x-dockmaster.schedule</code> (a service's settings → restart it → <i>it is a job</i>).</p>
      {editing !== null && (editing === '' || services.some(s => s.name === editing)) && (
        <ServiceForm service={services.find(s => s.name === editing)} services={services} envFile={envFile} secretNames={secretNames} running={running} onClose={() => setEditing(null)}
          onSave={(value, deploy) => editing === '' ? onAddService(value, deploy) : onSettings(editing, value, deploy)} />
      )}
      {mounting && services.some(s => s.name === mounting) && (() => {
        const s = services.find(x => x.name === mounting)!
        return <MountsEditor service={s} services={services} project={project} volumes={volumes} files={files} serverIds={serversOf(s)} agents={agents} running={running}
          onSave={(mounts, deploy) => onMounts(s.name, mounts, deploy)} onClose={() => setMounting(null)} />
      })()}
      {jobOpen && services.some(s => s.name === jobOpen) && (() => {
        const s = services.find(x => x.name === jobOpen)!
        const c = containers.find(x => x.service === s.name)
        return <JobPanel service={s} status={jobs.find(j => j.service === s.name)} running={running}
          containerLink={c ? `/agents/${c.agentId ?? defaultAgentId}/containers/${encodeURIComponent(c.name)}` : undefined}
          onRunNow={() => onRunNow(s.name)} onStop={() => onAction(s.name, 'stop')} onEdit={() => { setJobOpen(null); setEditing(s.name) }} onClose={() => setJobOpen(null)} />
      })()}
      {scaling && services.some(s => s.name === scaling) && (() => {
        const s = services.find(x => x.name === scaling)!
        return <AutoscaleEditor project={project} service={s} agents={agents} ownServers={serversOf(s)} choices={choices}
          status={autoscale.find(x => x.service === s.name)} onSave={body => onAutoscale(s.name, body)} onClose={() => setScaling(null)} />
      })()}
      {asking && (
        <ReplicaDialog project={project} items={asking.items} modes={asking.modes}
          onMode={(name, mode) => setAsking(a => a && { ...a, modes: { ...a.modes, [name]: mode } })}
          onContinue={() => asking.done(asking.modes)} onCancel={() => asking.done(null)} />
      )}
    </>
  )
}

/**
 * Where a service keeps its data, in its row: the volume or folder, or none; a warning when its image keeps data in a
 * path nothing keeps (lost on the next update) or a volume has no name.
 */
function DataCell({ s, volumes, disabled, onOpen }: { s: ServiceSummary; volumes?: ProjectVolumes; disabled: boolean; onOpen: () => void }) {
  const kept = s.mounts.filter(m => m.type === 'volume' || m.type === 'bind')
  const unkept = unkeptPaths(s, volumes)
  const unnamed = s.mounts.filter(m => m.type === 'anonymous').length
  const text = kept.length === 0 ? 'none' : kept.length === 1 ? kept[0].source : `${kept.length} places`
  const warn = unkept.length > 0 ? `the image keeps data in ${unkept.join(', ')} — not kept, lost on the next update`
    : unnamed ? `${unnamed} unnamed volume(s): a new container starts empty` : ''
  const title = (kept.map(m => `${m.source} → ${m.target}${m.readOnly ? ' (read-only)' : ''}`).join('\n') || 'nothing is kept: the service starts from its image every time')
    + (warn ? '\n⚠ ' + warn : '')
  return (
    <button type="button" className={'cell-open' + (kept.length === 0 && !warn ? ' off' : '')} disabled={disabled} onClick={onOpen} title={title}>
      {text}{warn && <span className="warn-text"> ⚠</span>}
    </button>
  )
}

/**
 * Under a service's replica count: what a new count changes (asked on ✓), a way to fix a saved file compose would
 * refuse, or the balancer that holds its ports.
 */
function ReplicaHint({ s, replicas, disabled, onFix }: { s: ServiceSummary; replicas: number; disabled: boolean; onFix: () => void }) {
  const need = needsOf(s, replicas)
  if (replicas !== s.replicas && (need.name || need.ports))
    return <div className="mini warn-text" title="asked on ✓">{[need.name && 'the name goes', need.ports && 'port: balancer or range'].filter(Boolean).join(', ')}</div>
  if (replicas === s.replicas && (need.name || need.ports))
    return (
      <button type="button" className="link mini warn-text" disabled={disabled} onClick={onFix}
        title={`compose refuses ${replicas} replicas with ${need.name ? 'one container name' : 'one fixed port'}: fix it and redeploy`}>
        ⚠ fix for {replicas} replicas
      </button>
    )
  // at 1 replica the service holds its ports itself, unless a rolling update or the autoscaler needs the balancer
  const balancer = s.balance && (s.replicas > 1 || s.rolling || (s.autoscale?.mode === 'auto' && s.autoscale.max > 1))
  return balancer ? <div className="mini muted" title={`${s.name}-balancer holds the ports and spreads the connections over the replicas`}>via balancer</div> : null
}

/** A service's autoscaling in its row: off, or the mode with its range and the replicas it runs (would run) now. */
function AutoscaleCell({ s, status, disabled, onOpen }: { s: ServiceSummary; status?: AutoscaleStatus; disabled: boolean; onOpen: () => void }) {
  const a = s.autoscale
  if (!a) return <button type="button" className="cell-open off" disabled={disabled} onClick={onOpen} title="start more replicas when the running ones are busy">off</button>
  // auto: what runs now; recommend: what it would run, when that is more than the file says
  const now = !status ? '' : a.mode === 'auto' ? ` · ${status.desired} now` : status.desired > s.replicas ? ` · would ${status.desired}` : ''
  return (
    <button type="button" className="cell-open" disabled={disabled} onClick={onOpen}
      title={`${a.mode}: ${s.replicas}–${a.max} replicas, one more above ${a.cpu}% of the CPU limit${status ? ` — ${status.state}` : ''}`}>
      {a.mode === 'auto' ? 'auto' : 'watch'} {s.replicas}–{a.max}{now}{!s.cpuLimit && <span className="error"> ⚠</span>}
    </button>
  )
}

/** The icon of a small square action button. */
const Glyph = ({ d }: { d: string }) => <Icon d={d} size={14} stroke={2.2} />
