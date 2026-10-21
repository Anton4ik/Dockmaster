import { useState } from 'react'
import Modal from '../../components/Modal'

/**
 * Deleting the project: opened from the toolbar's "⋯" menu and done only after its name is typed, so a slip of the
 * mouse cannot do it. Its containers are taken down first unless unticked — otherwise they would keep running with
 * nothing left here to manage them.
 */
export default function DeleteProject({ name, containers, onDelete, onClose }: {
  name: string
  /** its containers on the servers now, running or not */
  containers: number
  onDelete: (down: boolean) => Promise<void>
  onClose: () => void
}) {
  const [typed, setTyped] = useState('')
  const [down, setDown] = useState(true)
  const [busy, setBusy] = useState(false)
  const matches = typed.trim() === name
  const n = `${containers} container${containers === 1 ? '' : 's'}`

  return (
    <Modal label={`Delete ${name}?`} onClose={onClose} autoComplete="off" onSubmit={() => {
      if (!matches || busy) return
      setBusy(true)
      void onDelete(containers > 0 && down).finally(() => setBusy(false))
    }}>
      <p>Dockmaster forgets the project: its compose file, .env, files, secrets and deploy history. This cannot be undone.</p>
      {containers > 0 && (
        <label className="check">
          <input type="checkbox" checked={down} onChange={e => setDown(e.target.checked)} />
          <span>stop and remove its {n} first (down){!down && <span className="warn-text"> — they keep running on the server</span>}</span>
        </label>
      )}
      <p className="mini">Volumes and their data stay on the server either way.</p>
      <label htmlFor="del-name">Type <b>{name}</b> to confirm</label>
      <input id="del-name" autoFocus value={typed} onChange={e => setTyped(e.target.value)} placeholder={name} spellCheck={false} />
      <div className="toolbar form-actions flush">
        <button type="submit" className="danger" disabled={!matches || busy}>{busy ? 'deleting…' : 'Delete project'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
