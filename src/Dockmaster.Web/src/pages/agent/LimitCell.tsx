import type { Agent, Container } from '../../lib/api'
import { bytes, pct, USAGE } from '../../lib/fmt'
import type { LimitEdit, LimitEditor } from './useLimitEdit'

/** The limit being typed: Enter applies it live, Escape gives up; a refusal shows under it. */
function LimitForm({ c, edit, limits }: { c: Container; edit: LimitEdit; limits: LimitEditor }) {
  const memory = edit.field === 'memory'
  return (
    <form className="limit-form" onSubmit={e => { e.preventDefault(); void limits.save(c, edit) }}>
      <span className="limit-row">
        <input className={'cell ' + edit.field + (edit.error ? ' invalid' : '')} autoFocus value={edit.value}
          aria-label={memory ? 'memory limit' : 'CPU limit'} placeholder={memory ? '512M' : '0.5'}
          title={edit.error ?? (memory ? 'memory limit, e.g. 512M, 1G — Enter applies, Esc cancels' : 'CPU cores, e.g. 0.5 or 2 — Enter applies, Esc cancels')}
          onChange={e => limits.change({ ...edit, value: e.target.value, error: undefined })} onKeyDown={e => { if (e.key === 'Escape') limits.cancel() }} />
        <button className="icon ok" type="submit" title="apply now, without restarting" aria-label="apply">✓</button>
        <button className="icon" type="button" title="cancel" aria-label="cancel" onClick={limits.cancel}>×</button>
      </span>
      {edit.error && <span className="limit-error" role="alert">{edit.error}</span>}
    </form>
  )
}

/** CPU use of a running container, with its limit; a click on either sets or changes the limit. */
export function CpuCell({ c, agent, limits }: { c: Container; agent: Agent; limits: LimitEditor }) {
  const edit = limits.of(c, 'cpus')
  if (edit) return <LimitForm c={c} edit={edit} limits={limits} />
  if (c.state !== 'running') return null
  if (!agent.cpuLimits) return <span title="this host's kernel cannot limit CPU (no CFS quota — e.g. Synology)">{pct(c.cpuPercent)}</span>
  if (!limits.editable) return <>{pct(c.cpuPercent)}{c.cpuLimit > 0 && <span className="muted"> / {c.cpuLimit}</span>}</>
  return c.cpuLimit > 0
    ? <>{pct(c.cpuPercent)}{' / '}<button className="limit" title="CPU limit in cores — click to change it live" onClick={() => limits.start(c, 'cpus')}>{c.cpuLimit}</button></>
    : <button className="limit none" title="no CPU limit — click to set one live" onClick={() => limits.start(c, 'cpus')}>{pct(c.cpuPercent)}</button>
}

/** Memory use of a running container, with its limit and how full it is; a click sets or changes the limit. */
export function MemoryCell({ c, agent, limits }: { c: Container; agent: Agent; limits: LimitEditor }) {
  const edit = limits.of(c, 'memory')
  if (edit) return <LimitForm c={c} edit={edit} limits={limits} />
  if (c.state !== 'running') return null
  if (!limits.editable) return <span className="mem-cell"><span>{bytes(c.memoryUsage)}{c.memoryLimit > 0 && <span className="muted"> / {bytes(c.memoryLimit)}</span>}</span><MemPct used={c.memoryUsage} limit={c.memoryLimit} host={agent.memoryTotal} /></span>
  return c.memoryLimit > 0
    ? <span className="mem-cell"><span>{bytes(c.memoryUsage)}{' / '}<button className="limit" title="memory limit — click to change it live" onClick={() => limits.start(c, 'memory')}>{bytes(c.memoryLimit)}</button></span><MemPct used={c.memoryUsage} limit={c.memoryLimit} host={agent.memoryTotal} /></span>
    : <span className="mem-cell"><button className="limit none" title="no memory limit — click to set one live" onClick={() => limits.start(c, 'memory')}>{bytes(c.memoryUsage)}</button><MemPct used={c.memoryUsage} limit={0} host={agent.memoryTotal} /></span>
}

/** Share of the memory limit, or of the host's memory when there is none; yellow and red as for the host's memory. */
export function MemPct({ used, limit, host }: { used: number; limit: number; host: number }) {
  const of = limit > 0 ? limit : host
  if (!of || !used) return null
  const p = 100 * used / of
  return <span className={'mem-pct' + (p >= USAGE.memory.bad ? ' bad' : p >= USAGE.memory.warn ? ' warn' : '')} title={limit > 0 ? 'of the memory limit' : "of the host's memory (no limit set)"}>{p < 1 ? '<1' : Math.round(p)}%</span>
}
