import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, type Agent, type Instance, type InstanceResult, type InstanceState, type Template, type TemplateVariable } from '../lib/api'
import { bytes, pct } from '../lib/fmt'
import { POLL, usePoll } from '../lib/hooks'
import CodeEditor from '../components/CodeEditor'
import Modal from '../components/Modal'
import RowMenu from '../components/RowMenu'
import { Ago } from '../components/Status'

type Msg = { ok: boolean; text: string } | null

const EXAMPLE = `services:
  app:
    image: registry.example.com/team/app:latest
    environment:
      INSTANCE: \${INSTANCE}
      SETTINGS: \${SETTINGS}
    deploy:
      resources: { limits: { cpus: "1", memory: 512M } }
`

const emptyForm = (): Template => ({
  id: 0, name: '', description: '', composeYaml: EXAMPLE, envFile: '', variables: [], agentIds: [], maxInstances: 0, healthTimeoutSeconds: 180, instances: 0, updatedAt: '',
  warnings: [],
})

/**
 * Templates: a compose project to start many times over. A program with an API key creates instances of the templates
 * the key lists (each one a project of its own, named template-instance) and only fills in the template's variables.
 */
export default function Templates({ agents }: { agents: Agent[] }) {
  const [list, setList] = useState<Template[] | null>(null)
  const [form, setForm] = useState<Template | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [msg, setMsg] = useState<Msg>(null)

  const load = useCallback(() => api<Template[]>('/templates').then(setList).catch(err => setMsg({ ok: false, text: errorText(err) })), [])
  useEffect(() => { void load() }, [load])

  async function remove(t: Template) {
    if (!confirm(`Delete template "${t.name}"?`)) return
    try { await api(`/templates/${t.id}`, { method: 'DELETE' }); if (selected === t.name) setSelected(null); void load() }
    catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  const current = list?.find(t => t.name === selected) ?? null
  const serverNames = (ids: number[]) => ids.length === 0 ? 'any server' : ids.map(id => agents.find(a => a.id === id)?.name ?? `#${id}`).join(', ')

  return (
    <>
      <div className="toolbar">
        <h1>Templates</h1>
        {msg && !form && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
        <div className="spacer" />
        <button className="primary" onClick={() => { setMsg(null); setForm(emptyForm()) }}>+ New template</button>
      </div>
      <p className="mini">
        A template is a compose project a program starts many times over — a game server per player, a preview per branch. The program sends only
        the template's variables with an API key (Settings → API keys); the compose file stays yours, so the key cannot mount the docker socket or touch
        other projects. Each instance is an ordinary project named <code>template-instance</code>, with its history, metrics and events.
      </p>

      <div className="table-wrap"><table>
        <thead><tr><th>Template</th><th className="hide-sm">Description</th><th>Instances</th><th className="hide-md">Servers</th><th className="hide-md">Updated</th><th></th></tr></thead>
        <tbody>
          {list?.map(t => (
            <tr key={t.id} className={selected === t.name ? 'selected' : ''}>
              <td className="nowrap"><button className="link" onClick={() => setSelected(t.name)} title="show its instances">{t.name}</button></td>
              <td className="muted trunc hide-sm">{t.description || '—'}</td>
              <td className="nowrap">{t.instances}{t.maxInstances > 0 && <span className="muted"> / {t.maxInstances}</span>}</td>
              <td className="muted hide-md">{serverNames(t.agentIds)}</td>
              <td className="hide-md"><Ago iso={t.updatedAt} /></td>
              <td className="menu-cell">
                <RowMenu title={`actions for ${t.name}`} items={[
                  { label: 'instances', onClick: () => setSelected(t.name) },
                  { label: 'edit', onClick: () => { setMsg(null); setForm({ ...t, variables: t.variables.map(v => ({ ...v })) }) } },
                  { label: 'delete', danger: true, onClick: () => { void remove(t) } },
                ]} />
              </td>
            </tr>
          ))}
          {list?.length === 0 && <tr><td colSpan={6} className="muted">No templates yet.</td></tr>}
        </tbody>
      </table></div>

      {current && <Instances key={current.id} template={current} agents={agents} onClose={() => setSelected(null)} onChanged={() => { void load() }} />}

      {form && <TemplateForm key={`${form.id}:${form.updatedAt}`} form={form} agents={agents} onClose={() => setForm(null)} onSaved={t => {
        // a variable in a host path, port or image is worth a second look: the form stays open with the warnings
        if (t.warnings.length > 0) setForm({ ...t, variables: t.variables.map(v => ({ ...v })) })
        else setForm(null)
        setMsg({ ok: true, text: 'saved' }); setSelected(t.name); void load()
      }} />}
    </>
  )
}

function TemplateForm({ form: initial, agents, onClose, onSaved }: { form: Template; agents: Agent[]; onClose: () => void; onSaved: (t: Template) => void }) {
  const [f, setF] = useState(initial)
  const [error, setError] = useState<string | null>(null)
  const setVar = (i: number, v: Partial<TemplateVariable>) => setF({ ...f, variables: f.variables.map((x, j) => j === i ? { ...x, ...v } : x) })

  async function save() {
    setError(null)
    const body = {
      name: f.name, description: f.description, composeYaml: f.composeYaml, envFile: f.envFile, agentIds: f.agentIds,
      maxInstances: f.maxInstances, healthTimeoutSeconds: f.healthTimeoutSeconds,
      variables: f.variables.filter(v => v.name.trim()).map(v => ({ ...v, default: v.default === '' ? null : v.default })),
    }
    try { onSaved(await api<Template>(f.id ? `/templates/${f.id}` : '/templates', { method: f.id ? 'PUT' : 'POST', json: body })) }
    catch (err) { setError(errorText(err)) }
  }

  return (
    <Modal label={f.id ? `Edit template ${initial.name}` : 'New template'} wide onClose={onClose} onSubmit={() => { void save() }} autoComplete="off">
      <div className="row">
        <div><label htmlFor="t-name">Name <span className="muted">(instances are projects named name-instance)</span></label>
          <input id="t-name" value={f.name} onChange={e => setF({ ...f, name: e.target.value })} placeholder="game-server" required autoFocus disabled={f.instances > 0}
            title={f.instances > 0 ? 'cannot change while it has instances' : undefined} /></div>
        <div><label htmlFor="t-desc">Description</label><input id="t-desc" value={f.description} onChange={e => setF({ ...f, description: e.target.value })} /></div>
      </div>

      <label htmlFor="t-compose">Compose file — <code>{'${INSTANCE}'}</code> and <code>{'${TEMPLATE}'}</code> are always set, the variables below as named</label>
      <CodeEditor textareaId="t-compose" value={f.composeYaml} onChange={v => setF({ ...f, composeYaml: v })} language="yaml" minHeight={220} />
      <label htmlFor="t-env">.env every instance starts from <span className="muted">(the instance's variables are added below it)</span></label>
      <CodeEditor textareaId="t-env" value={f.envFile} onChange={v => setF({ ...f, envFile: v })} language="properties" minHeight={60} />

      <div className="field-label">Variables a caller may set</div>
      {f.variables.length > 0 && (
        <div className="table-wrap template-vars"><table>
          <thead><tr><th>Name</th><th>Pattern <span className="muted">(empty: a word of letters, digits, _ . -)</span></th><th>Default</th><th>Required</th><th className="hide-sm">Description</th><th></th></tr></thead>
          <tbody>
            {f.variables.map((v, i) => (
              <tr key={i}>
                <td><input aria-label="variable name" value={v.name} onChange={e => setVar(i, { name: e.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, '_') })} placeholder="SETTINGS" /></td>
                <td><input aria-label="pattern" value={v.pattern ?? ''} onChange={e => setVar(i, { pattern: e.target.value || null })} placeholder="[0-9]{1,4}" /></td>
                <td><input aria-label="default" value={v.default ?? ''} onChange={e => setVar(i, { default: e.target.value })} disabled={v.required} /></td>
                <td><input aria-label="required" type="checkbox" checked={v.required} onChange={e => setVar(i, { required: e.target.checked, default: e.target.checked ? null : v.default })} /></td>
                <td className="hide-sm"><input aria-label="description" value={v.description ?? ''} onChange={e => setVar(i, { description: e.target.value || null })} /></td>
                <td><button type="button" className="icon" aria-label={`remove ${v.name || 'variable'}`} onClick={() => setF({ ...f, variables: f.variables.filter((_, j) => j !== i) })}>×</button></td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      <button type="button" className="sm" onClick={() => setF({ ...f, variables: [...f.variables, { name: '', pattern: null, required: false, default: null, description: null }] })}>+ variable</button>
      {initial.warnings.length > 0 && <div>
        <p className="warn-text">Check these variables: a program with an API key picks their values.</p>
        <ul className="warn-text">{initial.warnings.map(w => <li key={w}>{w}</li>)}</ul>
      </div>}

      <div className="field-label">Servers instances may run on <span className="muted">(none ticked: any; a new instance goes to the online one with the most memory free)</span></div>
      <div className="check-list">
        {agents.map(a => (
          <label key={a.id} className="check-inline">
            <input type="checkbox" checked={f.agentIds.includes(a.id)}
              onChange={e => setF({ ...f, agentIds: e.target.checked ? [...f.agentIds, a.id] : f.agentIds.filter(id => id !== a.id) })} /> {a.name}
          </label>
        ))}
      </div>
      <div className="row">
        <div className="narrow"><label htmlFor="t-max">Most instances <span className="muted">(0 = no limit)</span></label>
          <input id="t-max" inputMode="numeric" value={f.maxInstances} onChange={e => setF({ ...f, maxInstances: Number(e.target.value.replace(/\D/g, '')) || 0 })} /></div>
        <div className="narrow"><label htmlFor="t-timeout">Ready timeout, s</label>
          <input id="t-timeout" inputMode="numeric" value={f.healthTimeoutSeconds} onChange={e => setF({ ...f, healthTimeoutSeconds: Number(e.target.value.replace(/\D/g, '')) || 0 })} /></div>
      </div>
      {f.id > 0 && f.instances > 0 && <p className="mini">The {f.instances} instance(s) take the changed files on their next deploy (start, stop and restart keep what they run).</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit">Save</button>
        <button type="button" onClick={onClose}>Cancel</button>
        {error && <span className="error">{error}</span>}
      </div>
    </Modal>
  )
}

const STATE_CLASS: Record<InstanceState, string> = {
  new: 'muted', deploying: 'warn', running: 'ok', starting: 'warn', unhealthy: 'bad', partial: 'warn', stopped: 'muted',
}

/** The instances of one template, refreshed every few seconds, with what the API offers on each. */
function Instances({ template: t, agents, onClose, onChanged }: { template: Template; agents: Agent[]; onClose: () => void; onChanged: () => void }) {
  const [list, setList] = useState<Instance[] | null>(null)
  const [msg, setMsg] = useState<Msg>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const base = `/templates/${encodeURIComponent(t.name)}/instances`

  const load = useCallback(() => { api<Instance[]>(base).then(setList).catch(err => setMsg({ ok: false, text: errorText(err) })) }, [base])
  usePoll(load, POLL.projects)

  async function run(i: Instance, op: string) {
    setBusy(i.name); setMsg({ ok: true, text: `${op} ${i.name}…` })
    try {
      const r = op === 'delete'
        ? await api<InstanceResult>(`${base}/${i.name}`, { method: 'DELETE' })
        : await api<InstanceResult>(`${base}/${i.name}/${op}`, { method: 'POST' })
      setMsg({ ok: r.ok, text: `${i.name}: ${r.message}` })
    } catch (err) { setMsg({ ok: false, text: `${i.name}: ${errorText(err)}` }) }
    finally { setBusy(null); load(); if (op === 'delete') onChanged() }
  }

  const origin = window.location.origin
  return (
    <div className="card instances">
      <div className="toolbar">
        <h2>Instances of {t.name}</h2>
        {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
        <div className="spacer" />
        <button className="sm" onClick={() => setCreating(true)}>+ New instance</button>
        <button className="icon" title="close" aria-label="close the instances" onClick={onClose}>×</button>
      </div>
      <div className="table-wrap"><table>
        <thead><tr><th>Instance</th><th>State</th><th className="hide-sm">Server</th><th className="hide-md">CPU / memory</th><th className="hide-md">Variables</th><th>Last deploy</th><th></th></tr></thead>
        <tbody>
          {list?.map(i => (
            <tr key={i.name}>
              <td className="nowrap"><Link to={`/projects/${i.projectId}`} title="open its project">{i.name}</Link></td>
              <td className="nowrap"><span className={'badge ' + STATE_CLASS[i.state]}>{i.state}</span>{i.total > 0 && <span className="muted mini"> {i.running}/{i.total}</span>}</td>
              <td className="muted hide-sm">{i.server}</td>
              <td className="muted hide-md nowrap">{i.running > 0 ? `${pct(i.cpuPercent)} · ${bytes(i.memoryUsage)}` : '—'}</td>
              <td className="muted trunc hide-md" title={Object.entries(i.variables).map(([k, v]) => `${k}=${v}`).join('\n')}>
                {Object.keys(i.variables).length === 0 ? '—' : Object.entries(i.variables).map(([k, v]) => `${k}=${v}`).join(' · ')}
              </td>
              <td><Ago iso={i.lastDeployedAt} suffix={i.lastDeployOk === false ? '✗' : i.lastDeployOk ? '✓' : ''} /></td>
              <td className="menu-cell">
                {busy === i.name ? <span className="muted mini">…</span> : (
                  <RowMenu title={`actions for ${i.name}`} items={[
                    { label: 'pull & deploy', onClick: () => { void run(i, 'deploy') } },
                    { label: 'start', onClick: () => { void run(i, 'start') } },
                    { label: 'stop', onClick: () => { void run(i, 'stop') } },
                    { label: 'restart', onClick: () => { void run(i, 'restart') } },
                    { label: 'delete', danger: true, onClick: () => { if (confirm(`Take ${i.name} down and delete it?`)) void run(i, 'delete') } },
                  ]} />
                )}
              </td>
            </tr>
          ))}
          {list?.length === 0 && <tr><td colSpan={7} className="muted">No instances. A program creates them with an API key, or add one here.</td></tr>}
        </tbody>
      </table></div>
      <details>
        <summary>Calling the API</summary>
        <pre className="cmd">{`# create and deploy (answers when it runs; ?wait=false answers at once)
curl -fsS -X POST ${origin}/api${base} \\
  -H "Authorization: Bearer $DOCKMASTER_KEY" -H "Content-Type: application/json" \\
  -d '{"name":"abc123","variables":{${t.variables.slice(0, 2).map(v => `"${v.name}":"…"`).join(',')}}}'

curl -fsS -H "Authorization: Bearer $DOCKMASTER_KEY" ${origin}/api${base}/abc123          # state
curl -fsS -X POST -H "Authorization: Bearer $DOCKMASTER_KEY" ${origin}/api${base}/abc123/stop  # or start, restart, deploy
curl -fsS -X DELETE -H "Authorization: Bearer $DOCKMASTER_KEY" ${origin}/api${base}/abc123     # down and forget`}</pre>
      </details>
      {creating && <NewInstance template={t} agents={agents} base={base} onClose={() => setCreating(false)}
        onCreated={text => { setCreating(false); setMsg({ ok: true, text }); load(); onChanged() }} />}
    </div>
  )
}

function NewInstance({ template: t, agents, base, onClose, onCreated }: { template: Template; agents: Agent[]; base: string; onClose: () => void; onCreated: (text: string) => void }) {
  const [name, setName] = useState('')
  const [server, setServer] = useState('')
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(t.variables.map(v => [v.name, v.default ?? ''])))
  const [deploy, setDeploy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const allowed = t.agentIds.length === 0 ? agents : agents.filter(a => t.agentIds.includes(a.id))

  async function create() {
    setError(null); setBusy(true)
    // empty optional fields are left out: the template's default (or nothing) applies
    const variables = Object.fromEntries(Object.entries(values).filter(([k, v]) => v !== '' || t.variables.find(x => x.name === k)?.required))
    try {
      const r = await api<InstanceResult>(base, { method: 'POST', json: { name, variables, server: server || null, deploy } })
      onCreated(`${name}: ${r.message}`)
    } catch (err) { setError(errorText(err)) }
    finally { setBusy(false) }
  }

  return (
    <Modal label={`New instance of ${t.name}`} onClose={onClose} onSubmit={() => { void create() }} autoComplete="off">
      <label htmlFor="i-name">Name <span className="muted">(lowercase letters, digits, '-'; the project is {t.name}-{name || '…'})</span></label>
      <input id="i-name" value={name} onChange={e => setName(e.target.value.toLowerCase())} required autoFocus />
      {t.variables.map(v => (
        <div key={v.name}>
          <label htmlFor={'i-' + v.name}>{v.name}{v.required ? ' *' : ''} {v.description && <span className="muted">— {v.description}</span>}</label>
          <input id={'i-' + v.name} value={values[v.name] ?? ''} onChange={e => setValues({ ...values, [v.name]: e.target.value })} placeholder={v.pattern ?? ''} required={v.required} />
        </div>
      ))}
      <label htmlFor="i-server">Server</label>
      <select id="i-server" value={server} onChange={e => setServer(e.target.value)}>
        <option value="">the online one with the most memory free</option>
        {allowed.map(a => <option key={a.id} value={a.name}>{a.name}{a.online ? '' : ' (offline)'}</option>)}
      </select>
      <label className="check"><input type="checkbox" checked={deploy} onChange={e => setDeploy(e.target.checked)} /> deploy it now</label>
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy}>{busy ? 'Creating…' : 'Create'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
        {error && <span className="error">{error}</span>}
      </div>
    </Modal>
  )
}
