import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, type Container, type Registry } from '../../lib/api'
import { useDismiss } from '../../lib/hooks'

/**
 * Compact "N unchecked" chip for images whose registry Dockmaster has no login for.
 * The popover offers the likely fix in one click: treat the address as another name of a registry already added.
 */
export default function UncheckedImages({ containers, onFixed }: { containers: Container[]; onFixed: () => void }) {
  const [open, setOpen] = useState(false)
  const [registries, setRegistries] = useState<Registry[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const ref = useRef<HTMLSpanElement>(null)

  const hosts = new Map<string, number>()
  for (const c of containers) {
    const m = c.updateDetail?.match(/^no access to (\S+?):? /)
    if (c.update === 'unknown' && m) hosts.set(m[1], (hosts.get(m[1]) ?? 0) + 1)
  }
  const total = [...hosts.values()].reduce((a, b) => a + b, 0)

  useDismiss(ref, open, () => setOpen(false))
  useEffect(() => {
    if (open) api<Registry[]>('/registries').then(setRegistries).catch(e => setError(errorText(e)))
  }, [open])

  if (total === 0) return null

  async function addAlias(r: Registry, host: string) {
    setBusy(host); setError(null)
    try {
      const aliases = [...r.aliases.split(',').filter(Boolean), host].join(',')
      await api(`/registries/${r.id}`, { method: 'PUT', json: { name: r.name, url: r.url, username: r.username, password: null, insecure: r.insecure, aliases } })
      await api('/updates/check', { method: 'POST' })
      setOpen(false)
      onFixed()
    } catch (e) { setError(errorText(e)) }
    finally { setBusy(null) }
  }

  return (
    <span className="menu" ref={ref}>
      <button className="chip" onClick={() => setOpen(o => !o)} aria-expanded={open} title="images Dockmaster cannot check for updates — click to fix">
        <i className="dot muted" />{total} unchecked
      </button>
      {open && (
        <div className="menu-list wide">
          {[...hosts].map(([host, n]) => (
            <div key={host} className="unchecked">
              <div><b>{n}</b> image{n > 1 ? 's' : ''} from <code>{host}</code> — no login for this address</div>
              <div className="actions">
                {registries.filter(r => r.username).map(r => (
                  <button key={r.id} className="sm primary" disabled={busy === host} onClick={() => addAlias(r, host)}
                    title={`treat ${host} as another address of ${r.name} (${r.url}) and use its login`}>
                    {busy === host ? 'checking…' : `use ${r.name} login`}
                  </button>
                ))}
                <Link className="btn sm" to={`/registries?url=${encodeURIComponent(host)}`}>add as new registry</Link>
              </div>
            </div>
          ))}
          {error && <p className="error mini">{error}</p>}
        </div>
      )}
    </span>
  )
}
