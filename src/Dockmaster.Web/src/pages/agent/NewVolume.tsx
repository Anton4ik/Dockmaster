import { useState } from 'react'
import { api, errorText } from '../../lib/api'
import Modal from '../../components/Modal'

type Kind = 'local' | 'nfs' | 'cifs'

const KINDS: { kind: Kind; label: string; text: string }[] = [
  { kind: 'local', label: 'On this server', text: 'Docker keeps the data on this server\'s disk. The usual choice.' },
  { kind: 'nfs', label: 'NFS share', text: 'A folder another machine shares over NFS, e.g. a Synology NAS (Control Panel → Shared Folder → NFS Permissions). Every server that mounts it sees the same files.' },
  { kind: 'cifs', label: 'Windows / SMB share', text: 'A shared folder of Windows, a NAS or Samba. Every server that mounts it sees the same files.' },
]

/**
 * A new volume: on this server, or a shared folder of another machine (NFS / SMB). A share is mounted once right away,
 * so a wrong address or password shows here and not when an app starts.
 */
export default function NewVolume({ agentId, onCreated, onClose }: { agentId: number; onCreated: (name: string, message: string) => void; onClose: () => void }) {
  const [kind, setKind] = useState<Kind>('local')
  const [name, setName] = useState('')
  const [address, setAddress] = useState('')
  const [path, setPath] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [version, setVersion] = useState('')
  const [extra, setExtra] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function create() {
    setBusy(true); setError(null)
    try {
      const r = await api<{ message: string }>(`/agents/${agentId}/volumes`, {
        method: 'POST', json: { name: name.trim(), kind, address, path, username, password, version, extra },
      })
      onCreated(name.trim(), r.message)
    } catch (e) { setError(errorText(e)) }
    finally { setBusy(false) }
  }

  const share = kind !== 'local'
  return (
    <Modal label="New volume" onClose={onClose} onSubmit={() => { void create() }} autoComplete="off">
      <label htmlFor="v-name">Name</label>
      <input id="v-name" autoFocus value={name} onChange={e => setName(e.target.value)} placeholder="e.g. app-data" />
      <div className="mini">letters, digits, <code>-</code>, <code>_</code> and <code>.</code> — a service picks it by this name</div>

      <div className="field-label">Where is the data?</div>
      <div className="seg" role="group" aria-label="where the data is">
        {KINDS.map(k => <button key={k.kind} type="button" className={k.kind === kind ? 'on' : undefined} aria-pressed={k.kind === kind} onClick={() => setKind(k.kind)}>{k.label}</button>)}
      </div>
      <p className="mini">{KINDS.find(k => k.kind === kind)!.text}</p>

      {share && (
        <>
          <div className="row">
            <div>
              <label htmlFor="v-addr">{kind === 'nfs' ? 'NFS server' : 'Server'}</label>
              <input id="v-addr" value={address} onChange={e => setAddress(e.target.value)} placeholder="192.168.1.10" />
            </div>
            <div>
              <label htmlFor="v-path">{kind === 'nfs' ? 'Exported folder' : 'Shared folder'}</label>
              <input id="v-path" value={path} onChange={e => setPath(e.target.value)} placeholder={kind === 'nfs' ? '/volume1/backup' : 'backup'} />
            </div>
          </div>
          {kind === 'cifs' && (
            <div className="row">
              <div>
                <label htmlFor="v-user">User</label>
                <input id="v-user" value={username} onChange={e => setUsername(e.target.value)} placeholder="empty: guest" autoComplete="off" />
              </div>
              <div>
                <label htmlFor="v-pass">Password</label>
                <input id="v-pass" type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password" />
              </div>
            </div>
          )}
          <details>
            <summary className="mini">more options</summary>
            <div className="row">
              <div className="narrow">
                <label htmlFor="v-ver">{kind === 'nfs' ? 'NFS version' : 'SMB version'}</label>
                <select id="v-ver" value={version} onChange={e => setVersion(e.target.value)}>
                  {kind === 'nfs'
                    ? <><option value="">whatever the server offers</option><option value="4">4</option><option value="3">3</option></>
                    : <><option value="">3.0 (default)</option><option value="3.1.1">3.1.1</option><option value="2.1">2.1</option><option value="1.0">1.0 (old devices)</option></>}
                </select>
              </div>
              <div>
                <label htmlFor="v-extra">Mount options</label>
                <input id="v-extra" value={extra} onChange={e => setExtra(e.target.value)} placeholder={kind === 'nfs' ? 'e.g. nolock,soft' : 'e.g. uid=1000,gid=1000'} />
              </div>
            </div>
          </details>
          <p className="mini">It is mounted once now to check the address{kind === 'cifs' ? ' and the password' : ''}. The server keeps these settings in the volume{kind === 'cifs' ? ', password included (docker volume inspect shows it)' : ''}.</p>
        </>
      )}

      {error && <p className="error">{error}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy || !name.trim()}>{busy ? (share ? 'checking the share…' : 'creating…') : 'Create'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
