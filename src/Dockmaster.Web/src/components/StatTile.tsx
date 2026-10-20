import type { ReactNode } from 'react'
import { barClass, type Thresholds } from '../lib/fmt'

/**
 * One tile of a stats row: the label (with `aside` at its right: a detail or a link), the big number, then an
 * optional small line and whatever `children` add (a usage bar, a limits line).
 */
export function StatTile({ label, aside, value, of, tone, note, bad, children }: {
  label: string
  aside?: ReactNode
  value: ReactNode
  /** the total the value is out of: "/ 12" after it, smaller */
  of?: ReactNode
  tone?: 'ok' | 'bad'
  note?: ReactNode
  /** the small line is a warning: red */
  bad?: boolean
  children?: ReactNode
}) {
  return (
    <div className="stat">
      {aside === undefined ? <div className="label">{label}</div> : <div className="kv"><span>{label}</span>{aside}</div>}
      <div className={'value' + (tone ? ' ' + tone : '')}>{value}{of !== undefined && <> <span className="muted of">/ {of}</span></>}</div>
      {note !== undefined && <div className={'mini' + (bad ? ' error' : '')}>{note}</div>}
      {children}
    </div>
  )
}

/** How full something is, as a thin bar: yellow and red past the thresholds. */
export function UsageBar({ percent, limits }: { percent: number; limits: Thresholds }) {
  return <div className={barClass(percent, limits)}><span style={{ width: Math.min(100, percent) + '%' }} /></div>
}
