import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api, errorText, type Registry } from '../lib/api'
import { useVisibleColumns } from '../lib/hooks'
import Modal from '../components/Modal'
import RowMenu from '../components/RowMenu'

const empty = { id: 0, name: '', url: '', username: '', password: '', insecure: false, aliases: '' }
const COPIED_SHOWN_MS = 1500

/**
 * Container registries: logins the servers pull with (and the update check asks), and a browser of their
 * repositories and tags with copy-ready image references.
 */
export default function Registries() {
  const [list, setList] = useState<Registry[]>([])
  const [params] = useSearchParams()
  const [form, setForm] = useState({ ...empty, url: params.get('url') ?? '', name: params.get('url') ?? '' })
  // the form only shows while adding / editing (or when a page linked here with ?url=)
  const [formOpen, setFormOpen] = useState(!!params.get('url'))
  const openForm = (f: typeof empty) => { setForm(f); setMsg(null); setFormOpen(true) }
  const closeForm = () => { setForm({ ...empty }); setFormOpen(false) }
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const columns = useVisibleColumns(4, { sm: 1 })
  const [browse, setBrowse] = useState<Registry | null>(null)
  const [repos, setRepos] = useState<string[]>([])
  const [repo, setRepo] = useState<string | null>(null)
  const [tags, setTags] = useState<string[]>([])
  const [filter, setFilter] = useState('')
  const [copied, setCopied] = useState<string | null>(null)
  const [browseError, setBrowseError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const latestRepo = useRef<string | null>(null)
  const copiedTimer = useRef<ReturnType<typeof setTimeout>>(undefined)
  useEffect(() => () => clearTimeout(copiedTimer.current), [])
  const load = () => api<Registry[]>('/registries').then(setList).catch(err => setMsg({ ok: false, text: errorText(err) }))
  useEffect(() => { load() }, [])

  async function save() {
    setMsg(null)
    const body = { name: form.name, url: form.url, username: form.username, password: form.password || null, insecure: form.insecure, aliases: form.aliases }
    try {
      if (form.id) await api(`/registries/${form.id}`, { method: 'PUT', json: body })
      else await api('/registries', { method: 'POST', json: body })
      closeForm(); setMsg({ ok: true, text: 'saved' }); load()
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function test() {
    setMsg({ ok: true, text: 'testing…' })
    try {
      const r = await api<{ ok: boolean; message: string }>(`/registries/test${form.id ? `?id=${form.id}` : ''}`, {
        method: 'POST', json: { name: form.name, url: form.url, username: form.username, password: form.password || null, insecure: form.insecure },
      })
      setMsg({ ok: r.ok, text: r.message })
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function remove(r: Registry) {
    if (!confirm(`Delete registry "${r.name}"?`)) return
    try {
      await api(`/registries/${r.id}`, { method: 'DELETE' })
      if (browse?.id === r.id) setBrowse(null)
      load()
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function openBrowse(r: Registry) {
    setBrowse(r); setRepos([]); setTags([]); setRepo(null); setCopied(null); setBrowseError(null); setLoading(true)
    try { setRepos(await api<string[]>(`/registries/${r.id}/catalog`)) }
    catch (err) { setBrowseError(errorText(err)) }
    finally { setLoading(false) }
  }

  async function loadTags(name: string) {
    setRepo(name); setTags([]); setCopied(null); setBrowseError(null); setLoading(true)
    latestRepo.current = name
    try {
      const list = await api<string[]>(`/registries/${browse!.id}/tags?repo=${encodeURIComponent(name)}`)
      if (latestRepo.current === name) setTags(list) // a slower answer for a repo clicked earlier must not win
    }
    catch (err) { if (latestRepo.current === name) setBrowseError(errorText(err)) }
    finally { if (latestRepo.current === name) setLoading(false) }
  }

  function copy(text: string) {
    setCopied(text)
    navigator.clipboard?.writeText(text).catch(() => {})
    clearTimeout(copiedTimer.current)
    copiedTimer.current = setTimeout(() => setCopied(null), COPIED_SHOWN_MS)
  }

  // repositories grouped by namespace ("dockmaster/agent" -> dockmaster), filtered
  const q = filter.trim().toLowerCase()
  const grouped = new Map<string, string[]>()
  for (const r of repos.filter(r => r.toLowerCase().includes(q))) {
    const ns = r.includes('/') ? r.slice(0, r.indexOf('/')) : ''
    ;(grouped.get(ns) ?? grouped.set(ns, []).get(ns)!).push(r)
  }
  const namespaces = [...grouped.keys()].sort((a, b) => a === '' ? -1 : b === '' ? 1 : a.localeCompare(b))

  return (
    <>
      <div className="toolbar">
        <h1>Registries</h1>
        {msg && !formOpen && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
        <div className="spacer" />
        <button className="primary" onClick={() => openForm({ ...empty })}>+ Add registry</button>
      </div>

      <div className="table-wrap"><table>
        <thead><tr><th>Name</th><th>Address</th><th className="hide-sm">User</th><th></th></tr></thead>
        <tbody>
          {list.map(r => (
            <tr key={r.id} className={browse?.id === r.id ? 'selected' : ''}>
              <td className="nowrap"><button className="link" onClick={() => openBrowse(r)} title="browse repositories and tags">{r.name}</button></td>
              <td className="muted">{r.insecure ? 'http://' : ''}{r.url}{r.aliases && <span className="mini" title="other addresses of this registry"> · also {r.aliases.split(',').join(', ')}</span>}</td>
              <td className="muted hide-sm">{r.username || '—'}</td>
              <td className="menu-cell">
                <RowMenu title={`actions for ${r.name}`} items={[
                  { label: 'browse', onClick: () => openBrowse(r) },
                  { label: 'edit', onClick: () => openForm({ id: r.id, name: r.name, url: r.url, username: r.username, password: '', insecure: r.insecure, aliases: r.aliases }) },
                  { label: 'delete', danger: true, onClick: () => remove(r) },
                ]} />
              </td>
            </tr>
          ))}
          {list.length === 0 && <tr><td colSpan={columns} className="muted">No registries yet. Add your private registry to browse its images and let servers pull from it.</td></tr>}
        </tbody>
      </table></div>

      {browse && (
        <div className="browser">
          <div className="browser-head">
            <h2>{browse.name} <span className="muted mini">{browse.url}</span></h2>
            <input type="search" aria-label="filter repositories" placeholder={`filter ${repos.length} repositories`} value={filter} onChange={e => setFilter(e.target.value)} />
            <div className="spacer" />
            <button className="icon" title="close" aria-label="close the repository browser" onClick={() => setBrowse(null)}>×</button>
          </div>
          {browseError && <p className="error">{browseError}</p>}
          <div className="browser-body">
            <div className="repo-list">
              {loading && repos.length === 0 && <p className="muted mini">loading…</p>}
              {namespaces.map(ns => (
                <div key={ns || '(root)'}>
                  {ns && <div className="ns">{ns}/</div>}
                  {grouped.get(ns)!.map(r => (
                    <button key={r} className={'repo' + (r === repo ? ' active' : '')} onClick={() => loadTags(r)}>{ns ? r.slice(ns.length + 1) : r}</button>
                  ))}
                </div>
              ))}
              {!loading && repos.length > 0 && namespaces.length === 0 && <p className="muted mini">nothing matches</p>}
            </div>
            <div className="tag-list">
              {!repo ? <p className="muted">Pick a repository to see its tags.</p> : (
                <>
                  <div className="tag-head"><b>{repo}</b> <span className="muted mini">{tags.length} tag{tags.length === 1 ? '' : 's'}</span></div>
                  {loading && tags.length === 0 && <p className="muted mini">loading…</p>}
                  <table>
                    <tbody>
                      {tags.map(t => {
                        const ref = `${browse.url}/${repo}:${t}`
                        return (
                          <tr key={t}>
                            <td className="nowrap" title={ref}><code>{t}</code></td>
                            <td className="nowrap" style={{ textAlign: 'right' }}>
                              <button className="sm" onClick={() => copy(ref)} title="copy the image reference">{copied === ref ? 'copied ✓' : 'copy'}</button>{' '}
                              <button className="sm" onClick={() => copy(`docker pull ${ref}`)} title="copy a docker pull command">{copied === `docker pull ${ref}` ? 'copied ✓' : 'pull cmd'}</button>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </>
              )}
            </div>
          </div>
        </div>
      )}

      {formOpen && (
        <Modal label={form.id ? `Edit ${form.name}` : 'Add registry'} onClose={closeForm} onSubmit={() => { void save() }} autoComplete="off">
          {!form.id && list.length > 0 && form.url && (
            <p className="hint">Same registry under another address? Edit it and add <code>{form.url}</code> to "Also reachable as" instead of a second entry.</p>
          )}
          <label htmlFor="r-name">Name</label><input id="r-name" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="NAS registry" required autoFocus />
          <label htmlFor="r-url">Address — host[:port], no scheme</label><input id="r-url" value={form.url} onChange={e => setForm({ ...form, url: e.target.value })} placeholder="registry.example.com:5000" required />
          <label htmlFor="r-aliases">Also reachable as <span className="muted">(other addresses of the same registry, comma separated)</span></label>
          <input id="r-aliases" value={form.aliases} onChange={e => setForm({ ...form, aliases: e.target.value })} placeholder="registry.example.org:443" />
          <div className="row">
            <div><label htmlFor="r-user">Username</label><input id="r-user" name="registry-user" value={form.username} onChange={e => setForm({ ...form, username: e.target.value })} autoComplete="off" data-lpignore="true" /></div>
            <div><label htmlFor="r-password">Password {form.id ? <span className="muted">(empty keeps it)</span> : ''}</label><input id="r-password" type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} autoComplete="new-password" /></div>
          </div>
          <label className="check"><input type="checkbox" checked={form.insecure} onChange={e => setForm({ ...form, insecure: e.target.checked })} /> plain HTTP (insecure registry)</label>
          <div className="toolbar form-actions flush">
            <button className="primary" type="submit">Save</button>
            <button type="button" onClick={test}>Test connection</button>
            <button type="button" onClick={() => { closeForm(); setMsg(null) }}>Cancel</button>
            {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
          </div>
        </Modal>
      )}
    </>
  )
}
