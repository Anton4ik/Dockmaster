import { useState } from 'react'
import type { ContainerDetails } from '../../lib/api'
import { useSetToggle } from '../../lib/hooks'

/**
 * Values are hidden until asked for: they are often passwords and tokens. The image's own variables sit last.
 * `hidden`: the server left the values out (no Secrets permission) — only the names are shown.
 */
export default function EnvTab({ env, hidden }: { env: ContainerDetails['env']; hidden?: boolean }) {
  const [shown, toggle] = useSetToggle<string>()
  const [all, setAll] = useState(false)
  const [filter, setFilter] = useState('')
  const q = filter.trim().toLowerCase()
  const rows = [...env].sort((a, b) => Number(a.fromImage) - Number(b.fromImage) || a.name.localeCompare(b.name))
    .filter(e => !q || e.name.toLowerCase().includes(q))
  const visible = (n: string) => all || shown.has(n)
  return (
    <>
      <div className="filters">
        <input type="search" aria-label="filter variables" placeholder="filter by name" value={filter} onChange={e => setFilter(e.target.value)} />
        {hidden
          ? <span className="muted mini">values are hidden: seeing them takes the Secrets permission on this server</span>
          : <label><input type="checkbox" checked={all} onChange={e => setAll(e.target.checked)} /> show all values</label>}
      </div>
      {/* fixed columns: a long value opened in place wraps inside its column instead of re-laying out the table */}
      <div className="table-wrap"><table className="fixed-cols">
        <colgroup><col className="env-name-col" /><col /></colgroup>
        <thead><tr><th>Name</th><th>Value</th></tr></thead>
        <tbody>
          {rows.map(e => (
            <tr key={e.name}>
              <td className="env-value"><code>{e.name}</code>{e.fromImage && <span className="mini muted" title="set by the image, not by this container"> image</span>}</td>
              <td className="env-value">
                {hidden ? <span className="muted">••••••••</span> : visible(e.name)
                  ? all ? <code>{e.value || '""'}</code> : <button className="link reveal" onClick={() => toggle(e.name)} title="hide the value"><code>{e.value || '""'}</code></button>
                  : <button className="link muted" onClick={() => toggle(e.name)} title="show the value">••••••••</button>}
              </td>
            </tr>
          ))}
          {rows.length === 0 && <tr><td colSpan={2} className="muted">no variables{q && ' match'}</td></tr>}
        </tbody>
      </table></div>
    </>
  )
}
