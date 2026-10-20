import { useEffect, useState } from 'react'
import { api, errorText, type Deployment } from '../../lib/api'
import { ago } from '../../lib/fmt'

/**
 * Under the project's title, whatever tab is open:
 * - a run going on that this page does not show (started before it was opened, or from elsewhere): "watch" picks
 *   up its log live;
 * - the last deploy, when it failed and nothing succeeded after it (a later stop or deploy makes it history): what
 *   went wrong and its log — the dashboard links here, and the history tab is one click too far to notice it.
 * Loaded again when `version` changes (a run ended), and every few seconds while a run goes on.
 */
export default function RunNotice({ projectId, version, watching, onLog, onFollow, onError }: {
  projectId: number
  version: number
  /** this page runs or follows one itself: no notice for it */
  watching: boolean
  onLog: (text: string) => void
  onFollow: (deploymentId: number) => void
  onError: (message: string) => void
}) {
  const [running, setRunning] = useState<Deployment | null>(null)
  const [failed, setFailed] = useState<Deployment | null>(null)
  useEffect(() => {
    let stop = false
    let timer: number | undefined
    const load = () => api<Deployment[]>(`/projects/${projectId}/deployments`)
      .then(all => {
        if (stop) return
        const current = all.find(d => d.ok === null) ?? null
        setRunning(current)
        // newest first: the first success or deploy decides
        const decisive = all.find(d => d.ok === true || (d.ok === false && d.action.startsWith('deploy')))
        setFailed(decisive?.ok === false ? decisive : null)
        if (current) timer = window.setTimeout(load, 5000)
      })
      .catch(() => {})
    void load()
    return () => { stop = true; window.clearTimeout(timer) }
  }, [projectId, version])

  if (running && !watching) return (
    <div className="run-notice" role="status">
      <span><i className="dot warn" /><b>{running.action}</b> <span className="muted">running since {ago(running.startedAt)}</span></span>
      <button className="sm" onClick={() => onFollow(running.id)} title="its log so far, then live">watch</button>
    </div>
  )
  if (!failed || running) return null
  return (
    <div className="deploy-failed" role="status">
      <span><b>Last deploy failed</b> <span className="muted">{ago(failed.finishedAt ?? failed.startedAt)} · {failed.action}</span></span>
      <span className="deploy-failed-message">{failed.message}</span>
      <button className="sm" onClick={() => api<string>(`/projects/${projectId}/deployments/${failed.id}/log`).then(onLog).catch(e => onError(errorText(e)))}>log</button>
    </div>
  )
}
