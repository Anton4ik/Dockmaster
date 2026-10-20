import { useEffect, useState } from 'react'
import { currentToasts, dismissToast, subscribeToasts, type Toast } from '../lib/toast'

/** Renders the notices of toast() in the corner of the screen; finished ones have a close button. */
export default function Toasts() {
  const [list, setList] = useState<Toast[]>(currentToasts)
  useEffect(() => subscribeToasts(setList), [])
  if (list.length === 0) return null
  return (
    <div className="toasts" role="status" aria-live="polite">
      {list.map(t => (
        <div key={t.id} className={'toast ' + t.kind}>
          {t.kind === 'busy' ? <span className="spinner" aria-hidden="true" /> : <i className={'dot ' + (t.kind === 'ok' ? 'ok' : 'bad')} />}
          <span className="toast-text">{t.text}</span>
          {t.kind !== 'busy' && <button className="icon" aria-label="close" title="close" onClick={() => dismissToast(t.id)}>×</button>}
        </div>
      ))}
    </div>
  )
}
