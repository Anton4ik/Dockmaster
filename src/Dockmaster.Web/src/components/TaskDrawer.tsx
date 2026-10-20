import { useState } from 'react'
import type { StreamResult } from '../lib/live'
import Drawer from './Drawer'

/**
 * The side drawer of a streamed task (deploy, pull & redeploy): its output, a dot for how it goes, and the outcome at
 * the end. While it runs, "hide" (and Escape) only puts it away — the task goes on and a chip in the corner brings
 * the log back — and "cancel" stops it on the server.
 */
export default function TaskDrawer({ title, text, running, result, onClose, onCancel }: {
  title: string
  text: string
  running: boolean
  result: StreamResult | null | undefined
  /** closes a finished task's log */
  onClose: () => void
  /** stops the running task; none = it cannot be stopped from here (a log followed after the page was left) */
  onCancel?: () => void
}) {
  const [hidden, setHidden] = useState(false)
  const dot = <i className={'dot ' + (running ? 'warn' : result == null ? 'muted' : result.ok ? 'ok' : 'bad')} />
  if (hidden) return (
    <button className="task-chip" onClick={() => setHidden(false)} title="show the log">
      {dot}{title}{running ? ' — running' : result ? (result.ok ? ' — done' : ' — failed') : ''} · show log
    </button>
  )
  return (
    <Drawer title={title} text={text} icon={dot}
      onClose={running ? () => setHidden(true) : onClose} closeLabel={running ? 'hide' : 'close'}
      actions={running && onCancel && <button className="sm danger" onClick={onCancel} title="stop it on the server">cancel</button>}
      footer={!running && result && <b className={result.ok ? 'ok' : 'error'}>{'\n' + (result.ok ? '✓ ' : '✗ ') + result.message}</b>} />
  )
}
