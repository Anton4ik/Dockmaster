import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, type Agent, type Mesh, type MeshNetwork, type MeshNode } from '../../lib/api'
import { usePhone } from '../../lib/hooks'
import { useStreamTask, type StreamResult } from '../../lib/live'
import { toast, withToast } from '../../lib/toast'
import NetworkChips from './NetworkChips'
import NetworkFocusBar from './NetworkFocusBar'
import NetworkGraph from './NetworkGraph'
import NetworkList from './NetworkList'
import { ConnectionsTable, OutsideTable, ServersTable } from './NetworkTables'
import { LinkPanel, NodePanel, TaskLog } from './Panels'
import { links as buildLinks, type MeshTask, type Selection } from './model'
import { useMesh } from './useMesh'

// a task's lines each change the mesh, but a burst of them needs one reload, not one each
const RELOAD_EVERY_MS = 1000

/**
 * The WireGuard mesh between servers. Servers are grouped in isolated networks (a server may be in several); the
 * graph shows who talks to whom, how (direct, over the LAN, through a hub), how fresh each tunnel is and how much
 * flows through it. Click a server or a link for details, the bandwidth test and settings.
 */
export default function NetworkPage({ agents }: { agents: Agent[] }) {
  const { mesh, connections, rates, error, apply, load, loadConnections } = useMesh()
  const [focus, setFocus] = useState<number | null>(null)
  const [selection, setSelection] = useState<Selection>(null)
  const [task, setTask] = useState<MeshTask | null>(null)
  const [bench, setBench] = useState<Record<string, { ab: string; ba: string }>>({})
  const [adding, setAdding] = useState(false)
  // leaving the page stops following a task quietly; the agents finish what they started
  const stream = useStreamTask()
  const phone = usePhone()

  const running = task != null && !task.result

  async function run(t: Omit<MeshTask, 'lines'>, method: string, args: unknown[], onDone?: (r: StreamResult) => void) {
    setTask({ ...t, lines: [] })
    let loaded = 0
    const result = await stream.run(method, args, line => {
      setTask(x => x && { ...x, lines: [...x.lines, line] })
      if (Date.now() - loaded >= RELOAD_EVERY_MS) { loaded = Date.now(); load() }
    })
    if (!result) return // replaced by another task, or the page was left
    setTask(x => x && { ...x, result })
    toast(`${t.title}: ${result.message}`, result.ok ? 'ok' : 'error')
    onDone?.(result)
    load(); loadConnections()
  }

  const addToNetwork = (n: { agentId: number; name: string }, networkId: number | null) => {
    const net = mesh?.networks.find(x => x.id === networkId)
    setSelection({ kind: 'node', id: n.agentId })
    void run({ kind: 'join', key: String(n.agentId), title: `${n.name} → ${net?.name ?? 'network'}` }, 'MeshJoin', [n.agentId, networkId])
  }

  function leave(n: MeshNode) {
    if (!confirm(`Take "${n.name}" out of the mesh?\n\nIts WireGuard interface, firewall rules and helper container are removed; services on other servers that reach it lose that path.`)) return
    void run({ kind: 'leave', key: String(n.agentId), title: `${n.name} leaves` }, 'MeshLeave', [n.agentId])
  }

  function runBench(l: { key: string; a: MeshNode; b: MeshNode }) {
    void run({ kind: 'bench', key: l.key, title: `benchmark ${l.a.name} ↔ ${l.b.name}` }, 'MeshBench', [l.a.agentId, l.b.agentId], r => {
      if (!r.ok) return
      // "a → b: 225 Mbit/s (3 retransmits) · b → a: 63 Mbit/s"
      const parts = r.message.split(' · ').map(p => p.slice(p.indexOf(':') + 1).trim())
      if (parts.length === 2) setBench(prev => ({ ...prev, [l.key]: { ab: parts[0], ba: parts[1] } }))
    })
  }

  /** A change the server answers with the whole mesh; resolves true when it worked. */
  const call = (path: string, init: RequestInit & { json?: unknown }, done: string) =>
    withToast('saving…', async () => apply(await api<Mesh>(path, init)), done)

  const configure = (n: MeshNode, patch: { public?: boolean | null; endpoint?: string | null; port?: number | null }) =>
    call(`/mesh/agents/${n.agentId}`, { method: 'PUT', json: { public: n.publicOverride, endpoint: n.endpointOverride, port: null, ...patch } }, `${n.name} updated`)

  const updateNetwork = (net: MeshNetwork, patch: { name?: string; hubAgentId?: number | null; trusted?: boolean }, done: string) =>
    call(`/mesh/networks/${net.id}`, { method: 'PUT', json: { name: net.name, hubAgentId: net.hubOverride, ...patch } }, done)

  async function createNetwork(input: string) {
    const name = input.trim()
    if (!name) { setAdding(false); return }
    if (await call('/mesh/networks', { method: 'POST', json: { name } }, `network ${name} created`)) setAdding(false)
  }

  function deleteNetwork(net: MeshNetwork) {
    if (!confirm(`Delete network "${net.name}"?\n\nIts servers stay in the mesh; those in no other network lose their peers.`)) return
    void call(`/mesh/networks/${net.id}`, { method: 'DELETE' }, 'network deleted').then(() => setFocus(null))
  }

  const nodes = useMemo(() => mesh?.nodes ?? [], [mesh])
  const links = useMemo(() => mesh ? buildLinks(mesh) : [], [mesh])
  const joined = nodes.filter(n => n.joined)
  const outside = nodes.filter(n => !n.joined || n.networkIds.length === 0)
  const focused = mesh?.networks.find(n => n.id === focus) ?? null
  const joining = task?.kind === 'join' && !task.result ? [Number(task.key)] : []
  const selNode = selection?.kind === 'node' ? nodes.find(n => n.agentId === selection.id) : undefined
  const selLink = selection?.kind === 'link' ? links.find(l => l.key === selection.key) : undefined
  // a server being added may not be in the graph yet: show it as soon as it has an address
  const graphNodes = joined.filter(n => n.networkIds.length > 0 || joining.includes(n.agentId))

  if (error && !mesh) return <p className="error">{error}</p>
  if (!mesh) return null

  const panel = selNode
    ? <NodePanel key={selNode.agentId} node={selNode} networks={mesh.networks} task={task} running={running}
        onAdd={id => addToNetwork(selNode, id)}
        onRemove={id => void call(`/mesh/networks/${id}/members/${selNode.agentId}`, { method: 'DELETE' }, `${selNode.name} removed from the network`)}
        onConfigure={p => void configure(selNode, p)} onLeave={() => leave(selNode)} onClose={() => setSelection(null)} />
    : selLink
      ? <LinkPanel link={selLink} networks={mesh.networks} rates={rates} task={task} bench={bench[selLink.key]} running={running}
          onBench={() => runBench(selLink)} onClose={() => setSelection(null)} />
      : null

  return (
    <>
      <div className="toolbar">
        <h1>Network</h1>
        <span className="muted hide-sm">WireGuard mesh <code>{mesh.subnet}</code>, UDP {mesh.port}</span>
        {joined.length > 0 && (
          <button className="push-right" disabled={running || !joined.some(n => n.online)}
            title="Each server measures the largest packet its connections carry and gets a tunnel MTU that fits: no packets split on the way, nothing wasted. About a minute; safe to run any time."
            onClick={() => { void run({ kind: 'tune', key: 'mesh', title: 'speed tuning' }, 'MeshTune', []) }}>
            {task?.kind === 'tune' && !task.result ? 'tuning…' : 'Tune speed'}
          </button>
        )}
      </div>
      {task?.kind === 'tune' && (
        <div className="card net-tune">
          <h2>Speed tuning</h2>
          <p className="mini">Pings that must not be fragmented find the largest packet each path carries; the tunnel MTU of every server is set from it{mesh.mtu ? ` — but the MTU ${mesh.mtu} set by hand in Settings stays in force` : ''}.</p>
          <TaskLog task={task} />
        </div>
      )}

      {/* networks: filter, create, and the selected one's settings */}
      <NetworkChips networks={mesh.networks} focus={focus} onFocus={setFocus} adding={adding} onAdding={setAdding} onCreate={name => { void createNetwork(name) }} />
      {focused && (
        <NetworkFocusBar key={focused.id} network={focused} networks={mesh.networks} nodes={nodes} running={running}
          onRename={name => updateNetwork(focused, { name }, 'renamed')}
          onHub={hub => { void updateNetwork(focused, { hubAgentId: hub }, 'hub updated') }}
          onTrusted={trusted => { void updateNetwork(focused, { trusted }, trusted ? 'LAN traffic leaves the tunnel' : 'LAN traffic back in the tunnel') }}
          onAddServer={n => addToNetwork(n, focused.id)} onDelete={() => deleteNetwork(focused)} />
      )}

      {agents.length === 0 && <p className="muted">No servers yet. <Link to="/agents/new">Add one</Link>, then put it into a network here.</p>}
      {mesh.problems.map(p => <p key={p} className="error">⚠ {p}</p>)}
      {mesh.hints.map(h => <p key={h} className="muted net-hint">💡 {h}</p>)}

      {mesh.networks.length === 0 && agents.length > 0 ? (
        <div className="card net-empty">
          <h2>Connect your servers</h2>
          <p className="muted">A network is a group of servers that reach each other through an encrypted WireGuard tunnel — directly, or through a public server when both sit behind NAT. Servers in different networks do not see each other. A server may be in several networks.</p>
          <button className="primary" onClick={() => setAdding(true)}>Create the first network</button>
        </div>
      ) : (
        <div className={'net-layout' + (panel ? ' with-panel' : '')}>
          <div className="net-main">
            {phone
              ? <NetworkList nodes={graphNodes} networks={mesh.networks} links={links} rates={rates} focus={focus} onSelect={setSelection} />
              : graphNodes.length > 0
                ? <NetworkGraph nodes={graphNodes} networks={mesh.networks} links={links} rates={rates} focus={focus} selection={selection} onSelect={setSelection} joining={joining} />
                : <div className="card net-empty"><p className="muted">No server is in a network yet. Pick a network above and add a server, or use a server's “add to a network” below.</p></div>}
            {!phone && graphNodes.length > 0 && (
              <p className="mini net-legend">
                <span><i className="lg ok" />live</span><span><i className="lg stale" />idle</span><span><i className="lg down" />no handshake</span>
                <span><i className="lg via" />through a hub</span><span><i className="lg plain" />LAN, no tunnel</span><span>moving dots: traffic, faster = more</span><span>drag servers to arrange them</span>
              </p>
            )}
          </div>
          {panel ?? (!phone && <aside className="net-panel idle hide-below-xl"><p className="muted">Click a server or a link for details and the bandwidth test.</p></aside>)}
        </div>
      )}

      {outside.length > 0 && (
        <>
          <h2>Not in a network</h2>
          <OutsideTable nodes={outside} networks={mesh.networks} running={running} onAdd={addToNetwork} onLeave={leave} />
        </>
      )}

      {joined.length > 0 && !phone && (
        <>
          <h2>Servers</h2>
          <ServersTable nodes={joined} networks={mesh.networks} selectedId={selNode?.agentId} onSelect={id => setSelection({ kind: 'node', id })} />
        </>
      )}

      {links.length > 0 && (
        <>
          <h2>Projects across servers</h2>
          {connections.length === 0
            ? <p className="muted">None yet. In a project's Services tab pick another server for a service; services with <code>expose</code>d ports stay reachable by name from every server of the project.</p>
            : <ConnectionsTable connections={connections} links={links} />}
        </>
      )}
      {phone && panel && <div className="net-sheet-backdrop" onClick={() => setSelection(null)} />}
    </>
  )
}
