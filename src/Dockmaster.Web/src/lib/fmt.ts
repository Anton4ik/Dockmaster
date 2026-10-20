export function bytes(b: number): string {
  if (!b || b <= 0) return '0'
  const u = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = b, i = 0
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  return (i === 0 ? v.toFixed(0) : v.toFixed(1).replace(/\.0$/, '')) + ' ' + u[i]
}

export const pct = (p: number) => `${(Math.round(p * 10) / 10).toString()}%`

export function ago(iso: string | null | undefined): string {
  if (!iso) return 'never'
  const d = (Date.now() - new Date(iso).getTime()) / 1000
  if (d < 60) return 'just now'
  if (d < 3600) return `${Math.floor(d / 60)} min ago`
  if (d < 172800) return `${Math.floor(d / 3600)} h ago`
  return `${Math.floor(d / 86400)} d ago`
}

/** "4m", "2h", "3d" — for table cells; the full date goes into the title */
export function agoShort(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = (Date.now() - new Date(iso).getTime()) / 1000
  if (d < 60) return 'now'
  if (d < 3600) return `${Math.floor(d / 60)}m`
  if (d < 86400) return `${Math.floor(d / 3600)}h`
  return `${Math.floor(d / 86400)}d`
}

export function uptime(startedUnix: number): string {
  if (!startedUnix) return ''
  return duration(Date.now() / 1000 - startedUnix)
}

export function duration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.floor(s / 60)}m`
  if (s < 86400) return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
  return `${Math.floor(s / 86400)}d ${Math.floor((s % 86400) / 3600)}h`
}

export function shortImage(image: string): string {
  // containers whose tag moved on report the bare image id
  if (image.startsWith('sha256:')) return image.slice(0, 19)
  const at = image.indexOf('@sha256:')
  if (at > 0) image = image.slice(0, at)
  const slash = image.lastIndexOf('/')
  return slash >= 0 && image.length > 40 ? image.slice(slash + 1) : image
}

/** Colour class and the one word that says it all: healthy implies running, so "running · healthy" is just "healthy". */
export function stateInfo(state: string, health: string): { cls: string; text: string } {
  if (state === 'running') {
    if (health === 'unhealthy') return { cls: 'bad', text: 'unhealthy' }
    if (health === 'starting') return { cls: 'warn', text: 'starting' }
    if (health === 'healthy') return { cls: 'ok', text: 'healthy' }
    return { cls: 'ok', text: 'running' }
  }
  if (state === 'restarting' || state === 'dead') return { cls: 'bad', text: state }
  return { cls: 'muted', text: state }
}

/** "0.0.0.0:5432->5432/tcp" -> "5432", "8765->8080/tcp" -> "8765→8080", udp keeps its suffix; duplicates removed. */
export function formatPorts(ports: string[]): string[] {
  const out: string[] = []
  for (const p of ports) {
    const m = p.match(/^(?:(.*):)?(\d+)->(\d+)\/(\w+)$/)
    if (!m) continue
    const [, , host, cont, proto] = m
    const text = (host === cont ? host : `${host}→${cont}`) + (proto === 'tcp' ? '' : '/' + proto)
    if (!out.includes(text)) out.push(text)
  }
  return out
}

/** image of a container has a newer version (in the registry, or already pulled) */
export const hasUpdate = (status: string) => status === 'outdated' || status === 'redeploy'

/** bytes -> compose memory notation (256M, 1G) for editing; '' for no limit */
export function composeMem(b: number): string {
  if (!b || b <= 0) return ''
  if (b % (1 << 30) === 0) return `${b / (1 << 30)}G`
  if (b % (1 << 20) === 0) return `${b / (1 << 20)}M`
  return `${Math.round(b / (1 << 20))}M`
}

export const when = (iso: string) => new Date(iso).toLocaleString()

/** The variables of a .env file: KEY=value lines (export and quotes allowed), comments and blank lines left out. */
export function parseEnvFile(text: string): Record<string, string> {
  const vars: Record<string, string> = {}
  for (const line of text.split(/\r?\n/)) {
    const m = /^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.-]*)\s*=(.*)$/.exec(line)
    if (!m) continue
    const v = m[2].trim()
    vars[m[1]] = /^(['"]).*\1$/.test(v) ? v.slice(1, -1) : v
  }
  return vars
}

/** A value to show that may be a password: its name says so. */
export const looksSecret = (name: string) => /PASS|SECRET|TOKEN|KEY|CONNECTIONSTRING/i.test(name)

/** Why a run ended, as a person reads it (the server words its events the same way). */
export function exitReason(code: number, oom: boolean): string {
  if (oom) return `out of memory (exit code ${code})`
  const why: Record<number, string> = {
    0: 'it ended by itself', 1: 'an error in the app', 2: 'an error in the app', 125: 'docker could not start it', 126: 'its command cannot be run',
    127: 'its command was not found', 134: 'aborted (SIGABRT)', 137: 'killed (SIGKILL): out of memory, or it did not stop in time',
    139: 'crashed (segmentation fault)', 143: 'stopped (SIGTERM)',
  }
  const text = why[code] ?? (code > 128 && code < 160 ? `ended by signal ${code - 128}` : '')
  return `exit code ${code}${text ? ' — ' + text : ''}`
}

/** Unhealthy, crash-looping or dead — and a job whose last run failed: what the red "problems" counters count (the server counts alike). */
export const isProblem = (c: { health: string; state: string; restartCount: number; exitCode?: number; job?: boolean }) =>
  (c.health === 'unhealthy' && c.state === 'running') || c.state === 'restarting' || c.state === 'dead'
  || (c.state === 'exited' && (c.job ? (c.exitCode ?? 0) !== 0 : c.restartCount > 0))

/**
 * The numbers a list shows for a set of containers. Jobs waiting for their next run are neither running nor stopped:
 * they are left out of the total and counted apart (the server counts the same way for its lists).
 */
export function counts(cs: { health: string; state: string; restartCount: number; exitCode?: number; job?: boolean; update?: string }[]) {
  const running = cs.filter(c => c.state === 'running').length
  return { running, total: cs.filter(c => !c.job || c.state === 'running').length, problems: cs.filter(isProblem).length, jobs: cs.filter(c => c.job).length,
    updates: cs.filter(c => c.update && hasUpdate(c.update)).length }
}

/** "in 5 min", "in 3 h", "in 2 days"; "now" when the time has come. */
export function untilShort(iso: string | null | undefined): string {
  if (!iso) return ''
  const s = (new Date(iso).getTime() - Date.now()) / 1000
  if (s < 45) return 'now'
  if (s < 3600) return `in ${Math.round(s / 60)} min`
  if (s < 86400) return `in ${Math.round(s / 3600)} h`
  const days = Math.round(s / 86400)
  return `in ${days} day${days === 1 ? '' : 's'}`
}

/**
 * Where a job's container stands, in a few words for a table and a longer tooltip: running for how long, or how its
 * last run went and when, and when the next one is. `cls` colours it: ok / bad (the last run failed) / muted.
 */
export function jobState(c: { state: string; startedUnix: number; jobInfo?: { schedule: string; cron: string; timeZone: string | null; nextAt: string | null;
  lastRun: { startedAt: string; finishedAt: string | null; ok: boolean | null; exitCode: number | null; note: string } | null } | null }) {
  const j = c.jobInfo
  if (!j) return null
  const next = j.nextAt ? `next ${untilShort(j.nextAt)}` : 'no next time'
  const r = j.lastRun
  const took = r?.finishedAt ? duration((new Date(r.finishedAt).getTime() - new Date(r.startedAt).getTime()) / 1000) : ''
  const lines = [`${j.schedule} (${j.cron}, ${j.timeZone ?? 'UTC'})`]
  if (r) lines.push(`last run: ${when(r.startedAt)}${took ? `, took ${took}` : ''}${r.exitCode !== null ? `, exit code ${r.exitCode}` : ''}${r.note ? ` — ${r.note}` : ''}`)
  if (j.nextAt) lines.push(`next run: ${when(j.nextAt)}`)
  const title = lines.join('\n')
  if (c.state === 'running') return { cls: 'ok', dot: 'running', text: `running ${uptime(c.startedUnix)}`, title }
  if (!r) return { cls: 'muted', dot: 'exited', text: `waiting · ${next}`, title: title + '\nnot run yet' }
  if (r.finishedAt === null) return { cls: 'muted', dot: 'exited', text: next, title }
  const short = agoShort(r.finishedAt), at = short === 'now' ? 'just now' : `${short} ago`
  if (r.ok === false) return { cls: 'bad', dot: 'dead', text: `failed ${at} · ${next}`, title }
  if (r.ok === null) return { cls: 'muted', dot: 'exited', text: `cut short ${at} · ${next}`, title }
  return { cls: 'ok-text', dot: 'exited', text: `ok ${at} (${took}) · ${next}`, title }
}

/** Who started a run of a job, in words. */
export const triggerText = (t: string) => t === 'schedule' ? 'on schedule' : t === 'manual' ? 'run now' : 'started elsewhere'

/** How long a restart counts as a live problem (red); after that it is history (grey). */
export const RECENT_RESTART_SECONDS = 3600

/**
 * Docker's restart count is for the container's whole life and only starts over when it is re-created, so a fixed
 * container keeps its count. Red only while the last restart is recent; the words say when it was.
 */
export function restartRecency(c: { restartCount: number; startedUnix: number; lastExit?: { unixMs: number } | null }) {
  if (c.restartCount <= 0) return { recent: false, text: 'no restarts', short: '0 restarts' }
  // the last automatic restart: when its last run ended, else when it started again
  const last = c.lastExit ? c.lastExit.unixMs / 1000 : c.startedUnix
  const since = Date.now() / 1000 - last
  const recent = last > 0 && since < RECENT_RESTART_SECONDS
  const n = `${c.restartCount} restart${c.restartCount === 1 ? '' : 's'}`
  const times = `${n} since the container was created`
  return {
    recent,
    text: last > 0 ? `${times}, the last ${duration(since)} ago` + (recent ? '' : ' — running fine since') : times,
    short: last > 0 ? `${n} · last ${duration(since)} ago` : n,
  }
}

/** Identity of a container that survives re-creation: compose project/service, else the name. */
export const containerKey = (c: { project: string; service: string; name: string }) =>
  c.project && c.service ? `${c.project}/${c.service}` : c.name

export interface Thresholds { warn: number; bad: number }
/** Percent of a host's (or a limit's) capacity from which usage shows yellow / red. */
export const USAGE: Record<'cpu' | 'memory' | 'disk', Thresholds> = {
  cpu: { warn: 70, bad: 90 },
  memory: { warn: 75, bad: 90 },
  disk: { warn: 80, bad: 90 },
}

/** Usage bar colour: yellow above `warn`, red above `bad` percent. */
export const barClass = (v: number, t: Thresholds) => (v > t.bad ? 'bar bad' : v > t.warn ? 'bar warn' : 'bar')

/**
 * Text colour for the containers' limits added up against the host. Limits are ceilings, not reservations: they
 * rarely all fill at once, so adding up to more than the host is normal. CPU limits only slow a container down —
 * never coloured. Memory: yellow from 150 % (the host could run out before the limits stop anyone), red from 250 %.
 * The usage bars show what is really used.
 */
export const limitShareClass = (share: number, kind: 'memory' | 'cpu') =>
  kind === 'cpu' ? '' : share > 250 ? 'error' : share > 150 ? 'warn-text' : ''

/** The tooltip line under the limits' sum: why a sum above the host is (or is not) a worry. */
export const limitShareHint = (kind: 'memory' | 'cpu') => kind === 'cpu'
  ? 'CPU limits only slow a container down, so adding up to more cores than the host has is normal.'
  : 'Limits are ceilings, not reservations: up to about 150 % of the host is normal. The memory bar shows what is really used.'

export interface RestartState { state: string; restartPolicy: string; restartMax: number; exitCode: number; restartCount: number; job?: boolean }

/** What a restart policy means, in words (Docker's names say little on their own). */
export function restartPolicyText(policy: string, max: number): string {
  switch (policy) {
    case 'always': return 'Docker restarts it whenever it stops by itself, and after Docker or the host restarts — even if it was stopped by hand'
    case 'unless-stopped': return 'Docker restarts it whenever it stops by itself and after Docker or the host restarts, unless it was stopped by hand'
    case 'on-failure': return `Docker restarts it only when it crashes (exits with an error)${max > 0 ? `, at most ${max} times` : ''}; a normal exit stays down`
    case 'no': return 'Docker never restarts it: once it stops or crashes, it stays down'
    default: return ''
  }
}

/**
 * Will Docker bring this container back, or is it down for good? `cls` is ok / warn / bad / muted; `short` is the
 * few words a crowded table shows next to the name, null when all is as it should be (running, restarts on a crash).
 * Null when the agent is too old to report the policy.
 */
export function restartVerdict(c: RestartState): { cls: string; text: string; short: string | null } | null {
  const p = c.restartPolicy
  if (!p) return null
  const auto = p === 'always' || p === 'unless-stopped'
  if (c.state === 'restarting') return { cls: 'bad', text: 'it keeps crashing and Docker keeps restarting it', short: 'crash loop' }
  // a job: started by Dockmaster on its schedule, over when the app exits
  if (c.job) {
    if (c.state === 'running') return { cls: 'ok', text: 'a run is going on; it ends when the app exits', short: null }
    if (c.state === 'created') return { cls: 'muted', text: 'created by the deploy, waits for its first scheduled run', short: 'waiting' }
    if (c.exitCode === 0) return { cls: 'muted', text: 'the last run finished (exit code 0); it waits for its next time', short: 'finished' }
    return { cls: 'bad', text: `the last run failed (exit code ${c.exitCode}); it runs again at its next time`, short: 'failed' }
  }
  if (c.state === 'running') {
    if (auto) return { cls: 'ok', text: 'if it crashes, Docker starts it again', short: null }
    if (p === 'on-failure') return { cls: 'ok', text: `if it crashes, Docker starts it again${c.restartMax > 0 ? ` (up to ${c.restartMax} times)` : ''}`, short: null }
    return { cls: 'warn', text: 'if it crashes, it stays down — no restart policy', short: 'no auto-restart' }
  }
  if (c.state === 'created') return { cls: 'muted', text: 'created but never started', short: 'never started' }
  // stopped (exited / dead)
  const crashed = c.exitCode !== 0
  const exit = `exit code ${c.exitCode}`
  if (auto) return { cls: 'muted', text: `stopped by hand (or by a deploy) — Docker restarts only containers that stop by themselves${p === 'always' ? '; it comes back when Docker or the host restarts' : ''}`, short: 'stopped by hand' }
  if (p === 'on-failure') {
    if (!crashed) return { cls: 'muted', text: 'finished normally (exit code 0) — on-failure restarts crashes only', short: 'finished' }
    if (c.restartMax > 0 && c.restartCount >= c.restartMax) return { cls: 'bad', text: `crashed (${exit}) and used up its ${c.restartMax} restarts — it stays down`, short: `gave up after ${c.restartMax}` }
    return { cls: 'bad', text: `crashed (${exit})`, short: 'crashed' }
  }
  return crashed
    ? { cls: 'bad', text: `crashed (${exit}) and is not restarted — no restart policy`, short: 'crashed · stays down' }
    : { cls: 'muted', text: 'stopped (exit code 0), not restarted — no restart policy', short: 'stopped' }
}
