/**
 * Turns container log lines into something readable: docker's timestamp prefix, structured JSON logs (Serilog's
 * JsonFormatter, CLEF, and the usual {level, message} shapes) and plain text with a level word in it.
 */

type Level = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'fatal' | null

export interface LogLine {
  raw: string
  /** HH:mm:ss.mmm in local time, from the entry itself or docker's prefix */
  time: string | null
  level: Level
  message: string
  /** logger / SourceContext, shortened to its last part */
  source: string | null
  exception: string | null
  /** the parsed JSON entry, shown pretty-printed when the line is opened */
  json: Record<string, unknown> | null
}

const DOCKER_TS = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)) /
// eslint-disable-next-line no-control-regex
const ANSI = /\x1b\[[0-9;]*[A-Za-z]/g
const TEXT_LEVEL = /\b(trace|trce|verbose|vrb|debug|dbug|dbg|info|inf|information|warn|warning|wrn|error|err|fail|fatal|ftl|crit|critical)\b/i

function levelOf(v: unknown): Level {
  switch (String(v ?? '').toLowerCase()) {
    case 'trace': case 'trce': case 'verbose': case 'vrb': case '10': return 'trace'
    case 'debug': case 'dbug': case 'dbg': case '20': return 'debug'
    case 'information': case 'info': case 'inf': case '30': return 'info'
    case 'warning': case 'warn': case 'wrn': case '40': return 'warn'
    case 'error': case 'err': case 'fail': case '50': return 'error'
    case 'fatal': case 'ftl': case 'crit': case 'critical': case '60': return 'fatal'
    default: return null
  }
}

function clock(iso: string | null | undefined): string | null {
  if (!iso) return null
  const d = new Date(iso)
  if (isNaN(d.getTime())) return null
  const p = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}

const short = (v: unknown) => typeof v === 'string' ? v : v === null || v === undefined ? '' : JSON.stringify(v)

/** Serilog / MEL message template: {Name}, {Name:format}, {@Name}, {$Name}, {Name,10} filled from the properties. */
function render(template: string, props: Record<string, unknown>): string {
  return template.replace(/\{([@$]?)([A-Za-z0-9_]+)(?:[,:][^}]*)?\}/g, (whole, _op, name: string) => name in props ? short(props[name]) : whole)
}

const pick = (o: Record<string, unknown>, ...keys: string[]) => {
  for (const k of keys) if (o[k] !== undefined && o[k] !== null && o[k] !== '') return o[k]
  return undefined
}

function fromJson(o: Record<string, unknown>, dockerTime: string | null): Omit<LogLine, 'raw'> | null {
  const props = (typeof o.Properties === 'object' && o.Properties ? o.Properties : o) as Record<string, unknown>
  const template = pick(o, 'MessageTemplate', '@mt')
  const message = pick(o, 'RenderedMessage', '@m', 'message', 'msg', 'Message', 'text')
    ?? (typeof template === 'string' ? render(template, props) : undefined)
  const levelRaw = pick(o, 'Level', '@l', 'level', 'severity', 'lvl', 'LogLevel', 'levelname')
  // CLEF leaves @l out for Information
  const level = levelOf(levelRaw) ?? ('@t' in o && message !== undefined ? 'info' : null)
  if (message === undefined && level === null) return null
  const source = short(pick(props, 'SourceContext', 'logger', 'Logger', 'category', 'Category', 'name'))
  return {
    time: clock(short(pick(o, 'Timestamp', '@t', 'time', 'timestamp', 'ts', '@timestamp'))) ?? dockerTime,
    level,
    message: short(message),
    source: source ? source.split('.').pop() ?? source : null,
    exception: short(pick(o, 'Exception', '@x', 'exception', 'error', 'stack', 'stacktrace')) || null,
    json: o,
  }
}

export function parseLine(line: string): LogLine {
  let rest = line.replace(ANSI, '')
  let dockerTime: string | null = null
  const ts = DOCKER_TS.exec(rest)
  if (ts) { dockerTime = clock(ts[1]); rest = rest.slice(ts[0].length) }
  const trimmed = rest.trim()
  if (trimmed.startsWith('{') && trimmed.endsWith('}')) {
    try {
      const o = JSON.parse(trimmed) as unknown
      if (o && typeof o === 'object' && !Array.isArray(o)) {
        const parsed = fromJson(o as Record<string, unknown>, dockerTime)
        if (parsed) return { raw: line, ...parsed }
      }
    } catch { /* not JSON after all */ }
  }
  // plain text: a level word near the start says how bad it is
  const m = TEXT_LEVEL.exec(rest.slice(0, 80))
  return { raw: line, time: dockerTime, level: m ? levelOf(m[1]) : null, message: rest, source: null, exception: null, json: null }
}

const RANK: Record<string, number> = { trace: 0, debug: 1, info: 2, warn: 3, error: 4, fatal: 5 }
/** Lines at or above `min`; lines without a level count as info. */
export const atLeast = (l: LogLine, min: 'all' | 'warn' | 'error') => min === 'all' || RANK[l.level ?? 'info'] >= RANK[min]

export const LEVEL_TAG: Record<string, string> = { trace: 'TRC', debug: 'DBG', info: 'INF', warn: 'WRN', error: 'ERR', fatal: 'FTL' }
