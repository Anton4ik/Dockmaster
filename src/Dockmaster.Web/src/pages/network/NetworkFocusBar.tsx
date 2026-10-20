import { useState } from 'react'
import type { MeshNetwork, MeshNode } from '../../lib/api'
import InlineNameForm from '../../components/InlineNameForm'
import RowMenu from '../../components/RowMenu'
import { netColor } from './model'

/** The chosen network's settings: its name, which server is its hub, adding a server, and deleting it. */
export default function NetworkFocusBar({ network, networks, nodes, running, onRename, onHub, onTrusted, onAddServer, onDelete }: {
  network: MeshNetwork
  networks: MeshNetwork[]
  nodes: MeshNode[]
  running: boolean
  /** resolves true once renamed */
  onRename: (name: string) => Promise<boolean>
  onHub: (agentId: number | null) => void
  onTrusted: (trusted: boolean) => void
  onAddServer: (node: MeshNode) => void
  onDelete: () => void
}) {
  const [renaming, setRenaming] = useState(false)
  const nodeOf = (id: number | null) => nodes.find(n => n.agentId === id)
  const members = network.members.map(nodeOf).filter((n): n is MeshNode => n !== undefined)
  return (
    <div className="net-focus" style={{ ['--c' as string]: netColor(networks, network.id) }}>
      {renaming
        ? <InlineNameForm className="net-new" inputClassName="cell" label="network name" initial={network.name} saveLabel="Save"
            onSave={name => { void onRename(name).then(ok => { if (ok) setRenaming(false) }) }} onCancel={() => setRenaming(false)} />
        : <b>{network.name}</b>}
      <label className="inline">hub
        <select className="cell" value={network.hubOverride ?? ''} onChange={e => onHub(e.target.value ? Number(e.target.value) : null)}
          title={'Relays between members behind different NATs, so it must be reachable from the internet. Members on one LAN talk directly and need no hub. '
            + 'A server behind NAT with a forwarded UDP port becomes a candidate once its "From the internet" says public.'}>
          <option value="">auto{network.hubAgentId ? ` (${nodeOf(network.hubAgentId)?.name})` : members.some(n => n.public) ? ' (none)' : ' (none: no public member)'}</option>
          {/* members behind NAT are listed too, greyed out, so it is clear why they cannot be picked */}
          {members.map(n => <option key={n.agentId} value={n.agentId} disabled={!n.public}>{n.name}{n.public ? '' : ' — behind NAT'}</option>)}
        </select>
      </label>
      <label className="inline" title={'Members on one LAN (a home network, the private network of a provider) talk without WireGuard: '
        + 'less latency and CPU, but the traffic is not encrypted, and anyone on that LAN can reach the mesh addresses. '
        + 'Everything else (over the internet, through a hub) stays in the tunnel.'}>
        <input type="checkbox" checked={network.trusted} disabled={running} onChange={e => onTrusted(e.target.checked)} /> trusted LAN
      </label>
      <select className="cell" aria-label="add a server to the network" value="" disabled={running} onChange={e => { const n = nodeOf(Number(e.target.value)); if (n) onAddServer(n) }}>
        <option value="">+ add a server…</option>
        {nodes.filter(n => !network.members.includes(n.agentId) && n.online && n.supported).map(n => <option key={n.agentId} value={n.agentId}>{n.name}{n.joined ? '' : ' (joins the mesh)'}</option>)}
      </select>
      <div className="spacer" />
      <RowMenu title={`network ${network.name}`} items={[
        { label: 'rename', onClick: () => setRenaming(true) },
        { label: 'delete network', danger: true, onClick: onDelete },
      ]} />
    </div>
  )
}
