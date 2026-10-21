import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorText, type ProjectSecret, type ServiceSummary } from '../../lib/api'
import { when } from '../../lib/fmt'
import { useVisibleColumns } from '../../lib/hooks'

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
/** ${NAME}, ${NAME:-default}… or $NAME: the secret used as a variable. */
const usesVariable = (text: string, name: string) => new RegExp(`\\$\\{${escape(name)}([:?+-][^}]*)?\\}|\\$${escape(name)}\\b`).test(text)

/**
 * Write-only secrets of a project: values are sent to the agent on deploy and never shown again. A service gets one as
 * a ${VARIABLE} of the compose file (in its environment, most of the time) or, ticked, as a file in /run/secrets.
 */
export default function ProjectSecrets({ projectId, services, compose, onUse }: {
  projectId: number
  services: ServiceSummary[]
  /** the compose file: where the secrets are used as ${VARIABLES} */
  compose: string
  /** a service's secrets: written into the compose file */
  onUse: (service: string, names: string[]) => Promise<boolean>
}) {
  const [list, setList] = useState<ProjectSecret[]>([])
  const [name, setName] = useState('')
  const [value, setValue] = useState('')
  const [editing, setEditing] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const columns = useVisibleColumns(5, { sm: 1 })
  const [busy, setBusy] = useState<string | null>(null)

  async function toggle(service: ServiceSummary, secret: string, on: boolean) {
    setBusy(service.name + '/' + secret)
    await onUse(service.name, on ? [...service.secrets, secret] : service.secrets.filter(n => n !== secret))
    setBusy(null)
  }

  const load = useCallback(() => api<ProjectSecret[]>(`/projects/${projectId}/secrets`).then(setList).catch(e => setMsg({ ok: false, text: errorText(e) })), [projectId])
  useEffect(() => { void load() }, [load])

  async function save(e: FormEvent) {
    e.preventDefault()
    const n = (editing ?? name).trim()
    setMsg(null)
    try {
      await api(`/projects/${projectId}/secrets/${encodeURIComponent(n)}`, { method: 'PUT', json: { value } })
      setName(''); setValue(''); setEditing(null)
      setMsg({ ok: true, text: `saved ${n} — deploy to apply` })
      void load()
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function remove(n: string) {
    if (!confirm(`Delete secret "${n}"? Services that use it will fail on the next deploy.`)) return
    try { await api(`/projects/${projectId}/secrets/${encodeURIComponent(n)}`, { method: 'DELETE' }); void load() }
    catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  return (
    <div className="grid2">
      <div>
        <div className="table-wrap"><table>
          <thead><tr><th>Name</th><th>Used by</th><th className="hide-sm">Updated</th><th></th></tr></thead>
          <tbody>
            {list.map(s => {
              // as ${NAME}: the settings of each service that use it
              const asVariable = services.map(svc => ({ svc: svc.name, settings: (svc.environment ?? []).filter(e => usesVariable(e.value, s.name)).map(e => e.name) })).filter(x => x.settings.length > 0)
              const asFile = services.filter(svc => svc.secrets.includes(s.name))
              const elsewhere = asVariable.length === 0 && usesVariable(compose, s.name)
              return (
              <tr key={s.name}>
                <td><b>{s.name}</b></td>
                <td>
                  <div className="secret-uses">
                    {asVariable.map(u => <span key={u.svc} className="chip" title={`${u.svc} gets it in ${u.settings.join(', ')}`}>{u.svc} <span className="muted">{u.settings.join(', ')}</span></span>)}
                    {asFile.map(svc => <span key={svc.name} className="chip" title={`${svc.name} reads the file /run/secrets/${s.name}`}>{svc.name} <span className="muted">/run/secrets/{s.name}</span></span>)}
                    {elsewhere && <span className="muted mini">as <code>${'{'}{s.name}{'}'}</code> in the compose file (not in a service's settings)</span>}
                    {asVariable.length === 0 && asFile.length === 0 && !elsewhere && <span className="warn-text mini">not used: no service gets it</span>}
                  </div>
                  <details className="secret-files">
                    <summary className="mini">as a file in /run/secrets{asFile.length > 0 ? ` (${asFile.length})` : ''}…</summary>
                    <div className="checks">
                      {services.map(svc => (
                        <label key={svc.name} title={`${svc.name} reads it at /run/secrets/${s.name}`}>
                          <input type="checkbox" checked={svc.secrets.includes(s.name)} disabled={busy !== null}
                            onChange={e => { void toggle(svc, s.name, e.target.checked) }} />{svc.name}
                        </label>
                      ))}
                    </div>
                  </details>
                </td>
                <td className="muted nowrap hide-sm">{when(s.updatedAt)}</td>
                <td><div className="actions">
                  <button className="sm" onClick={() => { setEditing(s.name); setValue(''); setMsg(null) }}>change</button>
                  <button className="sm danger" onClick={() => remove(s.name)}>delete</button>
                </div></td>
              </tr>
              )
            })}
            {list.length === 0 && <tr><td colSpan={columns} className="muted">No secrets yet.</td></tr>}
          </tbody>
        </table></div>

        <form className="card form spaced" onSubmit={save}>
          <h2>{editing ? `Change ${editing}` : 'Add secret'}</h2>
          {!editing && <><label htmlFor="secret-name">Name (letters, digits, . - _)</label><input id="secret-name" value={name} onChange={e => setName(e.target.value)} placeholder="db_password" required /></>}
          <label htmlFor="secret-value">Value {editing && '(replaces the stored value)'}</label>
          <textarea id="secret-value" className="code" style={{ height: 120 }} value={value} onChange={e => setValue(e.target.value)} spellCheck={false} autoComplete="off" required />
          <div className="toolbar form-actions">
            <button className="primary" type="submit">Save</button>
            {editing && <button type="button" onClick={() => { setEditing(null); setValue('') }}>Cancel</button>}
            {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
          </div>
        </form>
      </div>

      <div className="card">
        <h2>How to use</h2>
        <p className="muted">Values are stored encrypted on the server and are never shown again. A service gets a secret in one of two ways.</p>
        <p className="muted">As a variable: <code>${'{'}db_password{'}'}</code> anywhere in the compose file is filled in on deploy, like a line of the .env (a secret wins over a .env line of the same name) — for apps that read their settings from the environment. No tick needed:</p>
        <pre className="cmd">{`    environment:
      Database__Password: \${db_password}`}</pre>
        <p className="muted">As a file: tick the services under <i>as a file in /run/secrets</i>; on deploy the agent writes it to a file the service sees at <code>/run/secrets/&lt;name&gt;</code>. In the compose file the ticks are the service's list:</p>
        <pre className="cmd">{`services:
  app:
    image: myapp
    secrets:
      - db_password
    environment:
      DB_PASSWORD_FILE: /run/secrets/db_password`}</pre>
        <p className="muted">Do not declare them in the top-level <code>secrets:</code> section yourself. The file appears in the container as <code>/run/secrets/&lt;name&gt;</code>; changing a value takes effect on the next deploy.</p>
        <p className="mini">The agent must keep its data dir at the same path on the host (<code>-v /var/lib/dockmaster:/var/lib/dockmaster</code>), otherwise Docker cannot find the files — the deploy log warns about it.</p>
      </div>
    </div>
  )
}
