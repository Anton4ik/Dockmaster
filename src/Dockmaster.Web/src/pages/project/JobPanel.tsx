import { useState } from 'react'
import { Link } from 'react-router-dom'
import type { JobRun, JobStatus, ServiceSummary } from '../../lib/api'
import { agoShort, duration, triggerText, untilShort, when } from '../../lib/fmt'
import Modal from '../../components/Modal'

/** "took 3 min" for a finished run, "running 2 min" for one going on. */
export function runLength(r: { startedAt: string; finishedAt: string | null }) {
  const end = r.finishedAt ? new Date(r.finishedAt).getTime() : Date.now()
  return duration(Math.max(0, (end - new Date(r.startedAt).getTime()) / 1000))
}

/** The line a table shows for a job: its schedule in words and when it runs next (or that it runs now). */
export function jobLine(status?: JobStatus): { text: string; title: string } {
  if (!status) return { text: 'job', title: 'a job on a schedule' }
  const tz = status.timeZone ? ` (${status.timeZone})` : ' (UTC)'
  if (status.state === 'running') return { text: `${status.description} · running ${status.runningSince ? runLength({ startedAt: status.runningSince, finishedAt: null }) : ''}`, title: `${status.cron}${tz}` }
  if (status.state === 'undeployed') return { text: `${status.description} · deploy first`, title: `${status.cron}${tz} — no container on the server yet: deploy the project` }
  if (status.state === 'never') return { text: `${status.description} · never`, title: `${status.cron}${tz} names no time` }
  return { text: `${status.description} · next ${untilShort(status.nextAt)}`, title: `${status.cron}${tz}\nnext run: ${status.nextAt ? when(status.nextAt) : '—'}` }
}

/**
 * A job's schedule and its last runs: when each started, how long it took, how it ended (with the last log lines of a
 * failed one), who started it. Run now, stop a run going on, or change the schedule from here.
 */
export default function JobPanel({ service, status, containerLink, running, onRunNow, onStop, onEdit, onClose }: {
  service: ServiceSummary
  status?: JobStatus
  /** the container page of the job's container, for its full logs */
  containerLink?: string
  /** a deploy or another action of the project is going on */
  running: boolean
  onRunNow: () => Promise<boolean>
  onStop: () => void
  onEdit: () => void
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState<number | null>(null)
  const s = service.schedule
  const runs = status?.runs ?? []
  const state = status?.state
  const fmtIn = (iso: string | null) => iso ? `${untilShort(iso)} — ${when(iso)}` : '—'
  async function runNow() {
    setBusy(true)
    try { await onRunNow() } finally { setBusy(false) }
  }
  const result = (r: JobRun) => r.finishedAt === null
    ? <span className="badge warn">running</span>
    : r.ok === null ? <span className="badge muted" title={r.note}>cut short</span>
    : r.ok ? <span className="badge ok">ok</span> : <span className="badge bad" title={r.exitCode !== null ? `exit code ${r.exitCode}` : undefined}>failed{r.exitCode ? ` (${r.exitCode})` : ''}</span>
  return (
    <Modal label={`Job ${service.name}`} wide onClose={onClose} onSubmit={onClose}>
      <div className="kv-list">
        <div className="kv"><span>Schedule</span><span>{status?.description ?? s?.cron}<span className="muted"> · <code>{s?.cron}</code> · {s?.timeZone ?? 'UTC'}</span></span></div>
        <div className="kv"><span>Next run</span><span>{state === 'running' ? <>running now{status?.runningSince && <span className="muted"> · since {agoShort(status.runningSince)} ({runLength({ startedAt: status.runningSince, finishedAt: null })})</span>}</>
          : state === 'undeployed' ? <span className="warn-text">no container on the server yet — deploy the project first</span>
          : state === 'never' ? <span className="warn-text">the expression names no time</span>
          : fmtIn(status?.nextAt ?? null)}</span></div>
        {s?.timeout && <div className="kv"><span>Timeout</span><span>a run still going after {s.timeout} is stopped</span></div>}
        {status?.zoneProblem && <div className="kv"><span /><span className="warn-text">{status.zoneProblem}</span></div>}
      </div>
      <div className="toolbar">
        {state === 'running'
          ? <button type="button" disabled={running} onClick={onStop} title="stops the container; the run counts as failed">stop the run</button>
          : <button type="button" className="primary" disabled={busy || running || state === 'undeployed'} onClick={() => { void runNow() }} title="starts it now, outside its schedule">run now</button>}
        <button type="button" disabled={running} onClick={onEdit}>change the schedule…</button>
        {containerLink && <Link className="sm-link" to={containerLink}>container page &amp; logs</Link>}
        <div className="spacer" />
        <span className="mini">A job starts on its schedule, does its work and exits. Docker does not restart it; the next start is Dockmaster's.</span>
      </div>
      <h3>Last runs</h3>
      {runs.length === 0
        ? <p className="muted">None yet{state === 'undeployed' ? ' — deploy the project, then it runs at its time (or run it now)' : ''}.</p>
        : <div className="table-wrap"><table className="job-runs">
          <thead><tr><th>Started</th><th>Took</th><th>Result</th><th className="hide-sm">Started by</th><th className="hide-sm">Note</th></tr></thead>
          <tbody>
            {runs.map(r => [
              <tr key={r.id} className={r.logTail ? 'foldable' : undefined} onClick={r.logTail ? () => setOpen(open === r.id ? null : r.id) : undefined} title={r.logTail ? 'show its last log lines' : undefined}>
                <td className="nowrap" title={when(r.startedAt)}>{agoShort(r.startedAt)}<span className="muted mini"> · {new Date(r.startedAt).toLocaleString()}</span></td>
                <td className="nowrap">{runLength(r)}</td>
                <td>{result(r)}{r.logTail && <span className="mini muted"> {open === r.id ? '▾' : '▸'} log</span>}</td>
                <td className="muted hide-sm">{triggerText(r.trigger)}</td>
                <td className="muted hide-sm">{r.note}</td>
              </tr>,
              open === r.id && r.logTail ? <tr key={r.id + 'log'} className="in-group"><td colSpan={5}><pre className="log-tail">{r.logTail}</pre></td></tr> : null,
            ])}
          </tbody>
        </table></div>}
      <div className="toolbar form-actions flush"><button type="submit">Close</button></div>
    </Modal>
  )
}
