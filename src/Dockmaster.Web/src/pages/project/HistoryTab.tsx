import { useEffect, useState } from 'react'
import { api, errorText, type Deployment, type DeploymentFiles } from '../../lib/api'
import { when } from '../../lib/fmt'
import { usePhone } from '../../lib/hooks'
import DiffView from '../../components/DiffView'
import Modal from '../../components/Modal'

const took = (d: Deployment) => d.finishedAt ? ` (${Math.round((new Date(d.finishedAt).getTime() - new Date(d.startedAt).getTime()) / 1000)}s)` : ''

/**
 * Past deployments with who started them and their outcome; "log" opens one in the drawer. A deploy keeps the compose file and .env it put
 * out: "changes" shows what changed since, and from there the project goes back to it. Loaded again when `version`
 * changes (a deploy ended).
 */
export default function HistoryTab({ projectId, version, running, onLog, onFollow, onRollback, onError }: {
  projectId: number
  version: number
  running: boolean
  onLog: (text: string) => void
  /** a run still going on: its log live */
  onFollow: (deploymentId: number) => void
  /** put a deploy's files back and deploy them */
  /** missing: the user may not deploy, going back is not offered */
  onRollback?: (deploymentId: number) => void
  onError: (message: string) => void
}) {
  // null until loaded: no "no deployments yet" flash while it loads
  const [history, setHistory] = useState<Deployment[] | null>(null)
  const [open, setOpen] = useState<Deployment | null>(null)
  const phone = usePhone()
  useEffect(() => { api<Deployment[]>(`/projects/${projectId}/deployments`).then(setHistory).catch(() => {}) }, [projectId, version])
  // the newest deploy that worked: what runs now, as far as the files go (a rollback saves the files it replaced, never deployed)
  const current = history?.find(d => d.hasFiles && d.ok && d.action.startsWith('deploy'))

  return (
    <>
      <div className="table-wrap"><table>
        <thead><tr><th className="hide-sm">When</th><th>Action</th><th className="hide-sm" title="the signed-in user, a webhook or an API key">By</th><th>Result</th><th></th></tr></thead>
        <tbody>
          {history?.map(d => (
            <tr key={d.id}>
              <td className="muted nowrap hide-sm">{when(d.startedAt)}{took(d)}</td>
              <td>
                {d.action}{d === current && <span className="badge ok source" title="the files of the last deploy that worked">running now</span>}
                {phone && <div className="mini">{when(d.startedAt)}{took(d)}{d.source && <> · <Source source={d.source} /></>}</div>}
              </td>
              <td className="hide-sm">{d.source && <Source source={d.source} />}</td>
              <td>{d.ok === true ? <span className="badge ok">ok</span> : d.ok === false ? <span className="badge bad">failed</span> : <span className="badge warn">running</span>} <span className="muted">{d.message}</span></td>
              <td className="menu-cell nowrap">
                {d.hasFiles && d.ok !== null && <button className="sm" onClick={() => setOpen(d)} title="what changed in the compose file and .env since this deploy; go back to it">changes</button>}{' '}
                {d.ok === null
                  ? <button className="sm" onClick={() => onFollow(d.id)} title="its log so far, then live">watch</button>
                  : <button className="sm" onClick={() => api<string>(`/projects/${projectId}/deployments/${d.id}/log`).then(onLog).catch(e => onError(errorText(e)))}>log</button>}
              </td>
            </tr>
          ))}
          {history?.length === 0 && <tr><td colSpan={phone ? 3 : 5} className="muted">no deployments yet</td></tr>}
        </tbody>
      </table></div>
      {open && <Changes projectId={projectId} d={open} running={running} onClose={() => setOpen(null)}
        onRollback={onRollback && (() => { setOpen(null); onRollback(open.id) })} onError={onError} />}
    </>
  )
}

/** Who started a run: "anna", "anna: rollback to #12" (the user in bold), "webhook". */
function Source({ source }: { source: string }) {
  const at = source.indexOf(': ')
  return at > 0
    ? <span className="run-source"><b>{source.slice(0, at)}</b><span className="muted">: {source.slice(at + 2)}</span></span>
    : <span className="run-source">{source}</span>
}

/** A deploy's files next to the project's files now, and going back to them. */
function Changes({ projectId, d, running, onClose, onRollback, onError }: {
  projectId: number; d: Deployment; running: boolean; onClose: () => void; onRollback?: () => void; onError: (message: string) => void
}) {
  const [files, setFiles] = useState<DeploymentFiles | null>(null)
  // once per deploy shown (the handlers are new on every render of the page)
  useEffect(() => {
    api<DeploymentFiles>(`/projects/${projectId}/deployments/${d.id}/files`).then(setFiles).catch(e => { onError(errorText(e)); onClose() })
  }, [projectId, d.id]) // eslint-disable-line react-hooks/exhaustive-deps
  const same = files && files.composeYaml === files.currentComposeYaml && files.envFile === files.currentEnvFile

  function rollback() {
    if (!confirm(`Go back to the files of ${when(d.startedAt)}?\n\nThe compose file and .env of that deploy replace the ones now (what changed since — green on this page — is gone), then the project is pulled and deployed. Secrets stay as they are now.`)) return
    onRollback?.()
  }

  return (
    <Modal label={`Deploy of ${when(d.startedAt)} → now`} wide onClose={onClose} onSubmit={rollback}>
      {!files ? <p className="muted">Loading…</p> : (
        <>
          <p className="mini">Red lines were in that deploy and are not any more, green ones came since.</p>
          <div className="field-label">docker-compose.yml</div>
          <DiffView before={files.composeYaml} after={files.currentComposeYaml} same="the same as now" />
          <div className="field-label">.env</div>
          {files.envHidden ? <p className="muted mini">hidden: seeing the .env takes the Secrets permission</p>
            : <DiffView before={files.envFile} after={files.currentEnvFile} same="the same as now" />}
        </>
      )}
      <div className="toolbar form-actions flush">
        {onRollback && <button className="primary" type="submit" disabled={!files || !!same || running || files.envHidden}
          title={files?.envHidden ? 'going back puts the .env back too: it takes the Secrets permission' : same ? 'the files are the same as now' : running ? 'a deploy is running' : 'put these files back and pull & deploy'}>Go back to this version</button>}
        <button type="button" onClick={onClose}>Close</button>
        {d.ok === false && <span className="mini warn-text">this deploy failed</span>}
      </div>
    </Modal>
  )
}
