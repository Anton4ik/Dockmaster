import { Link as RouteLink } from 'react-router-dom'
import type { Connection, MeshNetwork, MeshNode } from '../../lib/api'
import { OnlineDot } from '../../components/Status'
import { fmtRtt, netColor, type Link } from './model'

/** Servers in no network (or not in the mesh at all), each with a way in. */
export function OutsideTable({ nodes, networks, running, onAdd, onLeave }: {
  nodes: MeshNode[]
  networks: MeshNetwork[]
  running: boolean
  onAdd: (node: MeshNode, networkId: number) => void
  onLeave: (node: MeshNode) => void
}) {
  return (
    <div className="table-wrap"><table>
      <tbody>
        {nodes.map(n => (
          <tr key={n.agentId}>
            <td className="nowrap"><RouteLink to={`/agents/${n.agentId}`}><OnlineDot online={n.online} />{n.name}</RouteLink></td>
            <td className="muted">{!n.online ? 'offline' : !n.supported ? <span className="warn-text">agent too old — update it</span> : n.joined ? `in the mesh as ${n.meshIp}, no network` : 'not in the mesh'}</td>
            <td className="menu-cell">
              {n.online && n.supported && networks.length > 0 && (
                <select className="cell" aria-label={`add ${n.name} to a network`} value="" disabled={running} onChange={e => { if (e.target.value) onAdd(n, Number(e.target.value)) }}>
                  <option value="">add to…</option>
                  {networks.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}
                </select>
              )}
              {n.joined && <button className="sm link muted" disabled={running} onClick={() => onLeave(n)}>leave mesh</button>}
            </td>
          </tr>
        ))}
      </tbody>
    </table></div>
  )
}

/** The servers in the mesh: their networks, address, how they are reached and whether WireGuard is fine. */
export function ServersTable({ nodes, networks, selectedId, onSelect }: {
  nodes: MeshNode[]
  networks: MeshNetwork[]
  selectedId: number | undefined
  onSelect: (agentId: number) => void
}) {
  return (
    <div className="table-wrap"><table>
      <thead><tr><th>Server</th><th>Networks</th><th>Address</th><th className="hide-md">Reachable</th><th className="hide-lg">WireGuard</th><th>State</th></tr></thead>
      <tbody>
        {nodes.map(n => (
          // the whole row opens the server's panel; the name is its button for the keyboard (its click reaches the row)
          <tr key={n.agentId} className={'clickable' + (selectedId === n.agentId ? ' selected' : '')} onClick={() => onSelect(n.agentId)}>
            <td className="nowrap"><OnlineDot online={n.online} /><button className="link inherit row-open">{n.name}</button>{n.hub && <span className="badge ok" style={{ marginLeft: 8 }}>hub</span>}</td>
            <td>{n.networkIds.map(id => { const x = networks.find(y => y.id === id); return x && <span key={id} className="net-chip-inline" style={{ ['--c' as string]: netColor(networks, id) }}>{x.name}</span> })}</td>
            <td className="num"><code>{n.meshIp}</code></td>
            <td className="hide-md">{n.public ? (n.endpoint ?? <span className="error">public, no endpoint</span>) : <span className="muted">behind NAT</span>}</td>
            <td className="hide-lg muted">{n.backend}</td>
            <td>{!n.online ? <span className="muted">offline</span> : n.problems.length ? <span className="error" title={n.problems.join('\n')}>{n.problems[0]}</span> : !n.up ? <span className="warn-text">dm0 down</span> : !n.inSync ? <span className="warn-text">config pending</span> : <span className="state"><i className="dot ok" />up</span>}</td>
          </tr>
        ))}
      </tbody>
    </table></div>
  )
}

/** Projects whose services sit on several servers, and what each reaches over the mesh (with the ping). */
export function ConnectionsTable({ connections, links }: { connections: Connection[]; links: Link[] }) {
  const rttBetween = (a: string, b: string) => links.find(x => (x.a.name === a && x.b.name === b) || (x.b.name === a && x.a.name === b))?.rttMs ?? null
  return (
    <div className="table-wrap"><table>
      <thead><tr><th>Project</th><th>Servers</th><th>Over the mesh</th></tr></thead>
      <tbody>
        {connections.map(c => (
          <tr key={c.projectId}>
            <td className="nowrap"><RouteLink to={`/projects/${c.projectId}`}>{c.name}</RouteLink>{c.problems && <span className="badge bad" style={{ marginLeft: 8 }} title="the project's Servers tab says what is wrong">problems</span>}</td>
            <td>{c.hosts.join(', ')}</td>
            <td>{c.links.length === 0 ? <span className="muted">nothing exposed</span> : (
              <div className="kv-list">{c.links.map((l, i) => (
                <span key={i} className="nowrap"><b>{l.fromHost}</b> → <code>{l.service}</code> on {l.toHost} <span className="muted">({l.address}:{l.ports.join(', :')}, {fmtRtt(rttBetween(l.fromHost, l.toHost))})</span></span>
              ))}</div>
            )}</td>
          </tr>
        ))}
      </tbody>
    </table></div>
  )
}
