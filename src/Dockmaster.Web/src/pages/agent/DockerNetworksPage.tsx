import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorText, type Agent } from '../../lib/api'
import { useCan } from '../../lib/access'
import { useSetToggle } from '../../lib/hooks'
import { toast, withToast } from '../../lib/toast'
import Modal from '../../components/Modal'
import ServerNav from './ServerNav'

interface DockerNetwork {
  name: string; driver: string; scope: string; internal: boolean; containers: number; users: string[]
  subnet: string; project: string; builtin: boolean; created: number
}

/**
 * The docker networks of one server: which containers are on each, the project that made it; new bridge networks
 * (e.g. to let services of two projects reach each other by name) and removing the ones nothing uses.
 */
export default function DockerNetworksPage({ agents }: { agents: Agent[] }) {
  const id = Number(useParams().id)
  // docker networks are the host's: the Server permission
  const mayChange = useCan()(id, 'server')
  const agent = agents.find(a => a.id === id)
  const [list, setList] = useState<DockerNetwork[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, toggle, setSelected] = useSetToggle<string>()
  const [creating, setCreating] = useState(false)
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => api<DockerNetwork[]>(`/agents/${id}/networks`).then(l => { setList(l); setError(null) }, e => setError(errorText(e))), [id])
  useEffect(() => { void load() }, [load])

  const removable = (n: DockerNetwork) => !n.builtin && n.containers === 0
  const chosen = (list ?? []).filter(n => selected.has(n.name))

  async function remove(targets: DockerNetwork[]) {
    if (!confirm(`Remove ${targets.length} network(s)?\n\n${targets.map(n => n.name).join('\n')}\n\nA project that declares one creates it again on its next deploy.`)) return
    setBusy(true)
    const ok = await withToast(`removing ${targets.length} network(s)…`,
      () => api<{ removed: string[]; failed: Record<string, string> }>(`/agents/${id}/networks/remove`, { method: 'POST', json: { names: targets.map(n => n.name) } }),
      r => { const f = Object.entries(r.failed); return { text: `removed ${r.removed.length}` + (f.length ? ` — not removed: ${f.map(([k, v]) => `${k} (${v})`).join('; ')}` : ''), ok: f.length === 0 } })
    if (ok) { setSelected([]); await load() }
    setBusy(false)
  }

  return (
    <>
      <div className="toolbar">
        <h1><Link to={`/agents/${id}`}>{agent?.name ?? `server ${id}`}</Link><span className="muted"> / docker networks</span></h1>
        <button className="sm" onClick={() => { void load() }}>refresh</button>
        {mayChange && <button className="sm primary" onClick={() => setCreating(true)} disabled={!agent?.online}>+ new network</button>}
      </div>
      <ServerNav agentId={id} />
      <p className="muted mini">Containers on the same network reach each other by service name. Every project gets its own network; a network made here lets services of different projects talk (connect a container on its page → Network). Connections between servers are the <Link to="/network">Network</Link> page.</p>
      {error && <p className="error">{error}</p>}
      {list === null ? (!error && <p className="muted">Loading…</p>) : (
        <>
          {mayChange && <div className="filters">
            <button className="sm danger" disabled={busy || chosen.length === 0} onClick={() => { void remove(chosen) }}>{chosen.length ? `remove ${chosen.length} selected` : 'remove selected'}</button>
            {list.some(removable) && <button className="sm" disabled={busy} onClick={() => { void remove(list.filter(removable)) }}>remove all {list.filter(removable).length} unused</button>}
          </div>}
          <div className="table-wrap"><table>
            <thead><tr><th className="check" /><th>Network</th><th className="hide-sm">Subnet</th><th>Containers</th></tr></thead>
            <tbody>
              {list.map(n => (
                <tr key={n.name} className={selected.has(n.name) ? 'selected' : undefined}>
                  <td className="check"><input type="checkbox" aria-label={`select ${n.name}`} checked={selected.has(n.name)} disabled={!removable(n)}
                    title={n.builtin ? 'docker\'s own network' : n.containers ? 'in use' : undefined} onChange={() => toggle(n.name)} /></td>
                  <td>
                    <b>{n.name}</b>
                    {n.internal && <span className="badge muted source" title="no way out to the internet">internal</span>}
                    {n.builtin && <span className="badge muted source">docker's own</span>}
                    <div className="mini">{n.driver}{n.project && <> · project {n.project}</>}</div>
                  </td>
                  <td className="muted hide-sm"><code>{n.subnet || '—'}</code></td>
                  <td>{n.users.length === 0 ? <span className="muted">—</span> : (
                    <div className="volume-users">{n.users.map(c => <Link key={c} to={`/agents/${id}/containers/${encodeURIComponent(c)}`}>{c}</Link>)}</div>
                  )}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        </>
      )}
      {creating && <NewNetwork agentId={id} onClose={() => setCreating(false)} onCreated={m => { setCreating(false); toast(m); void load() }} />}
    </>
  )
}

function NewNetwork({ agentId, onClose, onCreated }: { agentId: number; onClose: () => void; onCreated: (message: string) => void }) {
  const [name, setName] = useState('')
  const [internal, setInternal] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function create() {
    setBusy(true); setError(null)
    try { onCreated((await api<{ message: string }>(`/agents/${agentId}/networks`, { method: 'POST', json: { name: name.trim(), internal } })).message) }
    catch (e) { setError(errorText(e)) }
    finally { setBusy(false) }
  }
  return (
    <Modal label="New network" onClose={onClose} onSubmit={() => { void create() }}>
      <label htmlFor="n-name">Name</label>
      <input id="n-name" autoFocus value={name} onChange={e => setName(e.target.value)} placeholder="e.g. shared" />
      <label className="check"><input type="checkbox" checked={internal} onChange={e => setInternal(e.target.checked)} /> internal — the containers on it cannot reach the internet</label>
      {error && <p className="error">{error}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy || !name.trim()}>Create</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
