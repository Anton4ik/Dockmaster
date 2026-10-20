import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import type { Agent, MountEdit, MountSummary, ProjectVolumes, ServiceSummary, Volume } from '../../lib/api'
import { bytes } from '../../lib/fmt'
import Modal from '../../components/Modal'
import { dataPaths, freshVolumeName, unkeptPaths } from './dataPaths'

/** One mount while it is edited; `keep` = the index of the compose entry it still is, unchanged. */
interface Row {
  id: number
  keep: number | null
  orig?: MountSummary
  type: string
  source: string
  target: string
  readOnly: boolean
  /** an existing volume of the server, not the project's own */
  externalName: string | null
  /** a new volume: its name is typed */
  fresh: boolean
}

const NAME = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/
/** where a project's files are, as a service mounts them */
const FILES = './files/'
/** a folder of the server, or one of the project's files (a bind mount from ./files/) */
const kindOf = (r: { type: string; source: string }) => r.type === 'bind' && r.source.startsWith(FILES) ? 'file' : r.type
let nextId = 1

/**
 * Where a service keeps its data, as a form: each mount is a volume (docker keeps it) or a folder of the server, at a
 * path in the container. The paths the image keeps data in are offered, and a path nothing keeps is pointed out.
 * Saved into the compose file (kept entries stay as written); the service needs a deploy to use it.
 */
export default function MountsEditor({ service, services, project, volumes, files, serverIds, agents, running, onSave, onClose }: {
  service: ServiceSummary
  /** the project's files (Files tab): mountable as ./files/<name> */
  files: string[]
  services: ServiceSummary[]
  project: string
  volumes?: ProjectVolumes
  /** the servers the service runs on */
  serverIds: number[]
  agents: Agent[]
  running: boolean
  onSave: (mounts: MountEdit[], deploy: boolean) => Promise<boolean>
  onClose: () => void
}) {
  const [rows, setRows] = useState<Row[]>(() => service.mounts.map((m, i) => ({
    id: nextId++, keep: i, orig: m, type: m.type, source: m.source, target: m.target, readOnly: m.readOnly, externalName: null, fresh: false,
  })))
  const [busy, setBusy] = useState(false)

  // the project's own volumes (any service's), and the other volumes of the servers the service runs on
  const projectKeys = useMemo(() => {
    const keys = new Map<string, string[]>()
    for (const s of services) for (const m of s.mounts) if (m.type === 'volume') keys.set(m.source, [...(keys.get(m.source) ?? []), s.name])
    return keys
  }, [services])
  const serverVolumes = useMemo(() => {
    const byName = new Map<string, { v: Volume; server: string }>()
    for (const srv of volumes?.servers ?? []) {
      if (!serverIds.includes(srv.agentId)) continue
      for (const v of srv.volumes ?? []) if (!v.anonymous && !byName.has(v.name)) byName.set(v.name, { v, server: srv.agentName })
    }
    return [...byName.values()]
  }, [volumes, serverIds])
  // volumes of this project left on the server that no service mounts now: offered under their key
  const leftover = serverVolumes.filter(x => x.v.project === project && x.v.composeName && !projectKeys.has(x.v.composeName))
  const others = serverVolumes.filter(x => x.v.project !== project)
  const serverNames = serverIds.map(id => agents.find(a => a.id === id)?.name ?? '?')
  const volumeFor = (name: string | null) => name ? serverVolumes.find(x => x.v.name === name)?.v : undefined

  const taken = new Set([...projectKeys.keys(), ...rows.map(r => r.source)])
  const change = (id: number, patch: Partial<Row>) => setRows(rs => rs.map(r => r.id === id ? { ...r, ...patch, keep: null } : r))
  const add = (row: Partial<Row>) => setRows(rs => [...rs, { id: nextId++, keep: null, type: 'volume', source: '', target: '', readOnly: false, externalName: null, fresh: true, ...row }])
  const remove = (id: number) => setRows(rs => rs.filter(r => r.id !== id))
  const keepIn = (path: string) => add({ source: freshVolumeName(service.name, taken, path.split('/').filter(Boolean).pop()), target: path })

  const suggestions = dataPaths(service, volumes)
  const unkept = unkeptPaths(service, volumes, rows)

  /** what a volume row's picker shows */
  const pickValue = (r: Row) => r.fresh ? '__new' : r.externalName ? 'ext:' + r.externalName : 'key:' + r.source
  function pick(r: Row, value: string) {
    if (value === '__new') change(r.id, { fresh: true, externalName: null, source: freshVolumeName(service.name, taken) })
    else if (value.startsWith('key:')) change(r.id, { fresh: false, externalName: null, source: value.slice(4) })
    else {
      const name = value.slice(4)
      // declared in the file already (another service mounts it): the same key, nothing to declare
      const declared = services.flatMap(s => s.mounts).find(m => m.type === 'volume' && m.volumeName === name)
      change(r.id, declared ? { fresh: false, externalName: null, source: declared.source } : { fresh: false, externalName: name, source: name })
    }
  }

  const problem = (r: Row): string | null => {
    if (r.keep !== null || !r.target) return null
    if (!r.target.startsWith('/') || r.target.includes(':') || r.target === '/') return 'the path in the container starts with /'
    if (rows.some(o => o !== r && o.target.replace(/\/+$/, '') === r.target.replace(/\/+$/, ''))) return 'another mount uses this path'
    if (r.type === 'volume' && !NAME.test(r.source)) return 'a volume name is letters, digits, -, _ and .'
    if (kindOf(r) === 'file' && !files.includes(r.source.slice(FILES.length))) return "choose one of the project's files (Files tab)"
    if (r.type === 'bind' && !/^(\/|\.\.?\/)/.test(r.source)) return 'a folder is /srv/app on the server, or ./data in the project\'s folder'
    if (r.type === 'bind' && r.source.includes(':')) return 'a folder cannot contain :'
    return null
  }
  const problems = rows.map(problem)
  const changed = rows.some(r => r.keep === null) || rows.length !== service.mounts.length || rows.some((r, i) => r.keep !== i)
  // a new row without its path yet is not an error, only not ready
  const blocked = problems.some(Boolean) || rows.some(r => r.keep === null && !r.target.trim())

  async function save(deploy: boolean) {
    setBusy(true)
    const ok = await onSave(rows.map(r => r.keep !== null
      ? { keep: r.keep, type: '', source: '', target: '', readOnly: false }
      : { keep: null, type: r.type, source: r.source.trim(), target: r.target.trim(), readOnly: r.readOnly, externalName: r.externalName }), deploy)
    setBusy(false)
    if (ok) onClose()
  }

  const keeps = rows.filter(r => r.type === 'volume' || r.type === 'bind')
  return (
    <Modal label={`Data of ${service.name}`} wide onClose={onClose} onSubmit={() => { if (!blocked && changed) void save(!running) }}>
      <p className="mini">What a container writes is <b>lost when it is re-created</b> — on every update. Files that must stay (a database, uploads, settings) go into a <b>volume</b>, which docker keeps on the server, or a <b>folder of the server</b>.</p>

      {unkept.length > 0 && (
        <div className="callout warn">
          {unkept.map(p => (
            <div key={p} className="callout-line">
              <span>The image keeps its data in <code>{p}</code> — <b>not kept yet</b>, it is lost on the next update.</span>
              <button type="button" className="sm primary" onClick={() => keepIn(p)}>keep it in a volume</button>
            </div>
          ))}
        </div>
      )}

      {rows.length === 0 ? <p className="muted">No data is kept: this service starts from its image every time.</p> : (
        <div className="mounts">
          <div className="mount-row head mini" aria-hidden="true"><span>Kind</span><span>Volume or folder</span><span /><span>Path in the container</span><span>Read-only</span><span /></div>
          {rows.map((r, i) => {
            const locked = r.keep !== null && r.orig && !r.orig.editable
            const volume = r.type === 'volume' ? volumeFor(r.externalName ?? r.orig?.volumeName ?? (r.fresh ? null : `${project}_${r.source}`)) : undefined
            return (
              <div key={r.id} className={'mount-row' + (problems[i] ? ' bad' : '')}>
                {locked ? (
                  <span className="mount-locked">
                    <code>{r.orig!.type}</code> <code>{r.source || '—'}</code> → <code>{r.target}</code>
                    <span className="mini"> written with options this page does not show — change it in the Compose tab</span>
                  </span>
                ) : (
                  <>
                    <select aria-label="kind" value={kindOf(r)} onChange={e => change(r.id, e.target.value === 'file'
                      ? { type: 'bind', source: FILES + (files[0] ?? ''), externalName: null, fresh: false, readOnly: true }
                      : e.target.value === 'bind'
                      ? { type: 'bind', source: kindOf(r) === 'bind' ? r.source : './' + (r.type === 'volume' && r.source ? r.source : 'data'), externalName: null, fresh: false }
                      : { type: 'volume', source: r.type === 'volume' ? r.source : freshVolumeName(service.name, taken), fresh: r.type !== 'volume', externalName: null })}>
                      <option value="volume">Volume</option>
                      <option value="bind">Folder of the server</option>
                      <option value="file" disabled={files.length === 0} title={files.length === 0 ? 'make one in the Files tab first' : undefined}>Project file</option>
                      {r.type === 'anonymous' && <option value="anonymous">Unnamed volume</option>}
                    </select>
                    <span className="mount-source">
                      {r.type === 'volume' && (
                        <>
                          <select aria-label="volume" value={pickValue(r)} onChange={e => pick(r, e.target.value)}>
                            <option value="__new">new volume…</option>
                            {[...projectKeys.keys()].map(k => <option key={k} value={'key:' + k}>{k}{projectKeys.get(k)!.some(n => n !== service.name) ? ` (shared with ${projectKeys.get(k)!.filter(n => n !== service.name).join(', ')})` : ''}</option>)}
                            {leftover.map(x => <option key={x.v.name} value={'key:' + x.v.composeName}>{x.v.composeName} (left on {x.server}{x.v.size > 0 ? `, ${bytes(x.v.size)}` : ''})</option>)}
                            {others.length > 0 && <optgroup label={`other volumes on ${serverNames.join(', ')}`}>
                              {others.map(x => <option key={x.v.name} value={'ext:' + x.v.name}>{x.v.name}{x.v.share ? ` — ${x.v.share}` : ''}{x.v.containers.length ? ` (used by ${x.v.containers[0].name}${x.v.containers.length > 1 ? ' …' : ''})` : ''}</option>)}
                            </optgroup>}
                            {!r.fresh && !r.externalName && !projectKeys.has(r.source) && !leftover.some(x => x.v.composeName === r.source) && <option value={'key:' + r.source}>{r.source}</option>}
                          </select>
                          {r.fresh && <input aria-label="name of the new volume" value={r.source} onChange={e => change(r.id, { source: e.target.value, fresh: true })} placeholder="name" />}
                        </>
                      )}
                      {kindOf(r) === 'file' && (
                        <select aria-label="project file" value={r.source.slice(FILES.length)} onChange={e => change(r.id, { source: FILES + e.target.value })}>
                          {!files.includes(r.source.slice(FILES.length)) && <option value={r.source.slice(FILES.length)}>{r.source.slice(FILES.length) || 'choose a file'}</option>}
                          {files.map(f => <option key={f} value={f}>{f}</option>)}
                        </select>
                      )}
                      {kindOf(r) === 'bind' && (
                        <input aria-label="folder of the server" value={r.source} onChange={e => change(r.id, { source: e.target.value })} placeholder="./data or /srv/app"
                          title="./name: a folder in the project's folder on the server (kept by Down); /path: any folder of the server" />
                      )}
                      {r.type === 'anonymous' && (
                        <span className="warn-text mini">docker names it itself: a new container starts empty{' '}
                          <button type="button" className="link" onClick={() => change(r.id, { type: 'volume', fresh: true, source: freshVolumeName(service.name, taken, r.target.split('/').filter(Boolean).pop()) })}>give it a name</button>
                        </span>
                      )}
                    </span>
                    <span className="muted" aria-hidden="true">→</span>
                    <input aria-label="path in the container" list={`paths-${service.name}`} value={r.target} onChange={e => change(r.id, { target: e.target.value })} placeholder="/data" />
                    <label className="check-inline" title="the container may only read these files">
                      <input type="checkbox" checked={r.readOnly} disabled={r.type === 'anonymous'} onChange={e => change(r.id, { readOnly: e.target.checked })} /> <span className="show-sm">read-only</span>
                    </label>
                  </>
                )}
                <button type="button" className="icon" onClick={() => remove(r.id)} aria-label="remove this mount" title="remove this mount (the data itself stays on the server)">✕</button>
                {(problems[i] || volume || (r.keep === null && !r.target)) && (
                  <div className="mount-note mini">
                    {problems[i] ? <span className="error">{problems[i]}</span> : !r.target ? <span>where the container reads and writes these files, e.g. <code>{suggestions[0] ?? '/data'}</code></span> : volume && <>
                      on the server: <Link to={`/agents/${serverIds[0]}/volumes?q=${encodeURIComponent(volume.name)}`}>{volume.name}</Link>
                      {volume.size > 0 && `, ${bytes(volume.size)}`}{volume.share && `, ${volume.share}`}
                    </>}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      )}
      <datalist id={`paths-${service.name}`}>{suggestions.map(p => <option key={p} value={p} />)}</datalist>

      <div className="toolbar">
        <button type="button" className="sm" onClick={() => add({ source: freshVolumeName(service.name, taken), target: unkept[0] ?? '' })}>+ volume</button>
        <button type="button" className="sm" onClick={() => add({ type: 'bind', source: './', fresh: false })}>+ folder of the server</button>
        {files.length > 0 && <button type="button" className="sm" onClick={() => add({ type: 'bind', source: FILES + files[0], fresh: false, readOnly: true })}>+ project file</button>}
      </div>
      <p className="mini">
        <b>Volume</b>: docker keeps it on the server; its size and who uses it are on the server's <i>Volumes</i> page, where a <b>NAS share</b> can be made a volume too.
        {' '}<b>Folder of the server</b>: <code>./data</code> is a folder in the project's folder on the server, <code>/srv/app</code> any folder.
        {' '}<b>Project file</b>: a file of the <i>Files</i> tab (nginx.conf…), on every server of the project.
        {' '}Removing a mount here does not delete the data.
      </p>
      {serverIds.length > 1 && keeps.length > 0 && (
        <p className="warn-text mini">This service runs on {serverNames.join(' and ')}: each server has its own copy of these files — its replicas do not see each other's, unless the volume is a NAS share.</p>
      )}

      <div className="toolbar form-actions flush">
        <button className="primary" type="button" disabled={busy || blocked || !changed || running} onClick={() => { void save(true) }}
          title={running ? 'a deploy is running' : 'save into the compose file and re-create the service with it'}>Save &amp; deploy {service.name}</button>
        <button type="button" disabled={busy || blocked || !changed} onClick={() => { void save(false) }} title="save into the compose file; the next deploy uses it">Save only</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
