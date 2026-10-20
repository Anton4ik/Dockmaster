import { useEffect, useMemo, useState } from 'react'
import { api, type SchedulePreview } from '../../lib/api'

/** A job's schedule as the form holds it: the cron expression, the time zone its times are in, the timeout ("" = none). */
export interface ScheduleValue { cron: string; timeZone: string; timeout: string }

type Mode = 'every' | 'daily' | 'weekdays' | 'weekly' | 'monthly' | 'cron'
const MODES: { id: Mode; text: string }[] = [
  { id: 'every', text: 'every…' }, { id: 'daily', text: 'every day' }, { id: 'weekdays', text: 'Mon–Fri' }, { id: 'weekly', text: 'some days' }, { id: 'monthly', text: 'monthly' }, { id: 'cron', text: 'cron' },
]
const MINUTE_STEPS = [1, 2, 5, 10, 15, 20, 30]
const HOUR_STEPS = [1, 2, 3, 4, 6, 8, 12]
const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

/** The browser's time zone: the one a person means when they type 03:00. */
export function browserTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC' } catch { return 'UTC' }
}

function timeZones(): string[] {
  try {
    const list = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.('timeZone') ?? []
    return list.length ? list : [browserTimeZone(), 'UTC']
  } catch { return [browserTimeZone(), 'UTC'] }
}

/** The form's fields for a cron expression, when it has one of the shapes the form offers; cron mode otherwise. */
interface Fields { mode: Mode; n: number; unit: 'minutes' | 'hours'; time: string; days: number[]; day: number }
const DEFAULTS: Fields = { mode: 'daily', n: 15, unit: 'minutes', time: '03:00', days: [1], day: 1 }

function fromCron(cron: string): Fields {
  const f = cron.trim().split(/\s+/)
  const two = (n: number) => String(n).padStart(2, '0')
  const time = (min: string, hour: string) => /^\d+$/.test(min) && /^\d+$/.test(hour) ? `${two(+hour)}:${two(+min)}` : null
  if (f.length !== 5 || f[3] !== '*') return { ...DEFAULTS, mode: 'cron' }
  const [min, hour, day, , dow] = f
  let m: RegExpExecArray | null
  if (day === '*' && dow === '*') {
    if ((m = /^\*\/(\d+)$/.exec(min)) && hour === '*') return { ...DEFAULTS, mode: 'every', n: +m[1], unit: 'minutes' }
    if (min === '0' && (m = /^\*\/(\d+)$/.exec(hour))) return { ...DEFAULTS, mode: 'every', n: +m[1], unit: 'hours' }
    if (min === '0' && hour === '*') return { ...DEFAULTS, mode: 'every', n: 1, unit: 'hours' }
    if (min === '*' && hour === '*') return { ...DEFAULTS, mode: 'every', n: 1, unit: 'minutes' }
    const t = time(min, hour)
    if (t) return { ...DEFAULTS, mode: 'daily', time: t }
  } else if (day === '*') {
    const t = time(min, hour)
    if (t && dow === '1-5') return { ...DEFAULTS, mode: 'weekdays', time: t }
    if (t && /^[0-7](,[0-7])*$/.test(dow)) return { ...DEFAULTS, mode: 'weekly', time: t, days: [...new Set(dow.split(',').map(d => +d === 0 ? 7 : +d))].sort() }
  } else if (dow === '*' && /^\d+$/.test(day)) {
    const t = time(min, hour)
    if (t) return { ...DEFAULTS, mode: 'monthly', time: t, day: +day }
  }
  return { ...DEFAULTS, mode: 'cron' }
}

function toCron(f: Fields): string {
  const [h, m] = f.time.split(':').map(Number)
  const hm = `${m || 0} ${h || 0}`
  switch (f.mode) {
    case 'every': return f.unit === 'minutes' ? (f.n === 1 ? '* * * * *' : `*/${f.n} * * * *`) : (f.n === 1 ? '0 * * * *' : `0 */${f.n} * * *`)
    case 'daily': return `${hm} * * *`
    case 'weekdays': return `${hm} * * 1-5`
    case 'weekly': return `${hm} * * ${(f.days.length ? f.days : [1]).map(d => d === 7 ? 0 : d).sort().join(',')}`
    case 'monthly': return `${hm} ${f.day} * *`
    default: return ''
  }
}

/**
 * When a job runs, as a form: every N minutes or hours, every day / Mon–Fri / some days of the week / a day of the
 * month at a time, or a cron expression by hand — always a cron expression underneath. The times are in the chosen
 * time zone (the browser's by default). The server says back what the expression means and the next times it names.
 */
export default function SchedulePicker({ value, onChange, idPrefix = 'sp' }: { value: ScheduleValue; onChange: (v: ScheduleValue) => void; idPrefix?: string }) {
  const [fields, setFields] = useState<Fields>(() => fromCron(value.cron))
  const [cronText, setCronText] = useState(value.cron)
  const zones = useMemo(() => timeZones(), [])
  // the server's reading of an expression, with the expression it was asked about
  const [preview, setPreview] = useState<{ cron: string; data: SchedulePreview | null } | null>(null)

  function set(patch: Partial<Fields>) {
    const f = { ...fields, ...patch }
    setFields(f)
    if (f.mode !== 'cron') { const cron = toCron(f); setCronText(cron); onChange({ ...value, cron }) }
    else onChange({ ...value, cron: cronText })
  }
  const setCron = (text: string) => { setCronText(text); onChange({ ...value, cron: text }) }

  // what the expression means and when it fires next, from the server, a moment after the last keystroke
  useEffect(() => {
    const cron = value.cron
    if (!cron.trim()) return
    const t = setTimeout(() => {
      api<SchedulePreview>(`/projects/schedule-preview?cron=${encodeURIComponent(cron)}&timeZone=${encodeURIComponent(value.timeZone)}`)
        .then(data => setPreview({ cron, data })).catch(() => setPreview({ cron, data: null }))
    }, 300)
    return () => clearTimeout(t)
  }, [value.cron, value.timeZone])
  // only for the expression it was fetched for: an emptied field shows nothing, with no state reset inside the effect
  const shown = preview?.cron === value.cron ? preview.data : null

  const fmt = (iso: string) => {
    try { return new Date(iso).toLocaleString(undefined, { timeZone: value.timeZone || 'UTC', weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) }
    catch { return new Date(iso).toLocaleString() }
  }
  const timeInput = <input id={`${idPrefix}-time`} type="time" aria-label="time of day" value={fields.time} onChange={e => set({ time: e.target.value || '00:00' })} required />

  return (
    <div className="schedule-picker">
      <div className="seg" role="group" aria-label="how often">
        {MODES.map(m => <button key={m.id} type="button" className={m.id === fields.mode ? 'on' : undefined} aria-pressed={m.id === fields.mode} onClick={() => set({ mode: m.id })}>{m.text}</button>)}
      </div>
      <div className="form-row schedule-when">
        {fields.mode === 'every' && <>
          <span>every</span>
          <select aria-label="how many" className="cell" value={fields.n} onChange={e => set({ n: Number(e.target.value) })}>
            {(fields.unit === 'minutes' ? MINUTE_STEPS : HOUR_STEPS).map(n => <option key={n} value={n}>{n}</option>)}
          </select>
          <select aria-label="minutes or hours" className="cell" value={fields.unit} onChange={e => set({ unit: e.target.value as Fields['unit'], n: e.target.value === 'minutes' ? 15 : 1 })}>
            <option value="minutes">minutes</option><option value="hours">hours</option>
          </select>
        </>}
        {fields.mode === 'daily' && <><span>every day at</span>{timeInput}</>}
        {fields.mode === 'weekdays' && <><span>Monday to Friday at</span>{timeInput}</>}
        {fields.mode === 'weekly' && <>
          <span className="checks">
            {DAYS.map((d, i) => {
              const n = i + 1
              return <label key={d}><input type="checkbox" checked={fields.days.includes(n)} onChange={e => set({ days: e.target.checked ? [...fields.days, n].sort() : fields.days.filter(x => x !== n) })} />{d}</label>
            })}
          </span>
          <span>at</span>{timeInput}
        </>}
        {fields.mode === 'monthly' && <>
          <span>on day</span>
          <select aria-label="day of the month" className="cell" value={fields.day} onChange={e => set({ day: Number(e.target.value) })}>
            {Array.from({ length: 28 }, (_, i) => i + 1).map(d => <option key={d} value={d}>{d}</option>)}
          </select>
          <span>of every month at</span>{timeInput}
        </>}
        {fields.mode === 'cron' && <>
          <input id={`${idPrefix}-cron`} className="cron" aria-label="cron expression" value={cronText} onChange={e => setCron(e.target.value)} placeholder="0 3 * * *" spellCheck={false} />
          <span className="mini">minute hour day month weekday — <code>*/15 * * * *</code>, <code>30 2 * * 1-5</code>, <code>@daily</code></span>
        </>}
      </div>
      <div className="row">
        <div className="narrow">
          <label htmlFor={`${idPrefix}-tz`}>Time zone</label>
          <select id={`${idPrefix}-tz`} className="wide-select" value={value.timeZone} onChange={e => onChange({ ...value, timeZone: e.target.value })}>
            {!zones.includes(value.timeZone) && value.timeZone && <option value={value.timeZone}>{value.timeZone}</option>}
            {zones.map(z => <option key={z} value={z}>{z}</option>)}
          </select>
        </div>
        <div className="narrow">
          <label htmlFor={`${idPrefix}-timeout`}>Stop it if it still runs after</label>
          <input id={`${idPrefix}-timeout`} value={value.timeout} onChange={e => onChange({ ...value, timeout: e.target.value })} placeholder="never — e.g. 30m, 2h" />
        </div>
      </div>
      {shown && (shown.ok
        ? <p className="mini schedule-preview">
            <b>{shown.description}</b>{shown.next.length > 0 && <> · next: {shown.next.map(fmt).join(', ')}</>}
            {shown.zoneProblem && <span className="warn-text"> · {shown.zoneProblem}</span>}
          </p>
        : <p className="error">{shown.error}</p>)}
    </div>
  )
}
