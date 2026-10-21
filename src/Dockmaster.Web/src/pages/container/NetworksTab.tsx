import { useEffect, useState } from 'react'
import { api, type ContainerDetails } from '../../lib/api'
import { useCan } from '../../lib/access'
import { withToast } from '../../lib/toast'

interface HostNetwork { name: string; driver: string; scope: string; internal: boolean; containers: number }

/**
 * Networks with connect / disconnect, live (docker network connect / disconnect). A name that is not a network yet
 * creates one. For a container of a Dockmaster project the server writes the change into the service's networks.
 */
export default function NetworksTab({ d, agentId, onChanged }: { d: ContainerDetails; agentId: number; onChanged: () => void }) {
  // a container of a project gets the change written into its compose file: editing projects on the server
  const editable = useCan()(agentId, 'edit')
  const [host, setHost] = useState<HostNetwork[]>([])
  const [pick, setPick] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => { api<HostNetwork[]>(`/agents/${agentId}/networks`).then(setHost).catch(() => {}) }, [agentId, d.networks.length])
  const own = (d.networkMode ?? '').startsWith('container:') || d.networkMode === 'host'
  const attached = new Set(d.networks.map(n => n.name))
  const choices = host.filter(n => !attached.has(n.name))
  const target = pick.trim()
  const exists = host.some(n => n.name === target)

  async function change(network: string, connect: boolean) {
    if (!connect && !confirm(d.networks.length === 1
      ? `Disconnect ${d.name} from ${network}?\n\nIt is its only network: the container is cut off from everything until it gets another one.`
      : `Disconnect ${d.name} from ${network}?`)) return
    setBusy(true)
    const ok = await withToast(`${d.name}: ${connect ? `connecting to ${network}` : `disconnecting from ${network}`}…`,
      () => api<{ message: string }>(`/agents/${agentId}/containers/${encodeURIComponent(d.name)}/networks`, { method: 'POST', json: { network, connect } }),
      r => `${d.name}: ${r.message}`, message => `${d.name}: ${message}`)
    if (ok) { setPick(''); onChanged() }
    setBusy(false)
  }

  return (
    <>
      {!own && editable && (
        <form className="filters net-connect" onSubmit={e => { e.preventDefault(); if (target) void change(target, true) }}>
          <input list="host-networks" aria-label="network to connect" placeholder="network to connect (a new name creates it)" value={pick} onChange={e => setPick(e.target.value)} />
          <datalist id="host-networks">
            {choices.map(n => <option key={n.name} value={n.name}>{`${n.driver}${n.internal ? ', internal' : ''} · ${n.containers} container${n.containers === 1 ? '' : 's'}`}</option>)}
          </datalist>
          <button className="sm primary" type="submit" disabled={busy || !target || attached.has(target)} title={exists || !target ? 'docker network connect, live' : 'docker network create (bridge), then connect'}>
            {!target || exists ? 'connect' : 'create & connect'}
          </button>
        </form>
      )}
      <div className="table-wrap"><table>
        <thead><tr><th>Network</th><th>IP</th><th className="hide-sm">Gateway</th><th className="hide-md">MAC</th><th>Aliases</th><th><span className="sr-only">actions</span></th></tr></thead>
        <tbody>
          {d.networks.map(n => (
            <tr key={n.name}>
              <td className="nowrap">{n.name}</td>
              <td className="nowrap"><code>{n.ip ? `${n.ip}/${n.prefix}` : '—'}</code></td>
              <td className="nowrap muted hide-sm">{n.gateway || '—'}</td>
              <td className="nowrap muted hide-md">{n.mac || '—'}</td>
              <td className="muted">{n.aliases.join(', ') || '—'}</td>
              <td className="menu-cell">{editable && <button className="sm danger" disabled={busy} onClick={() => { void change(n.name, false) }} title="docker network disconnect, live">disconnect</button>}</td>
            </tr>
          ))}
          {d.networks.length === 0 && <tr><td colSpan={6} className="muted">network mode {d.networkMode ?? 'none'}</td></tr>}
        </tbody>
      </table></div>
      <h2>Ports</h2>
      {d.ports.length === 0 ? <p className="muted">no ports exposed</p> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Container</th><th>Published on the host</th></tr></thead>
          <tbody>
            {d.ports.map(p => <tr key={p.container}><td><code>{p.container}</code></td><td>{p.published.length ? p.published.map(x => <code key={x}>{x}</code>) : <span className="muted">not published</span>}</td></tr>)}
          </tbody>
        </table></div>
      )}
    </>
  )
}
