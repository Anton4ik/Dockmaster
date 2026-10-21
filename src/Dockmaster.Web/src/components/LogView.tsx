import { useMemo, useRef, useState } from 'react'
import { useAutoScroll, useSetToggle, useStoredState } from '../lib/hooks'
import { atLeast, LEVEL_TAG, parseLine, type LogLine } from '../lib/logfmt'

interface Prefs { pretty: boolean; wrap: boolean }
const DEFAULT_PREFS: Prefs = { pretty: true, wrap: true }
// stored before `wrap` existed, or edited by hand: what is missing falls back to the default
const PREFS_CODEC = { parse: (s: string): Prefs => ({ ...DEFAULT_PREFS, ...(JSON.parse(s) as Partial<Prefs>) }) }

/** The most lines drawn at once: a following log grows for hours, the browser should not. */
const MAX_LINES = 3000

// parsed lines by their text: a following log only gets appended to, so each line is parsed once
const parsed = new Map<string, LogLine>()
function parseCached(line: string) {
  let p = parsed.get(line)
  if (!p) {
    if (parsed.size > MAX_LINES * 8) parsed.clear()
    p = parseLine(line)
    parsed.set(line, p)
  }
  return p
}

/**
 * A container log with its own toolbar: pretty (structured JSON as time · level · message, a click opens the entry)
 * or raw, wrapped lines or one line per entry, a text filter, a minimum level and follow / pause.
 * Pretty and wrap are remembered in this browser.
 */
export default function LogView({ text, empty, className }: { text: string; empty?: string; className?: string }) {
  const [prefs, setPrefs] = useStoredState<Prefs>('dockmaster.logview', DEFAULT_PREFS, PREFS_CODEC)
  const [follow, setFollow] = useState(true)
  const [filter, setFilter] = useState('')
  const [min, setMin] = useState<'all' | 'warn' | 'error'>('all')
  const [open, toggle] = useSetToggle<string>()
  const box = useRef<HTMLDivElement>(null)
  const set = (p: Partial<Prefs>) => setPrefs(prev => ({ ...prev, ...p }))

  const lines = useMemo(() => {
    const raw = text.split('\n')
    if (raw[raw.length - 1] === '') raw.pop()
    return raw.map(parseCached)
  }, [text])

  const q = filter.trim().toLowerCase()
  const shown = useMemo(() => {
    const all = lines.filter(l => atLeast(l, min) && (!q || l.raw.toLowerCase().includes(q)))
    return all.length > MAX_LINES ? all.slice(-MAX_LINES) : all
  }, [lines, min, q])
  const hidden = lines.length - shown.length

  useAutoScroll(box, shown.length + (shown[shown.length - 1]?.raw ?? ''), follow)

  return (
    <div className={'logview ' + (className ?? '')}>
      <div className="log-toolbar">
        <input type="search" aria-label="filter the log" placeholder="filter" value={filter} onChange={e => setFilter(e.target.value)} />
        <select className="cell" aria-label="minimum level" value={min} onChange={e => setMin(e.target.value as typeof min)} style={{ width: 'auto' }}>
          <option value="all">all levels</option>
          <option value="warn">warnings +</option>
          <option value="error">errors</option>
        </select>
        <span className="seg" role="group" aria-label="view">
          <button className={'sm' + (prefs.pretty ? ' on' : '')} aria-pressed={prefs.pretty} onClick={() => set({ pretty: true })} title="structured logs as time · level · message; click a line for the whole entry">pretty</button>
          <button className={'sm' + (!prefs.pretty ? ' on' : '')} aria-pressed={!prefs.pretty} onClick={() => set({ pretty: false })} title="the lines as the container wrote them">raw</button>
        </span>
        <label className="check-inline"><input type="checkbox" checked={prefs.wrap} onChange={e => set({ wrap: e.target.checked })} /> wrap</label>
        <button className="sm push-right" onClick={() => setFollow(f => !f)}>{follow ? 'pause scroll' : 'follow'}</button>
      </div>
      <div className={'log-lines' + (prefs.wrap ? ' wrap' : ' nowrap')} ref={box}>
        {hidden > 0 && <div className="log-note">{q || min !== 'all' ? `${hidden} line${hidden === 1 ? '' : 's'} hidden by the filter` : `showing the last ${MAX_LINES} lines`}</div>}
        {shown.length === 0 && <div className="log-note">{text ? 'nothing matches the filter' : empty ?? '…'}</div>}
        {prefs.pretty ? shown.map((l, i) => <Pretty key={i} l={l} open={open.has(l.raw)} onToggle={() => toggle(l.raw)} />)
          : shown.map((l, i) => <div key={i} className={'log-line' + (l.level ? ' lv-' + l.level : '')}>{l.raw}</div>)}
      </div>
    </div>
  )
}

/** One line as time · level · message; a structured entry or an exception opens in place (click, Enter or Space). */
function Pretty({ l, open, onToggle }: { l: LogLine; open: boolean; onToggle: () => void }) {
  const expandable = !!(l.json || l.exception)
  const opener = expandable ? {
    role: 'button', tabIndex: 0, 'aria-expanded': open, onClick: onToggle,
    onKeyDown: (e: React.KeyboardEvent) => { if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onToggle() } },
  } : {}
  return (
    <div className={'log-line pretty' + (l.level ? ' lv-' + l.level : '') + (expandable ? ' expandable' : '')} {...opener}>
      {l.time && <span className="log-time">{l.time}</span>}
      {l.level && <span className={'log-level ' + l.level}>{LEVEL_TAG[l.level]}</span>}
      <span className="log-msg">{l.message}</span>
      {l.source && <span className="log-source">{l.source}</span>}
      {l.exception && !open && <span className="log-exc">⚠ exception</span>}
      {open && (
        <pre className="log-entry" onClick={e => e.stopPropagation()}>
          {l.exception && <span className="log-exc-text">{l.exception + '\n\n'}</span>}
          {l.json ? JSON.stringify(l.json, null, 2) : l.raw}
        </pre>
      )}
    </div>
  )
}
