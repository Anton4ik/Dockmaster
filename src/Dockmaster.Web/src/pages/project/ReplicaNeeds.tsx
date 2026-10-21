import type { PortMode, ServiceSummary } from '../../lib/api'
import Modal from '../../components/Modal'

/** What several replicas of a service need that the file does not give yet: its container_name goes, its fixed ports need a way. */
export function needsOf(s: ServiceSummary, replicas: number) {
  return { name: s.hasContainerName && replicas > 1, ports: replicas > (s.portRoom ?? Infinity) }
}
export const needsAnything = (s: ServiceSummary, replicas: number) => { const n = needsOf(s, replicas); return n.name || n.ports }

/** "127.0.0.1:8765:8080/tcp" → its parts; null for what is not one fixed port or a range. */
function fixedPort(entry: string) {
  const m = /^(?:(.*):)?(\d+)(?:-(\d+))?:(\d+)(\/\w+)?$/.exec(entry)
  return m ? { ip: m[1], from: Number(m[2]), to: Number(m[3] ?? m[2]), target: m[4], proto: m[5] ?? '' } : null
}

/** The fixed host ports as they read, and as ranges for `replicas` ("8765:8080" → "8765-8766:8080"). */
function portsOf(s: ServiceSummary, replicas: number) {
  return (s.portList ?? []).map(fixedPort).filter(p => p !== null && p.to - p.from + 1 < replicas).map(p => {
    const ip = p!.ip ? p!.ip + ':' : ''
    return { now: `${ip}${p!.from}${p!.to > p!.from ? `-${p!.to}` : ''}`, range: `${ip}${p!.from}-${p!.from + replicas - 1}:${p!.target}${p!.proto}`, first: p!.from, target: p!.target }
  })
}

/**
 * What changes when a service gets several replicas, and the choice for its fixed ports: a balancer holds them and
 * spreads the connections (recommended), or each replica takes a port of a range.
 */
export function ReplicaNeeds({ project, service: s, replicas, mode, onMode }: {
  project: string
  service: ServiceSummary
  replicas: number
  mode: PortMode
  onMode: (mode: PortMode) => void
}) {
  const need = needsOf(s, replicas)
  const name = s.containerName ?? s.name
  const ports = portsOf(s, replicas)
  const hostPorts = ports.map(p => p.now).join(', ')
  const radio = `ports-${s.name}`
  return (
    <div className="replica-needs">
      {need.name && (
        <p className="mini">
          <b>Container name.</b> Several replicas cannot share the name <code>{name}</code>: it is removed from the compose file and the containers
          become <code>{project}-{s.name}-1</code>, <code>-2</code>…{' '}
          {name !== s.name
            ? <span className="warn-text">Services that connect to <code>{name}</code> must use <code>{s.name}</code> instead.</span>
            : <>Other services still reach it as <code>{s.name}</code>.</>}
        </p>
      )}
      {need.ports && (
        <>
          <p className="mini"><b>Port {hostPorts}.</b> Only one container can hold a port of the server. How should {replicas} replicas share it?</p>
          <div className="choice-cards" role="radiogroup" aria-label={`ports of ${s.name}`}>
            <label className={mode === 'balance' ? 'on' : undefined}>
              <input type="radio" name={radio} checked={mode === 'balance'} onChange={() => onMode('balance')} />
              <span>
                <b>Balancer</b> <span className="badge ok">recommended</span>
                <span className="mini">Port {hostPorts} stays. A small HAProxy container, <code>{s.name}{'-balancer'}</code>, holds it and spreads the connections over all replicas; replicas are updated one by one without downtime, and the ones the autoscaler adds are found by themselves.</span>
              </span>
            </label>
            <label className={mode === 'range' ? 'on' : undefined}>
              <input type="radio" name={radio} checked={mode === 'range'} onChange={() => onMode('range')} />
              <span>
                <b>Port range</b>
                <span className="mini">Each replica gets a port of its own: {ports.map(p => <code key={p.range}>{p.range}</code>)}. Port {ports[0]?.first} reaches only the first replica — for when something in front (a proxy, the clients) picks the replica itself. Replicas are re-created on an update.</span>
              </span>
            </label>
          </div>
        </>
      )}
    </div>
  )
}

/** The services about to get several replicas that need something, with the port choice of each: Continue or Cancel. */
export function ReplicaDialog({ project, items, modes, onMode, onContinue, onCancel }: {
  project: string
  items: { service: ServiceSummary; replicas: number }[]
  modes: Record<string, PortMode>
  onMode: (service: string, mode: PortMode) => void
  onContinue: () => void
  onCancel: () => void
}) {
  return (
    <Modal label={items.length === 1 ? `${items[0].replicas} replicas of ${items[0].service.name}` : 'Several replicas'} onClose={onCancel} onSubmit={onContinue}>
      {items.map(({ service, replicas }) => (
        <div key={service.name}>
          {items.length > 1 && <div className="field-label">{service.name} × {replicas}</div>}
          <ReplicaNeeds project={project} service={service} replicas={replicas} mode={modes[service.name] ?? 'balance'} onMode={m => onMode(service.name, m)} />
        </div>
      ))}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit">Continue</button>
        <button type="button" onClick={onCancel}>Cancel</button>
      </div>
    </Modal>
  )
}
