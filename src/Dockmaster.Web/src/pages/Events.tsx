import { useState } from 'react'
import { Link } from 'react-router-dom'
import EventList from '../components/EventList'

type Show = 'all' | 'warnings' | 'audit'

const SHOW: { key: Show; label: string; title: string }[] = [
  { key: 'all', label: 'All', title: 'everything' },
  { key: 'warnings', label: 'Warnings', title: 'warnings and errors only' },
  { key: 'audit', label: 'Audit', title: 'what people did: deploys and actions, changes to projects, secrets, users, roles and servers' },
]

/**
 * Everything Dockmaster noticed, newest first: failed deploys, servers gone, containers restarting, limits reached,
 * autoscaler decisions — and who did what (Audit).
 */
export default function Events() {
  const [show, setShow] = useState<Show>('all')
  return (
    <>
      <div className="toolbar">
        <h1>Events</h1>
        <div className="seg push-right" role="group" aria-label="which events">
          {SHOW.map(s => <button key={s.key} className={show === s.key ? 'on' : undefined} aria-pressed={show === s.key} title={s.title} onClick={() => setShow(s.key)}>{s.label}</button>)}
        </div>
      </div>
      <p className="mini">Kept for 30 days. With a Seq server set in <Link to="/settings">Settings</Link> they go there too, where its alerts can send them by e-mail.</p>
      <div className="card"><EventList key={show} warnings={show === 'warnings'} kind={show === 'audit' ? 'audit' : undefined} limit={100} more
        empty={show === 'audit' ? 'Nobody did anything yet.' : undefined} /></div>
    </>
  )
}
