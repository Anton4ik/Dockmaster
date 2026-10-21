import { useRef, useState } from 'react'
import { agoShort, formatPorts, jobState, restartPolicyText, restartVerdict, stateInfo, when, type RestartState } from '../lib/fmt'
import type { JobInfo } from '../lib/api'
import { HIDDEN_UNTIL_PLACED, useDismiss, useFixedPopover } from '../lib/hooks'

/** "a, b, c +N": "+N" opens a small list above the table, so the row keeps its height; outside click or Esc closes it. */
function Collapsed({ items, max, className, sep = ', ' }: { items: string[]; max: number; className: string; sep?: string }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLSpanElement>(null)
  const button = useRef<HTMLButtonElement>(null)
  const popup = useRef<HTMLSpanElement>(null)
  useDismiss(ref, open, () => setOpen(false))
  // fixed to the viewport: the list usually sits in a truncated cell of a scrolling table, which would clip it
  useFixedPopover(open, () => setOpen(false), button, popup, 'left')
  return (
    <span className={className + ' overflow names-line'} ref={ref}>
      {/* only the names get the ellipsis in a narrow cell: "+N" stays visible and clickable */}
      <span className="names-text">{items.slice(0, max).join(sep)}</span>
      {items.length > max && <button ref={button} className="link more" onClick={e => { e.stopPropagation(); setOpen(o => !o) }}
        title={open ? 'hide' : 'show all'} aria-label={`${open ? 'hide' : 'show'} all ${items.length}`} aria-expanded={open}>+{items.length - max}</button>}
      {open && <span className="popover" ref={popup} style={HIDDEN_UNTIL_PLACED}>{items.map(i => <span key={i}>{i}</span>)}</span>}
    </span>
  )
}

/** A coloured dot with the state in the tooltip; with `label` the one word is shown too ("healthy" implies running). */
export function StateDot({ state, health, extra, label }: { state: string; health: string; extra?: string; label?: boolean }) {
  const s = stateInfo(state, health)
  return <span className="state" title={s.text}><i className={'dot ' + s.cls} />{label ? s.text : null}{extra ? <span className="muted"> {extra}</span> : null}</span>
}

/**
 * A few words next to a container when it is not simply running and safe (stopped by hand, crashed for good, a crash
 * loop, no restart policy); nothing otherwise, so a crowded table stays quiet. The tooltip explains the policy.
 */
export function RestartMark({ c }: { c: RestartState & { startedUnix?: number; jobInfo?: JobInfo | null } }) {
  if (c.jobInfo) return <JobMark c={{ state: c.state, startedUnix: c.startedUnix ?? 0, jobInfo: c.jobInfo }} />
  const v = restartVerdict(c)
  if (!v?.short) return null
  return (
    <span className={'restart-mark ' + v.cls} title={`${c.restartPolicy}: ${restartPolicyText(c.restartPolicy, c.restartMax)}.\nNow: ${v.text}`}>
      {v.short}
    </span>
  )
}

/** A job's container: running for how long, or its last run and the next time; the tooltip has the schedule and the times. */
export function JobMark({ c, className = 'restart-mark' }: { c: { state: string; startedUnix: number; jobInfo?: JobInfo | null }; className?: string }) {
  const s = jobState(c)
  if (!s) return null
  return <span className={`${className} job-mark ${s.cls}`} title={s.title}>{s.text}</span>
}

/** The dot of a container, a job's by how its last run went (grey while it waits, red after a failed run). */
export function ContainerDot({ c, label, extra }: { c: { state: string; health: string; startedUnix: number; jobInfo?: JobInfo | null }; label?: boolean; extra?: string }) {
  const s = jobState(c)
  if (!s) return <StateDot state={c.state} health={c.health} label={label} extra={extra} />
  const info = stateInfo(s.dot, c.state === 'running' ? c.health : '')
  return <span className="state" title={s.title}><i className={'dot ' + info.cls} />{label ? 'job' : null}</span>
}

/** Green or red dot for a server that is online or not. */
export function OnlineDot({ online, title }: { online: boolean; title?: string }) {
  return <i className={'dot ' + (online ? 'on' : 'off')} title={title} />
}

/** "online" / "offline" as a badge, next to a server's name. */
export function OnlineBadge({ online }: { online: boolean }) {
  return <span className={'badge ' + (online ? 'ok' : 'bad')}>{online ? 'online' : 'offline'}</span>
}

/** Image freshness as a dot before the image name (registry check); the words live in the tooltip. */
export function ImageDot({ status, detail }: { status: string; detail?: string | null }) {
  const info: Record<string, [string, string]> = {
    latest: ['ok', 'up to date'],
    outdated: ['warn', 'update available'],
    redeploy: ['info', 'newer image already pulled — deploy to use it'],
    pinned: ['muted', 'pinned by digest'],
    unknown: ['muted', 'not checked'],
  }
  const entry = info[status]
  if (!entry) return null
  return <i className={'dot ' + entry[0]} title={detail || entry[1]} />
}

/** Running / problems / updates of a project on one line, dots instead of badges. */
/** `jobs`: containers of jobs on a schedule; the ones waiting for their next run are not in `total` (they are neither running nor stopped). */
export function ProjectStatus({ p }: { p: { running: number; total: number; problems: number; updates?: number; jobs?: number } }) {
  const jobs = p.jobs ? <span className="muted" title="jobs on a schedule: they run at their time and exit"> · {p.jobs} job{p.jobs > 1 ? 's' : ''}</span> : null
  if (p.total === 0 && p.problems === 0) return p.jobs
    ? <span className="state muted" title="only jobs on a schedule, none running now"><i className="dot muted" />{p.jobs} job{p.jobs > 1 ? 's' : ''} waiting</span>
    : <span className="state muted" title="no containers on the server"><i className="dot muted" />not deployed</span>
  const partial = p.running < p.total
  return (
    <span className="state">
      {p.problems > 0
        ? <span className="error" title={`${p.problems} unhealthy, crash-looping, or a job whose run failed`}><i className="dot bad" />{p.problems} problem{p.problems > 1 ? 's' : ''}<span className="muted"> · {p.running} / {p.total} running</span></span>
        : p.running === 0
          ? <span className="muted"><i className="dot muted" />stopped <span className="mini">({p.total})</span></span>
          : <><i className={'dot ' + (partial ? 'warn' : 'ok')} />{partial ? `${p.running} / ${p.total}` : p.running} running</>}
      {jobs}
      {p.updates ? <span title="containers whose image has a newer version" style={{ marginLeft: 10 }}><i className="dot warn" />{p.updates} update{p.updates > 1 ? 's' : ''}</span> : null}
    </span>
  )
}

/** Up to `max` names, the rest behind a clickable "+N". */
export function NameList({ names, max = 4 }: { names: string[]; max?: number }) {
  if (names.length === 0) return <span className="muted">—</span>
  return <Collapsed items={names} max={max} className="muted names" />
}

/** Relative time that never wraps, with the exact date on hover. */
export function Ago({ iso, suffix }: { iso: string | null | undefined; suffix?: string }) {
  return <span className="nowrap muted" title={iso ? when(iso) : 'never'}>{agoShort(iso)}{suffix ? ' ' + suffix : ''}</span>
}

/** "5432", "8765→8080", "5000/udp"; more than three sit behind a clickable "+N". */
export function Ports({ ports, max = 3 }: { ports: string[]; max?: number }) {
  const list = formatPorts(ports)
  if (list.length === 0) return null
  return <Collapsed items={list} max={max} className="ports" />
}
