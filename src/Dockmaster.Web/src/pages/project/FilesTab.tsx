import { useCallback, useEffect, useState } from 'react'
import { api, errorText, type MountEdit, type ProjectFile, type ServiceSummary } from '../../lib/api'
import { bytes, when } from '../../lib/fmt'
import { toast } from '../../lib/toast'
import CodeEditor from '../../components/CodeEditor'
import { languageOf } from '../../lib/languages'

const FOLDER = './files/'
const NAME = /^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/

/** Where each service mounts a project file: file name → [service, path in the container]. */
function usage(services: ServiceSummary[]) {
  const map = new Map<string, { service: string; target: string; readOnly: boolean }[]>()
  for (const s of services)
    for (const m of s.mounts)
      if (m.type === 'bind' && m.source.startsWith(FOLDER)) {
        const name = m.source.slice(FOLDER.length).split('/')[0]
        map.set(name, [...(map.get(name) ?? []), { service: s.name, target: m.target, readOnly: m.readOnly }])
      }
  return map
}

/**
 * A project's files — nginx.conf, appsettings.json, a script — edited here and written to every server of the project
 * on deploy. A service uses one by mounting it at a path in its container (from here, or Services → Data); a changed
 * file re-creates the services that mount it on the next deploy.
 */
export default function FilesTab({ projectId, services, running, onMount, onDeploy }: {
  projectId: number
  services: ServiceSummary[]
  running: boolean
  /** writes a service's mounts (all of them), like the Data editor */
  onMount: (service: string, mounts: MountEdit[], deploy: boolean) => Promise<boolean>
  onDeploy: (services: string[]) => void
}) {
  const [files, setFiles] = useState<ProjectFile[] | null>(null)
  const [current, setCurrent] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [content, setContent] = useState('')
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [useIn, setUseIn] = useState({ service: services[0]?.name ?? '', target: '', readOnly: true })

  const load = useCallback(() => api<ProjectFile[]>(`/projects/${projectId}/files`).then(list => { setFiles(list); return list }, e => { setError(errorText(e)); return [] as ProjectFile[] }), [projectId])
  useEffect(() => { void load() }, [load])

  const used = usage(services)
  const isNew = current === null
  const fileName = isNew ? name.trim() : current
  const users = used.get(fileName) ?? []
  const hidden = !isNew && !!files?.find(f => f.name === current)?.hidden

  function open(f: ProjectFile | null) {
    if (dirty && !confirm('Leave this file without saving?')) return
    setCurrent(f?.name ?? null); setName(f?.name ?? ''); setContent(f?.content ?? ''); setDirty(false); setError(null)
    setUseIn(u => ({ ...u, target: f ? guessTarget(f.name) : '' }))
  }

  async function save(deploy: boolean) {
    const n = fileName
    if (!NAME.test(n)) { setError('a file name is letters, digits, ., - and _ — e.g. nginx.conf'); return }
    if (isNew && files?.some(f => f.name === n) && !confirm(`${n} exists already. Replace it?`)) return
    setError(null)
    try {
      await api(`/projects/${projectId}/files/${encodeURIComponent(n)}`, { method: 'PUT', json: { content } })
      if (isNew) setUseIn(u => ({ ...u, target: guessTarget(n) }))
      setDirty(false); setCurrent(n)
      await load()
      const who = (used.get(n) ?? []).map(u => u.service)
      if (deploy && who.length) { onDeploy([...new Set(who)]); toast(`${n} saved — deploying ${[...new Set(who)].join(', ')}`) }
      else toast(who.length ? `${n} saved — deploy ${[...new Set(who)].join(', ')} to use it` : `${n} saved — mount it in a service below`)
    } catch (e) { setError(errorText(e)) }
  }

  async function remove() {
    if (current === null) return
    // docker would make an empty folder in its place, and the service fails: the mounts go first
    const who = [...new Set(users.map(u => u.service))]
    if (who.length) { setError(`${who.join(', ')} ${who.length === 1 ? 'uses' : 'use'} ${current} — stop using it first (Used by, below)`); return }
    if (!confirm(`Delete ${current}?`)) return
    try { await api(`/projects/${projectId}/files/${encodeURIComponent(current)}`, { method: 'DELETE' }); setCurrent(null); setName(''); setContent(''); setDirty(false); await load() }
    catch (e) { setError(errorText(e)) }
  }

  /** mounts the open file into a service: its mounts as they are, plus this one */
  async function mount(deploy: boolean) {
    const s = services.find(x => x.name === useIn.service)
    if (!s || current === null) return
    if (!useIn.target.startsWith('/')) { setError('the path in the container starts with /, e.g. /etc/nginx/conf.d/default.conf'); return }
    const keep: MountEdit[] = s.mounts.map((_, i) => ({ keep: i, type: '', source: '', target: '', readOnly: false }))
    await onMount(s.name, [...keep, { keep: null, type: 'bind', source: FOLDER + current, target: useIn.target.trim(), readOnly: useIn.readOnly }], deploy)
  }

  /** takes the open file out of a service's mounts, the others stay as they are */
  async function unmount(service: string) {
    const s = services.find(x => x.name === service)
    if (!s || current === null || !confirm(`Stop mounting ${current} into ${service}?`)) return
    const keep: MountEdit[] = s.mounts.flatMap((m, i) => m.type === 'bind' && m.source.startsWith(FOLDER) && m.source.slice(FOLDER.length).split('/')[0] === current
      ? [] : [{ keep: i, type: '', source: '', target: '', readOnly: false }])
    await onMount(s.name, keep, false)
  }

  return (
    <div className="files">
      <div className="files-list">
        <button className={'files-item new' + (isNew ? ' on' : '')} onClick={() => open(null)}>+ new file</button>
        {files?.map(f => (
          <button key={f.name} className={'files-item' + (f.name === current ? ' on' : '')} onClick={() => open(f)}>
            <b>{f.name}</b>
            <span className="mini">{bytes(f.size ?? new Blob([f.content]).size)} · {(used.get(f.name) ?? []).map(u => u.service).join(', ') || 'not used yet'}</span>
          </button>
        ))}
        {files?.length === 0 && <p className="mini">Config files a service needs — <code>nginx.conf</code>, <code>appsettings.json</code>, a start script — live here instead of on one server by hand. On deploy each server of the project gets them.</p>}
      </div>

      <div className="files-editor">
        <div className="toolbar">
          {isNew
            ? <input aria-label="file name" className="files-name" value={name} onChange={e => { setName(e.target.value); setDirty(true) }} placeholder="file name, e.g. nginx.conf" />
            : <h2 className="files-title">{current}</h2>}
          <div className="spacer" />
          {users.length > 0 && <button className="primary sm" disabled={hidden || running || (!dirty && !isNew)} onClick={() => { void save(true) }} title={`save and re-create ${[...new Set(users.map(u => u.service))].join(', ')} with it`}>Save &amp; deploy</button>}
          <button className={users.length ? 'sm' : 'sm primary'} disabled={hidden || (!dirty && !isNew)} onClick={() => { void save(false) }}>Save</button>
          {!isNew && <button className="sm danger" onClick={() => { void remove() }}>delete</button>}
        </div>
        {error && <p className="error">{error}</p>}
        {hidden
          ? <p className="muted">The content is hidden: config files often hold passwords, and seeing or changing them takes the Secrets permission.</p>
          : <CodeEditor language={languageOf(fileName)} value={content} onChange={v => { setContent(v); setDirty(true) }} minHeight={320} />}
        {!isNew && files?.find(f => f.name === current) && <p className="mini">saved {when(files.find(f => f.name === current)!.updatedAt)}{dirty && <span className="warn-text"> · changed, not saved</span>}</p>}

        {!isNew && (
          <div className="card spaced">
            <h3>Used by</h3>
            {users.length === 0 ? <p className="mini">No service mounts it yet.</p> : (
              <ul className="files-users">{users.map(u => <li key={u.service + u.target}><b>{u.service}</b> at <code>{u.target}</code>{u.readOnly && <span className="mini"> read-only</span>}{' '}
                <button className="sm" onClick={() => { void unmount(u.service) }} title="take it out of this service's mounts (deploy the service afterwards)">stop using</button></li>)}</ul>
            )}
            {services.length > 0 && (
              <div className="files-mount">
                <select aria-label="service" value={useIn.service} onChange={e => setUseIn(u => ({ ...u, service: e.target.value }))}>
                  {services.map(s => <option key={s.name} value={s.name}>{s.name}</option>)}
                </select>
                <span className="muted">at</span>
                <input aria-label="path in the container" value={useIn.target} onChange={e => setUseIn(u => ({ ...u, target: e.target.value }))} placeholder="/etc/nginx/conf.d/default.conf" />
                <label className="check-inline"><input type="checkbox" checked={useIn.readOnly} onChange={e => setUseIn(u => ({ ...u, readOnly: e.target.checked }))} /> read-only</label>
                <button className="sm primary" disabled={running || !useIn.target} onClick={() => { void mount(true) }} title="mount it and re-create the service">use &amp; deploy</button>
                <button className="sm" disabled={!useIn.target} onClick={() => { void mount(false) }}>use</button>
              </div>
            )}
            <p className="mini">The path is where the service expects the file, e.g. <code>/etc/nginx/conf.d/default.conf</code> for nginx. Changing the file here re-creates the services that use it on their next deploy.</p>
          </div>
        )}
      </div>
    </div>
  )
}

/** Where the usual files go, as a first suggestion. */
function guessTarget(name: string) {
  const n = name.toLowerCase()
  if (n === 'nginx.conf') return '/etc/nginx/nginx.conf'
  if (n.endsWith('.conf') && n.includes('nginx') || n === 'default.conf') return '/etc/nginx/conf.d/default.conf'
  if (n.startsWith('appsettings') && n.endsWith('.json')) return '/app/' + name
  if (n === 'my.cnf') return '/etc/mysql/conf.d/my.cnf'
  if (n === 'redis.conf') return '/usr/local/etc/redis/redis.conf'
  if (n === 'prometheus.yml') return '/etc/prometheus/prometheus.yml'
  return '/config/' + name
}
