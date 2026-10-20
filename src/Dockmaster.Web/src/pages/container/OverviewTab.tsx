import { useState, type FormEvent, type ReactNode } from 'react'
import { api, type ContainerDetails, type ContainerExit } from '../../lib/api'
import { useCan } from '../../lib/access'
import { agoShort, bytes, exitReason, restartPolicyText, restartVerdict, when } from '../../lib/fmt'
import { toast, withToast } from '../../lib/toast'
import EventList from '../../components/EventList'
import KeyValues from './KeyValues'

const row = (label: string, value: ReactNode): [string, ReactNode] => [label, value]
const code = (s: string | null | undefined) => s ? <code>{s}</code> : null
// Docker reports "never" as the zero date
const time = (iso: string) => iso && !iso.startsWith('0001') ? when(iso) : null

/** State, health, how it was started and its resources, then the last health checks and why it restarted or stopped. */
export default function OverviewTab({ d, agentId, lastExit, restarts, restartsRecent, onChanged }: {
  d: ContainerDetails; agentId: number
  /** from the heartbeats: the details are read once, these stay current */
  lastExit?: ContainerExit | null; restarts?: number
  /** false: the restarts are old (the container runs fine since), shown grey */
  restartsRecent?: boolean
  onChanged: () => void
}) {
  const s = d.state
  const h = s.health
  return (
    <>
      <KeyValues rows={[
        row('Status', <>{s.status}{s.oomKilled && <span className="error"> · killed: out of memory</span>}{s.error && <span className="error"> · {s.error}</span>}</>),
        row('Started', time(s.startedAt)),
        row('Finished', !s.running ? time(s.finishedAt) : null),
        row('Exit code', !s.running ? <span className={s.exitCode ? 'error' : ''}>{s.exitCode}</span> : null),
        row('Restarts', <>{(restarts ?? s.restartCount) ? <span className={restartsRecent === false ? '' : 'error'}>{restarts ?? s.restartCount}</span> : '0'}{lastExit && <LastExit exit={lastExit} />}</>),
        row('Health', h && (s.running
          ? <>{h.status}{h.failingStreak > 0 && <span className="error"> · {h.failingStreak} failed in a row</span>}</>
          : <span className="muted">not running · the last check before it stopped said {h.status}</span>)),
        row('Health check', d.healthcheck && <>{code(d.healthcheck.test.slice(d.healthcheck.test[0]?.startsWith('CMD') ? 1 : 0).join(' '))} <span className="muted">every {d.healthcheck.interval}s, timeout {d.healthcheck.timeout}s, {d.healthcheck.retries} retries</span></>),
        row('Created', time(d.created)),
        row('Image', <span title={d.imageId}>{d.image} <span className="muted">{d.imageId.replace('sha256:', '').slice(0, 12)}</span></span>),
        row('Command', code(d.command)),
        row('Working dir', code(d.workingDir)),
        row('User', code(d.user)),
        row('Hostname', code(d.hostname)),
        row('Restart policy', d.restartPolicy && <RestartPolicy d={d} agentId={agentId} onChanged={onChanged} />),
        row('Memory limit', d.memoryLimit ? bytes(d.memoryLimit) : 'none'),
        row('Memory reservation', d.memoryReservation ? bytes(d.memoryReservation) : null),
        row('CPU limit', d.cpus ? `${d.cpus} cores` : 'none'),
        row('CPU shares', d.cpuShares || null),
        row('Log driver', d.logDriver),
        row('Platform', d.platform),
        row('Privileged', d.privileged ? <span className="error">yes</span> : null),
      ]} />
      <h2>Restarts and stops</h2>
      <EventList agentId={agentId} container={d.name.replace(/^\//, '')} compact limit={20} more
        empty="None recorded: when it restarts or stops by itself, why it did and its last log lines show here." />
      {h && h.log.length > 0 && (
        <>
          <h2>Last health checks</h2>
          <div className="table-wrap"><table>
            <thead><tr><th>When</th><th>Exit</th><th>Output</th></tr></thead>
            <tbody>
              {[...h.log].reverse().map(l => (
                <tr key={l.start}>
                  <td className="nowrap muted">{when(l.start)}</td>
                  <td className={l.exitCode ? 'error' : 'success'}>{l.exitCode}</td>
                  <td><pre className="inline-out">{l.output || '—'}</pre></td>
                </tr>
              ))}
            </tbody>
          </table></div>
        </>
      )}
    </>
  )
}

/** How its last run ended: docker forgets the exit code once the container runs again, the agent keeps it. */
function LastExit({ exit }: { exit: ContainerExit }) {
  const iso = new Date(exit.unixMs).toISOString()
  const bad = exit.oom || ![0, 143].includes(exit.exitCode)
  const ago = agoShort(iso)
  return <span className="muted"> · last ended {ago === 'now' ? 'just now' : ago + ' ago'}: <span className={bad ? 'error' : ''} title={when(iso)}>{exitReason(exit.exitCode, exit.oom)}</span></span>
}

const RESTART_POLICIES = ['no', 'always', 'unless-stopped', 'on-failure']

/**
 * The policy, what it means, and whether Docker brings the container back in the state it is in now. "change" swaps
 * in a picker applied live (docker update --restart); a container of a Dockmaster project also gets it in the compose file.
 */
function RestartPolicy({ d, agentId, onChanged }: { d: ContainerDetails; agentId: number; onChanged: () => void }) {
  const max = d.maxRetries ?? 0
  const [edit, setEdit] = useState<{ policy: string; max: string } | null>(null)
  const [busy, setBusy] = useState(false)
  // written into the project's compose file too: editing projects on the server
  const editable = useCan()(agentId, 'edit')
  const verdict = restartVerdict({ state: d.state.status, restartPolicy: d.restartPolicy ?? '', restartMax: max, exitCode: d.state.exitCode, restartCount: d.state.restartCount })

  async function apply(e: FormEvent) {
    e.preventDefault()
    if (!edit) return
    const retries = edit.policy === 'on-failure' && edit.max.trim() ? Number(edit.max) : 0
    if (!Number.isInteger(retries) || retries < 0) { toast('retries: a whole number, 0 = no limit', 'error'); return }
    if (edit.policy === d.restartPolicy && retries === max) { setEdit(null); return }
    setBusy(true)
    const ok = await withToast(`${d.name}: restart policy ${edit.policy}…`,
      () => api<{ message: string }>(`/agents/${agentId}/containers/${encodeURIComponent(d.name)}/restart-policy`, { method: 'POST', json: { policy: edit.policy, maxRetries: retries } }),
      r => `${d.name}: ${r.message}`, message => `${d.name}: ${message}`)
    setBusy(false)
    if (ok) { setEdit(null); onChanged() }
  }

  if (edit) return (
    <form className="filters restart-form" onSubmit={e => { void apply(e) }}>
      <select className="cell" aria-label="restart policy" autoFocus value={edit.policy} onChange={e => setEdit({ ...edit, policy: e.target.value })}
        onKeyDown={e => { if (e.key === 'Escape') setEdit(null) }}>
        {RESTART_POLICIES.map(p => <option key={p} value={p}>{p}</option>)}
      </select>
      {edit.policy === 'on-failure' && (
        <input className="cell retries" aria-label="maximum restarts" placeholder="no limit" inputMode="numeric" value={edit.max}
          title="at most this many restarts; empty or 0 = no limit" onChange={e => setEdit({ ...edit, max: e.target.value })} />
      )}
      <button className="sm primary" type="submit" disabled={busy} title="docker update --restart: applied now, the container keeps running">apply</button>
      <button className="sm" type="button" disabled={busy} onClick={() => setEdit(null)}>cancel</button>
      <div className="mini muted restart-hint">{restartPolicyText(edit.policy, Number(edit.max) || 0)}</div>
    </form>
  )

  return (
    <>
      <b>{d.restartPolicy}</b>{d.restartPolicy === 'on-failure' && max > 0 && ` (max ${max})`}
      <span className="muted"> — {restartPolicyText(d.restartPolicy ?? '', max)}</span>
      {editable && <>{' '}<button className="link" onClick={() => setEdit({ policy: d.restartPolicy ?? 'no', max: max > 0 ? String(max) : '' })} title="change the restart policy, live">change</button></>}
      {verdict && <div className={'verdict ' + verdict.cls}><i className={'dot ' + verdict.cls} />now: {verdict.text}</div>}
    </>
  )
}
