import { useCallback, useEffect, useRef, useState } from 'react'
import { api, errorText } from '../lib/api'
import { bytes, when } from '../lib/fmt'
import { toast, withToast } from '../lib/toast'

interface BackupFile { name: string; size: number; at: string }

/**
 * Backups of Dockmaster itself: projects, secrets, history, the agents' certificate and the keys that decrypt the
 * secrets. One a day is kept on the server (the last 7); a download goes somewhere else — the NAS's own backup, a
 * laptop. Restoring puts a backup in place and restarts the server.
 */
export default function SettingsBackup() {
  const [list, setList] = useState<BackupFile[] | null>(null)
  const [busy, setBusy] = useState(false)
  const file = useRef<HTMLInputElement>(null)

  const load = useCallback(() => api<BackupFile[]>('/system/backups').then(l => setList(l), () => setList([])), [])
  useEffect(() => { void load() }, [load])

  async function makeNow() {
    setBusy(true)
    if (await withToast('making a backup…', () => api<BackupFile>('/system/backups', { method: 'POST' }), b => `backup ${b.name} made (${bytes(b.size)})`)) await load()
    setBusy(false)
  }

  async function restore(f: File) {
    if (!confirm(`Restore ${f.name}?\n\nProjects, secrets, registries, servers and the history become what they were in the backup — what changed since is gone (the data before is kept in the server's data folder as before-restore-…). The server restarts; it must run with restart: always.`)) return
    setBusy(true)
    try {
      const r = await api<{ message: string }>('/system/restore', { method: 'POST', body: f, headers: { 'Content-Type': 'application/zip' } })
      toast(r.message, 'busy')
      // back when the restarted server answers
      const started = Date.now()
      const wait = async () => {
        try { await api('/auth/me'); if (Date.now() - started > 4000) { window.location.reload(); return } } catch { /* restarting */ }
        setTimeout(() => { void wait() }, 2000)
      }
      setTimeout(() => { void wait() }, 3000)
    } catch (e) { toast(errorText(e), 'error'); setBusy(false) }
    if (file.current) file.current.value = ''
  }

  return (
    <div className="card form spaced">
      <h2>Backup</h2>
      <p className="muted">Everything Dockmaster knows: projects with their compose files, .env, files and secrets, registries, servers, the deploy history — and the agents' certificate and the keys that decrypt the secrets. Containers and their volumes are not in it: back up the volumes on the servers.</p>
      <div className="toolbar">
        <a className="btn primary" href="/api/system/backup" download>Download a backup now</a>
        <button type="button" disabled={busy} onClick={() => { void makeNow() }} title="make one on the server now, next to the daily ones; it counts among the last 7 kept">make one on the server</button>
        <button type="button" disabled={busy} onClick={() => file.current?.click()}>restore from a file…</button>
        <input ref={file} type="file" accept=".zip,application/zip" hidden onChange={e => { const f = e.target.files?.[0]; if (f) void restore(f) }} />
      </div>
      <div className="field-label">Backups on the server, daily and made here (the last 7, in its data folder under backups/)</div>
      {list === null ? <p className="muted">…</p> : list.length === 0 ? <p className="mini">None yet — the first is made a few minutes after the server starts.</p> : (
        <div className="kv-list">
          {list.map(b => (
            <div key={b.name} className="kv"><span>{when(b.at)}</span><span>{bytes(b.size)} · <a href={`/api/system/backups/${encodeURIComponent(b.name)}`} download>download</a></span></div>
          ))}
        </div>
      )}
      <p className="mini">A copy on the same disk helps after a bad update or a mistake, not when the disk dies — download one now and then, or let the NAS back up the server's data volume.</p>
    </div>
  )
}
