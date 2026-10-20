import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, type DmEvent } from '../lib/api'
import { agoShort, when } from '../lib/fmt'
import { useEvents } from '../lib/live'
import Icon from './Icon'
import { ICON } from './icons'

const LEVEL: Record<DmEvent['level'], { cls: string; word: string }> = {
  error: { cls: 'badge bad', word: 'error' },
  warning: { cls: 'badge warn', word: 'warning' },
  info: { cls: 'badge muted', word: 'info' },
}

/** Two lists of events as one, newest first, each event once. */
function merge(a: DmEvent[], b: DmEvent[]) {
  const seen = new Set<number>()
  return [...a, ...b].filter(e => !seen.has(e.id) && seen.add(e.id)).sort((x, y) => y.id - x.id)
}

/**
 * Events newest first, new ones added as they happen. `projectId` / `agentId` narrow the list, `warnings` leaves out
 * the info ones, `kind` keeps one kind ("audit": what people did); `more` pages back through older ones.
 */
export default function EventList({ projectId, agentId, container, warnings = false, kind, hours, limit = 50, more = false, compact = false, empty = 'Nothing happened yet.' }: {
  projectId?: number; agentId?: number; warnings?: boolean; kind?: string; limit?: number; more?: boolean
  /** only the events about this container (by name) of `agentId` */
  container?: string
  /** only the last hours */
  hours?: number
  /** without the project / server columns (a project's own page) */
  compact?: boolean
  empty?: string
}) {
  const [events, setEvents] = useState<DmEvent[] | null>(null)
  const [end, setEnd] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [olderError, setOlderError] = useState<string | null>(null)

  const query = useCallback((before?: number) => {
    const q = new URLSearchParams({ limit: String(limit) })
    if (projectId !== undefined) q.set('projectId', String(projectId))
    if (agentId !== undefined) q.set('agentId', String(agentId))
    if (container !== undefined) q.set('container', container)
    if (warnings) q.set('warnings', 'true')
    if (kind !== undefined) q.set('kind', kind)
    if (hours !== undefined) q.set('hours', String(hours))
    if (before !== undefined) q.set('before', String(before))
    return api<DmEvent[]>('/events?' + q.toString())
  }, [projectId, agentId, container, warnings, kind, hours, limit])

  // what came in live while the list was being read stays in it
  const reload = useCallback(() => {
    query().then(list => {
      setEvents(prev => merge(list, prev ?? []).slice(0, Math.max(limit, list.length)))
      setEnd(list.length < limit); setError(null)
    }).catch(e => setError(errorText(e)))
  }, [query, limit])
  useEffect(reload, [reload])

  useEvents(useCallback((e: DmEvent) => {
    if (projectId !== undefined && e.projectId !== projectId) return
    if (agentId !== undefined && e.agentId !== agentId) return
    if (container !== undefined && e.container !== container) return
    if (warnings && e.level === 'info') return
    if (kind !== undefined && e.kind !== kind) return
    setEvents(prev => prev?.some(x => x.id === e.id) ? prev : [e, ...(prev ?? [])].slice(0, more ? undefined : limit))
  }, [projectId, agentId, container, warnings, kind, limit, more]), reload)

  async function older() {
    if (!events?.length) return
    setOlderError(null)
    try {
      const list = await query(events[events.length - 1].id)
      setEvents(prev => merge(prev ?? [], list)); setEnd(list.length < limit)
    } catch (e) { setOlderError(errorText(e)) }
  }

  if (error) return <p className="error">{error}</p>
  if (!events) return <p className="muted">…</p>
  if (events.length === 0) return <p className="muted">{empty}</p>
  return (
    <>
      <div className="event-list">
        {events.map(e => (
          <div key={e.id} className="event">
            <span className="nowrap mini" title={when(e.at)}>{agoShort(e.at)}</span>
            <span className={LEVEL[e.level]?.cls ?? 'badge'}>{LEVEL[e.level]?.word ?? e.level}</span>
            <span className="event-kind mini">{e.kind}</span>
            <span className="event-text">
              {e.user && <span className="event-user" title="who did it"><Icon d={ICON.user} size={13} stroke={2} />{e.user}</span>}
              {!compact && e.project && e.projectId !== null && <><Link to={`/projects/${e.projectId}`}>{e.project}</Link> · </>}
              {!compact && !e.project && e.agent && e.agentId !== null && <><Link to={`/agents/${e.agentId}`}>{e.agent}</Link> · </>}
              {e.message}
              {e.details && <details className="event-details"><summary className="mini">its last lines</summary><pre className="inline-out">{e.details}</pre></details>}
            </span>
          </div>
        ))}
      </div>
      {more && !end && <div className="toolbar"><button onClick={() => { void older() }}>Older</button>{olderError && <span className="error">{olderError}</span>}</div>}
    </>
  )
}
