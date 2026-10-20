import { useState } from 'react'
import { agentByName, type Agent, type AutoscaleStatus, type PortMode, type ServiceSummary } from '../../lib/api'
import Modal from '../../components/Modal'
import { needsAnything, needsOf, ReplicaNeeds } from './ReplicaNeeds'
import type { ServerChoice } from './ServerPicker'

type Mode = 'off' | 'recommend' | 'auto'
/** `ports`: how the replicas it starts share a fixed host port, when the file does not say yet */
export interface AutoscaleBody { max: number | null; cpu: number; agentIds: number[]; mode: 'recommend' | 'auto'; ports?: PortMode }

const MAX_REPLICAS = 50

/**
 * x-dockmaster.autoscale of a service: off, recommend (only says what it would do) or auto; the most replicas, the
 * CPU share of the limit above which one more starts, and the servers extra replicas may go to.
 */
export default function AutoscaleEditor({ project, service, agents, ownServers, choices, status, onSave, onClose }: {
  project: string
  service: ServiceSummary
  agents: Agent[]
  /** the servers the service runs on */
  ownServers: number[]
  /** servers a replica could go to; `disabled` says why one cannot */
  choices: ServerChoice[]
  status?: AutoscaleStatus
  onSave: (body: AutoscaleBody) => Promise<boolean>
  onClose: () => void
}) {
  const a = service.autoscale
  const min = service.replicas
  const [mode, setMode] = useState<Mode>(a?.mode ?? 'off')
  // as typed: a field being retyped may be empty or short for a moment
  const [maxText, setMaxText] = useState(String(a?.max ?? min + 2))
  const [cpuText, setCpuText] = useState(String(a?.cpu ?? 70))
  const [servers, setServers] = useState<number[]>(() => (a?.agentNames ?? []).map(n => agentByName(agents, n)?.id).filter((id): id is number => id != null))
  const [busy, setBusy] = useState(false)
  const [ports, setPorts] = useState<PortMode>('balance')

  const max = Number(maxText), cpu = Number(cpuText)
  // auto starts up to max replicas: the file must let them run (the server makes the room as chosen here)
  const needs = mode === 'auto' && max > 1 && needsAnything(service, max)
  const problem = mode === 'off' ? null
    : !maxText || !Number.isInteger(max) || max < min || max > MAX_REPLICAS ? `the most replicas is between ${min} (as written) and ${MAX_REPLICAS}`
    : !cpuText || cpu < 10 || cpu > 95 ? 'the threshold is between 10 and 95 % of the CPU limit'
    : null

  async function save() {
    if (problem) return
    setBusy(true)
    const ok = await onSave(mode === 'off' ? { max: null, cpu: 70, agentIds: [], mode: 'recommend' }
      : { max, cpu, agentIds: servers, mode, ports: needs && needsOf(service, max).ports ? ports : undefined })
    setBusy(false)
    if (ok) onClose()
  }

  // servers picked earlier that can no longer be chosen stay listed (and can be unticked), so a save keeps them
  const listed = [...choices, ...servers.filter(id => !choices.some(c => c.agent.id === id)).flatMap(id => {
    const agent = agents.find(x => x.id === id)
    return agent ? [{ agent, disabled: 'not in the mesh with the project\'s server' }] : []
  })]
  const toggle = (id: number, on: boolean) => setServers(prev => on ? [...prev, id] : prev.filter(x => x !== id))
  const step = (d: number) => setMaxText(String(Math.min(MAX_REPLICAS, Math.max(min, (Number(maxText) || min) + d))))
  return (
    <Modal label={`Autoscale ${service.name}`} onClose={onClose} onSubmit={() => { void save() }}>
      <div className="seg" role="group" aria-label="mode">
        {(['off', 'recommend', 'auto'] as const).map(m => (
          <button key={m} type="button" className={m === mode ? 'on' : undefined} aria-pressed={m === mode} onClick={() => setMode(m)}>{m}</button>
        ))}
      </div>
      <p className="mini">
        {mode === 'off' && (a?.mode === 'auto' ? 'The replicas it added are removed; deploy the project afterwards to take the balancers out of its layout.' : 'The replica count stays as written.')}
        {mode === 'recommend' && 'Watches the CPU and reports under Events (and to Seq) what it would do — nothing is started or removed. Run it for a while to see whether and when it would act.'}
        {mode === 'auto' && 'Starts a replica when the running ones stay above the threshold for 3 minutes, removes it after 15 calm minutes. The service is reached through the balancers on every server from the next deploy on (point a reverse proxy at the address in the Servers tab); deploy after switching to auto.'}
      </p>
      {mode !== 'off' && <>
        {!service.cpuLimit && <p className="error">The service has no CPU limit: the autoscaler measures against it, so it does nothing until one is set.</p>}
        {mode === 'auto' && service.expose.length === 0 && servers.some(id => !ownServers.includes(id)) &&
          <p className="warn-text">The service exposes no port: replicas on other servers would get no connections.</p>}
        <div className="row">
          <div className="narrow">
            <label htmlFor="as-max">Replicas at most</label>
            <span className="stepper">
              <button type="button" disabled={max <= min} onClick={() => step(-1)} title="one less at most">−</button>
              <input id="as-max" className="cell" inputMode="numeric" value={maxText} onChange={e => setMaxText(e.target.value.replace(/\D/g, ''))} />
              <button type="button" disabled={max >= MAX_REPLICAS} onClick={() => step(1)} title="one more at most">+</button>
            </span>
            <div className="mini">at least {min}, as written</div>
          </div>
          <div className="narrow">
            <label htmlFor="as-cpu">One more above</label>
            <input id="as-cpu" inputMode="numeric" value={cpuText} onChange={e => setCpuText(e.target.value.replace(/\D/g, ''))} />
            <div className="mini">% of the CPU limit, average of the replicas</div>
          </div>
        </div>
        {needs && <ReplicaNeeds project={project} service={service} replicas={max} mode={ports} onMode={setPorts} />}
        <div className="field-label">Servers extra replicas may go to</div>
        <div className="checks">
          {listed.map(c => {
            const on = servers.includes(c.agent.id)
            return (
              <label key={c.agent.id} className={c.disabled ? 'muted' : undefined} title={c.disabled}>
                <input type="checkbox" checked={on} disabled={!!c.disabled && !on} onChange={e => toggle(c.agent.id, e.target.checked)} />
                {c.agent.name}{!c.agent.online && <span className="muted"> (offline)</span>}{c.disabled && <span className="mini"> — {c.disabled}</span>}
              </label>
            )
          })}
        </div>
        <p className="mini">None ticked: the servers the service runs on. A replica goes where most memory is free after the limits of what runs there, another server first; a server that needs its memory or CPU back gets it back first.</p>
      </>}
      {status && mode !== 'off' && <p className="mini">Now: {status.desired} replica{status.desired === 1 ? '' : 's'}{status.mode === 'recommend' ? ' (would run)' : ''} · {status.state}{status.lastChange ? ` · last: ${status.lastChange}` : ''}</p>}
      {problem && <p className="error">{problem}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy || !!problem}>Save</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
