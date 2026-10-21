import { Link } from 'react-router-dom'
import type { ProjectPlan } from '../../lib/api'
import { OnlineDot } from '../../components/Status'

/**
 * The project split by server: what each server gets, how the exposed services are reached, and what stops a deploy.
 * The generated compose files are shown as they go to the agents.
 */
export default function ProjectPlacement({ plan, running, onDeploy }: { plan: ProjectPlan | null; running: boolean; onDeploy: () => void }) {
  if (!plan) return <p className="muted">…</p>
  return (
    <>
      {plan.problems.map(p => <p key={p} className="error">✗ {p}</p>)}
      {plan.warnings.map(w => <p key={w} className="warn-text">⚠ {w}</p>)}
      {!plan.spanning && (
        <p className="muted">All services run on <b>{plan.hosts[0]?.agentName}</b>. In the Services tab pick other servers for a service — one to move it, several to spread its replicas (the servers must be in the <Link to="/network">mesh</Link>) — and the project spans them: services with exposed ports stay reachable by name from every server.</p>
      )}
      <div className="cards">
        {plan.hosts.map(h => (
          <div key={h.agentId} className={'card' + (!h.online || (plan.spanning && !h.inMesh) ? ' card-bad' : '')}>
            <h3>
              <span><OnlineDot online={h.online} /><Link to={`/agents/${h.agentId}`}>{h.agentName}</Link></span>
              {plan.spanning && (h.inMesh ? <code title="mesh address: exposed ports are published here">{h.meshIp}</code> : <span className="badge bad">not in the mesh</span>)}
            </h3>
            <div className="meta">{h.services.length} service{h.services.length === 1 ? '' : 's'}: {h.services.map(s => (h.replicas[s] ?? 1) === 1 ? s : `${s} ×${h.replicas[s]}`).join(', ') || '—'}</div>
            {h.exposed.length > 0 && (
              <div className="kv-list">
                {h.exposed.map(e => (
                  <div key={e.service} className="kv"><span>{e.service}</span>
                    <span className="num" title={e.balanced ? 'each replica on a port docker picks on this address; the other servers reach them through their balancers' : undefined}>
                      {e.balanced ? `${e.address}, ${e.ports.join(', ')} — balanced` : e.ports.map(p => plan.spanning && e.address ? `${e.address}:${p}` : p).join(', ')}
                    </span>
                  </div>
                ))}
                {plan.spanning && <p className="mini">reachable from the other servers by name: {h.exposed.map((e, i) => <span key={e.service}>{i > 0 && ', '}<code>{e.service}</code></span>)}</p>}
              </div>
            )}
            {h.balancers.length > 0 && (
              <p className="mini">
                reaches {h.balancers.map((s, i) => <span key={s}>{i > 0 && ', '}<code>{s}</code> at <code>{balancerAddress(plan, h.meshIp, s)}</code></span>)} through
                a balancer over every replica on every server — a reverse proxy on this server uses the same address
              </p>
            )}
            {plan.spanning && h.composeYaml && (
              <details>
                <summary className="mini">compose file this server gets</summary>
                <pre className="cmd">{h.composeYaml}</pre>
              </details>
            )}
          </div>
        ))}
      </div>
      {plan.spanning && (
        <div className="toolbar form-actions">
          <button className="primary" disabled={running || plan.problems.length > 0} onClick={onDeploy} title={plan.problems.length > 0 ? 'fix the problems above first' : 'pull & deploy on every server, the ones with exposed services first'}>Pull &amp; deploy on all servers</button>
          <span className="mini">Servers with exposed services are deployed first. A service moved to another server is taken down where it ran before.</span>
        </div>
      )}
    </>
  )
}

/** mesh-ip:port of a server's balancer for a service (its TCP ports, as some server exposes them). */
function balancerAddress(plan: ProjectPlan, meshIp: string | null, service: string) {
  const ports = plan.hosts.flatMap(h => h.exposed).find(e => e.service === service)?.ports.filter(p => !p.endsWith('/udp')).map(p => p.split('/')[0]) ?? []
  return ports.map(p => `${meshIp ?? '?'}:${p}`).join(', ')
}
