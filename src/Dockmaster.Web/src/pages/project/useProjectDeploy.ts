import { useState } from 'react'
import { useStreamTask, type StreamResult } from '../../lib/live'
import { toast } from '../../lib/toast'

/**
 * A project's streamed actions (deploy, restart, stop…) and the log for the side drawer, which also shows old logs
 * from the history and the log of a run started before the page was opened. `finished` counts the runs that ended,
 * for what has to reload after one.
 */
export function useProjectDeploy() {
  const stream = useStreamTask()
  const [log, setLog] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<StreamResult | null>(null)
  const [finished, setFinished] = useState(0)
  // watching a run someone else started (a page left since): it cannot be cancelled from here
  const [following, setFollowing] = useState(false)

  /** Runs `method` for project `pid`; null when the page was left meanwhile. */
  async function start(pid: number, method: string, args: unknown[], follow = false) {
    setLog(''); setOutcome(null); setFollowing(follow)
    const r = await stream.run(method, [pid, ...args], line => setLog(l => (l ?? '') + line + '\n'))
    if (!r) return null
    setOutcome(r)
    // the drawer may have been closed meanwhile: the outcome shows in the corner as well
    toast(r.ok ? r.message || 'done' : r.message, r.ok ? 'ok' : 'error')
    setFinished(n => n + 1)
    return r
  }

  /** The log of a run going on (or just ended), from its start and then live: one started before this page opened. */
  const follow = (pid: number, deploymentId: number) => start(pid, 'FollowDeployment', [deploymentId], true)
  /** A finished deployment's log, from the history. */
  const showLog = (text: string) => { setOutcome(null); setFollowing(false); setLog(text) }
  /** closes a finished run's log (a running one is only hidden by its drawer) */
  const close = () => setLog(null)
  /** stops the running action on the server */
  const cancel = () => stream.cancel()

  return { log, outcome, running: stream.running, following, finished, start, follow, showLog, close, cancel }
}
