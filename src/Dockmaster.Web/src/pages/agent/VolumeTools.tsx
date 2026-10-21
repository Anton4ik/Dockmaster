import { useCallback, useEffect, useRef, useState } from 'react'
import { api, errorText, type Agent, type Volume } from '../../lib/api'
import { bytes } from '../../lib/fmt'
import { withToast } from '../../lib/toast'
import Modal from '../../components/Modal'

interface Entry { name: string; dir: boolean; size: number; modified: number; link: boolean }

const exportUrl = (agentId: number, volume: string, path: string, file: boolean) =>
  `/api/agents/${agentId}/volumes/${encodeURIComponent(volume)}/export?path=${encodeURIComponent(path)}${file ? '&file=true' : ''}`

/** The files in a volume, folder by folder; a file or a folder downloads with one click. */
export function BrowseVolume({ agentId, volume, onClose }: { agentId: number; volume: string; onClose: () => void }) {
  const [path, setPath] = useState('')
  // what was read, for which folder: another folder shows "Reading…" until its answer is there
  const [read, setRead] = useState<{ path: string; entries?: Entry[]; error?: string } | null>(null)

  const load = useCallback((p: string) => {
    api<Entry[]>(`/agents/${agentId}/volumes/${encodeURIComponent(volume)}/browse?path=${encodeURIComponent(p)}`)
      .then(entries => setRead({ path: p, entries }), e => setRead({ path: p, error: errorText(e) }))
  }, [agentId, volume])
  useEffect(() => load(path), [load, path])
  const entries = read?.path === path ? read.entries ?? null : null
  const error = read?.path === path ? read.error ?? null : null

  const parts = path.split('/').filter(Boolean)
  const into = (name: string) => setPath([...parts, name].join('/'))
  return (
    <Modal label={`Files in ${volume}`} wide onClose={onClose} onSubmit={onClose}>
      <div className="crumbs">
        <button type="button" className="link" onClick={() => setPath('')}>{volume}</button>
        {parts.map((p, i) => <span key={i}> / <button type="button" className="link" onClick={() => setPath(parts.slice(0, i + 1).join('/'))}>{p}</button></span>)}
        <div className="spacer" />
        <a className="btn sm" href={exportUrl(agentId, volume, path, false)} download title="this folder as a tar.gz">download this folder</a>
      </div>
      {error && <p className="error">{error}</p>}
      {entries === null ? (!error && <p className="muted">Reading…</p>) : (
        <div className="table-wrap files-browser"><table>
          <tbody>
            {parts.length > 0 && <tr><td colSpan={3}><button type="button" className="link" onClick={() => setPath(parts.slice(0, -1).join('/'))}>..</button></td></tr>}
            {entries.map(e => (
              <tr key={e.name}>
                <td>{e.dir
                  ? <button type="button" className="link" onClick={() => into(e.name)}>📁 {e.name}</button>
                  : <a href={exportUrl(agentId, volume, [...parts, e.name].join('/'), true)} download title="download this file">{e.name}</a>}
                  {e.link && <span className="mini"> (link)</span>}</td>
                <td className="num muted">{e.dir ? '' : bytes(e.size)}</td>
                <td className="muted nowrap hide-sm">{new Date(e.modified * 1000).toLocaleString()}</td>
              </tr>
            ))}
            {entries.length === 0 && <tr><td className="muted" colSpan={3}>empty</td></tr>}
          </tbody>
        </table></div>
      )}
      <p className="mini">Read-only: a helper container mounts the volume to look inside. To change files, use the container's Console, or upload a tar.gz.</p>
    </Modal>
  )
}

/** A tar.gz from the computer unpacked into the volume: added to what is there, or instead of it. */
export function UploadVolume({ agentId, volume, onClose, onDone }: { agentId: number; volume: string; onClose: () => void; onDone: () => void }) {
  const file = useRef<HTMLInputElement>(null)
  const [wipe, setWipe] = useState(false)
  const [busy, setBusy] = useState(false)
  async function upload() {
    const f = file.current?.files?.[0]
    if (!f) return
    if (wipe && !confirm(`Replace what is in ${volume}? Everything in it now is deleted once the archive is unpacked.`)) return
    setBusy(true)
    const ok = await withToast(`uploading ${f.name} (${bytes(f.size)}) into ${volume}…`,
      () => api<{ message: string }>(`/agents/${agentId}/volumes/${encodeURIComponent(volume)}/import${wipe ? '?wipe=true' : ''}`, { method: 'POST', body: f, headers: { 'Content-Type': 'application/gzip' } }),
      r => `${volume}: ${r.message}`)
    setBusy(false)
    if (ok) { onDone(); onClose() }
  }
  return (
    <Modal label={`Upload into ${volume}`} onClose={onClose} onSubmit={() => { void upload() }}>
      <p className="mini">A <code>.tar.gz</code> — the download of a volume from this page, or <code>tar -czf data.tar.gz -C folder .</code> — is unpacked into the volume. Stop the containers that use it first: a volume a running container uses is refused. A broken archive changes nothing.</p>
      <input ref={file} type="file" accept=".tar.gz,.tgz,application/gzip" />
      <label className="check"><input type="checkbox" checked={wipe} onChange={e => setWipe(e.target.checked)} /> replace what is in the volume (otherwise files are added, same names replaced)</label>
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy}>{busy ? 'uploading…' : 'Upload'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}

/** Copies a volume to another server (or to another name here): packed there, sent through the Dockmaster server, unpacked. */
export function CopyVolume({ agentId, volume, agents, onClose }: { agentId: number; volume: Volume; agents: Agent[]; onClose: () => void }) {
  const others = agents.filter(a => a.online)
  const [to, setTo] = useState(others.find(a => a.id !== agentId)?.id ?? agentId)
  const [name, setName] = useState(volume.name)
  const [wipe, setWipe] = useState(true)
  const [busy, setBusy] = useState(false)
  const writers = volume.containers.filter(c => c.running && c.rw)
  async function copy() {
    setBusy(true)
    const target = agents.find(a => a.id === to)?.name ?? '?'
    const ok = await withToast(`copying ${volume.name} to ${target}…`,
      () => api<{ message: string }>(`/agents/${agentId}/volumes/${encodeURIComponent(volume.name)}/copy`, { method: 'POST', json: { toAgentId: to, toName: name.trim(), wipe } }),
      r => r.message)
    setBusy(false)
    if (ok) onClose()
  }
  return (
    <Modal label={`Copy ${volume.name}`} onClose={onClose} onSubmit={() => { void copy() }}>
      <p className="mini">For a service that moves to another server: its data goes along. Deploy it there after the copy.</p>
      {writers.length > 0 && <p className="warn-text mini">{writers.map(c => c.name).join(', ')} write{writers.length === 1 ? 's' : ''} to it now: stop {writers.length === 1 ? 'it' : 'them'} first (a database copied while it runs may not open).</p>}
      <div className="row">
        <div>
          <label htmlFor="cv-to">To server</label>
          <select id="cv-to" value={to} onChange={e => setTo(Number(e.target.value))}>
            {others.map(a => <option key={a.id} value={a.id}>{a.name}{a.id === agentId ? ' (this one)' : ''}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor="cv-name">Volume there</label>
          <input id="cv-name" value={name} onChange={e => setName(e.target.value)} />
        </div>
      </div>
      <div className="mini">A project's volume keeps the name docker gave it (<code>{volume.name}</code>): the service finds its data under the same name on the other server.</div>
      <label className="check"><input type="checkbox" checked={wipe} onChange={e => setWipe(e.target.checked)} /> replace what is in it there (a volume a running container uses there is refused)</label>
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy || !name.trim() || (to === agentId && name.trim() === volume.name)}>{busy ? 'copying…' : 'Copy'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
