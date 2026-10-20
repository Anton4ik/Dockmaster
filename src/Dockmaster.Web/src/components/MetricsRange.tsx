import { RANGES } from '../lib/chart'

/** 1 h · 6 h · 24 h, and what the points are. */
export function RangePicker({ hours, onChange, note }: { hours: number; onChange: (h: number) => void; note: string }) {
  return (
    <div className="toolbar">
      <div className="seg" role="group" aria-label="time range">
        {RANGES.map(h => <button key={h} className={h === hours ? 'on' : undefined} aria-pressed={h === hours} onClick={() => onChange(h)}>{h} h</button>)}
      </div>
      <span className="mini">{note}</span>
    </div>
  )
}
