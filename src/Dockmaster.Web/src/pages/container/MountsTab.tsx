import { Link } from 'react-router-dom'
import type { ContainerDetails } from '../../lib/api'

/** Volumes and bind mounts: where they sit in the container, where they come from, and whether it may write. */
export default function MountsTab({ d, agentId }: { d: ContainerDetails; agentId: number }) {
  const unnamed = (m: ContainerDetails['mounts'][number]) => m.type === 'volume' && /^[0-9a-f]{64}$/.test(m.name)
  return (
    <>
      <div className="table-wrap"><table>
        <thead><tr><th>Container path</th><th>Source</th><th className="hide-sm">Type</th><th>Access</th></tr></thead>
        <tbody>
          {d.mounts.map(m => (
            <tr key={m.destination}>
              <td><code>{m.destination}</code></td>
              <td className="muted env-value">
                {m.type === 'volume'
                  ? <Link to={`/agents/${agentId}/volumes?q=${encodeURIComponent(m.name)}`} title="the volume on this server: size, who else uses it">{unnamed(m) ? `unnamed ${m.name.slice(0, 12)}` : m.name}</Link>
                  : m.source}
                {unnamed(m) && <span className="mini warn-text"> — a re-created container gets a new, empty one</span>}
              </td>
              <td className="muted hide-sm">{m.type === 'bind' ? 'folder of the server' : m.type}</td>
              <td className={m.rw ? '' : 'muted'}>{m.rw ? 'read-write' : 'read-only'}</td>
            </tr>
          ))}
          {d.mounts.length === 0 && <tr><td colSpan={4} className="muted">no mounts — what this container writes is lost when it is re-created</td></tr>}
        </tbody>
      </table></div>
    </>
  )
}
