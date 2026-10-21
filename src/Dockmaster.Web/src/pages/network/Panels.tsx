import { useState, type ReactNode } from 'react'
import { Link as RouteLink } from 'react-router-dom'
import type { MeshNetwork, MeshNode } from '../../lib/api'
import { bytes, duration } from '../../lib/fmt'
import { OnlineDot } from '../../components/Status'
import { fmtRate, fmtRtt, HEALTH_DOT, netColor, type Link, type MeshTask } from './model'

const pathText = (l: Link) => l.ab.path === 'direct' ? 'direct' : l.ab.path === 'lan' ? 'direct, over the LAN'
  : l.ab.path === 'plain' ? 'over the LAN, no tunnel (unencrypted)' : l.ab.path === 'via' ? `through ${l.ab.viaName} (hub)` : 'no way: no public server in this network'
const healthText: Record<Link['health'], string> = { ok: 'live', stale: 'idle — no handshake for a while', down: 'no handshake', off: 'a server is offline' }

function Row({ label, children }: { label: string; children: ReactNode }) {
  return <div className="np-row"><span>{label}</span><span>{children}</span></div>
}

export function TaskLog({ task }: { task: MeshTask }) {
  return (
    <div className="np-task">
      <ol>{task.lines.map((l, i) => <li key={i} className={i === task.lines.length - 1 && !task.result ? 'now' : 'done'}>{l}</li>)}</ol>
      {task.result ? <p className={task.result.ok ? 'success' : 'error'}>{task.result.ok ? '✓' : '✗'} {task.result.message}</p> : <p className="mini"><span className="spinner inline" /> working…</p>}
    </div>
  )
}

/** Both directions between two servers: path, freshness, ping, traffic now, and a bandwidth test. */
export function LinkPanel({ link, networks, rates, task, bench, running, onBench, onClose }: {
  link: Link
  networks: MeshNetwork[]
  rates: Record<string, { mbps: number }>
  task: MeshTask | null
  bench: { ab: string; ba: string } | undefined
  running: boolean
  onBench: () => void
  onClose: () => void
}) {
  const { a, b } = link
  const hs = Math.min(link.ab.handshakeAgeS ?? Infinity, link.ba?.handshakeAgeS ?? Infinity)
  const net = networks.find(n => n.id === link.networkId)
  const mine = task?.kind === 'bench' && task.key === link.key ? task : null
  return (
    <aside className="net-panel" aria-label={`link ${a.name} – ${b.name}`}>
      <header>
        <h2><i className={'dot ' + HEALTH_DOT[link.health]} />{a.name} ↔ {b.name}</h2>
        <button className="icon" onClick={onClose} aria-label="close">×</button>
      </header>
      <p className="mini">{healthText[link.health]}</p>
      <Row label="Path">{pathText(link)}</Row>
      {net && <Row label="Network"><span className="net-chip-inline" style={{ ['--c' as string]: netColor(networks, net.id) }}>{net.name}</span></Row>}
      <Row label="Ping">{fmtRtt(link.rttMs)}</Row>
      <Row label="Handshake">{link.ab.path === 'plain' ? 'none, no tunnel' : hs === Infinity ? 'never' : `${duration(hs)} ago`}</Row>
      {/* the tunnel counts its bytes; through a hub or outside the tunnel there is nothing to show */}
      {link.ab.path !== 'via' && link.ab.path !== 'plain' && <>
        <Row label={`${a.name} → ${b.name}`}>{fmtRate(rates[`${a.agentId}-${b.agentId}`]?.mbps ?? 0)} <span className="mini">· {bytes(link.ab.tx)} total</span></Row>
        <Row label={`${b.name} → ${a.name}`}>{fmtRate(rates[`${b.agentId}-${a.agentId}`]?.mbps ?? 0)} <span className="mini">· {bytes(link.ab.rx)} total</span></Row>
      </>}
      <h3>Bandwidth</h3>
      {bench && !mine && (
        <div className="np-bench">
          <div><b>{bench.ab}</b><span>{a.name} → {b.name}</span></div>
          <div><b>{bench.ba}</b><span>{b.name} → {a.name}</span></div>
        </div>
      )}
      {mine && <TaskLog task={mine} />}
      <button className="primary np-wide" disabled={running || link.health === 'off' || link.ab.path === 'none'} onClick={onBench}
        title="iperf3 through the tunnel, 5 s each way (TCP; the first second is left out)">{mine && !mine.result ? 'measuring…' : bench ? 'Measure again' : 'Run benchmark'}</button>
      <p className="mini">About 10 s. Measures the tunnel end to end, so the slower of the two internet connections sets the number.</p>
    </aside>
  )
}

/** One server: its networks, how others reach it, what is wrong. */
export function NodePanel({ node, networks, task, running, onAdd, onRemove, onConfigure, onLeave, onClose }: {
  node: MeshNode
  networks: MeshNetwork[]
  task: MeshTask | null
  running: boolean
  onAdd: (networkId: number) => void
  onRemove: (networkId: number) => void
  onConfigure: (patch: { public?: boolean | null; endpoint?: string | null; port?: number | null }) => void
  onLeave: () => void
  onClose: () => void
}) {
  const [endpoint, setEndpoint] = useState(node.endpointOverride ?? '')
  const [port, setPort] = useState(String(node.port))
  const mine = task && task.key === String(node.agentId) ? task : null
  const others = networks.filter(n => !node.networkIds.includes(n.id))
  return (
    <aside className="net-panel" aria-label={`server ${node.name}`}>
      <header>
        <h2><OnlineDot online={node.online} /><RouteLink to={`/agents/${node.agentId}`}>{node.name}</RouteLink></h2>
        <button className="icon" onClick={onClose} aria-label="close">×</button>
      </header>
      {mine && <TaskLog task={mine} />}
      {node.problems.map(p => <p key={p} className="error mini">⚠ {p}</p>)}
      {node.notes.map(n => <p key={n} className="muted mini">{n}</p>)}
      {!node.joined && !mine && <p className="mini">{node.online ? (node.supported ? 'Not in the mesh yet. Adding it to a network sets it up: keys, a helper container with the WireGuard interface, an address.' : 'This agent predates the mesh — update it first.') : 'Offline.'}</p>}

      <h3>Networks</h3>
      <div className="np-nets">
        {node.networkIds.map(id => {
          const n = networks.find(x => x.id === id)
          if (!n) return null
          return (
            <span key={id} className="net-chip-inline" style={{ ['--c' as string]: netColor(networks, id) }}>
              {n.name}{n.hubAgentId === node.agentId && <b className="mini"> · hub</b>}
              <button className="icon" disabled={running} onClick={() => onRemove(id)} aria-label={`remove from ${n.name}`} title={`take out of ${n.name}`}>×</button>
            </span>
          )
        })}
        {node.networkIds.length === 0 && node.joined && <span className="mini">in no network: no peers</span>}
      </div>
      {others.length > 0 && node.online && node.supported && (
        <select className="cell np-wide" aria-label="add to a network" value="" disabled={running} onChange={e => { if (e.target.value) onAdd(Number(e.target.value)) }}>
          <option value="">+ add to a network…</option>
          {others.map(n => <option key={n.id} value={n.id}>{n.name}</option>)}
        </select>
      )}

      {node.joined && <>
        <h3>Reachability</h3>
        <Row label="Mesh address"><code>{node.meshIp}</code></Row>
        <Row label="From the internet">
          <select className="cell" aria-label="reachable from the internet" value={node.publicOverride === null ? 'auto' : node.publicOverride ? 'yes' : 'no'} disabled={running}
            onChange={e => onConfigure({ public: e.target.value === 'auto' ? null : e.target.value === 'yes' })}
            title="A public server is a direct endpoint for everyone and relays between members behind NAT. 'auto' looks at the host's own addresses.">
            <option value="auto">auto ({node.public ? 'public' : 'behind NAT'})</option>
            <option value="yes">public</option>
            <option value="no">behind NAT</option>
          </select>
        </Row>
        {node.public && (
          <form className="np-inline" onSubmit={e => { e.preventDefault(); onConfigure({ endpoint: endpoint.trim() || null }) }}>
            <label htmlFor="np-endpoint">Endpoint others connect to (host or host:outside-port)</label>
            <div className="row-inline">
              <input id="np-endpoint" className="cell" value={endpoint} placeholder={node.endpoint ?? 'host:port'} onChange={e => setEndpoint(e.target.value)} />
              <button className="sm" type="submit" disabled={running}>save</button>
            </div>
          </form>
        )}
        <form className="np-inline" onSubmit={e => { e.preventDefault(); const p = Number(port); if (p) onConfigure({ port: p }) }}>
          <label htmlFor="np-port">UDP port on the host</label>
          <div className="row-inline">
            <input id="np-port" className="cell" inputMode="numeric" value={port} onChange={e => setPort(e.target.value.replace(/\D/g, ''))} />
            <button className="sm" type="submit" disabled={running || Number(port) === node.port}>save</button>
          </div>
        </form>
        <h3>Host</h3>
        <Row label="WireGuard">{node.backend || '—'}{node.up ? '' : ' · dm0 down'}{node.inSync ? '' : ' · config pending'}</Row>
        {node.tcpCongestion && <Row label="TCP">{node.tcpCongestion}</Row>}
        <Row label="Tunnel MTU">{node.mtu || '—'}{node.pathMtu ? <span className="mini"> · its paths carry {node.pathMtu}</span> : <span className="mini"> · paths not measured yet (Tune speed above)</span>}</Row>
        <Row label="Host addresses">{node.hostIps.join(', ') || '—'}</Row>
        {node.peerIp && <Row label="Connects from">{node.peerIp}</Row>}
        <button className="danger np-wide" disabled={running} onClick={onLeave}>Leave the mesh</button>
      </>}
    </aside>
  )
}
