import { useEffect, useRef } from 'react'
import type { Agent } from '../../lib/api'

/** A server that can be offered; `disabled` says why it cannot be picked. */
export interface ServerChoice { agent: Agent; disabled?: string }

/** How `replicas` spread over `count` servers, as the server splits them: evenly, the first ones take the rest. */
const shares = (replicas: number, count: number) =>
  Array.from({ length: count }, (_, i) => Math.floor(replicas / count) + (i < replicas % count ? 1 : 0))

/**
 * The servers a service runs on: one, or several to spread its replicas over — a dropdown of checkboxes, closed it
 * shows the split ("alpha ×2 · beta ×1"). At least one server stays chosen.
 */
export default function ServerPicker({ label, choices, selected, replicas, unknown, disabled, onChange }: {
  label: string
  choices: ServerChoice[]
  selected: number[]
  replicas: number
  /** names in the compose file that are no server */
  unknown: string[]
  disabled: boolean
  onChange: (ids: number[]) => void
}) {
  const ref = useRef<HTMLDetailsElement>(null)
  // a click elsewhere closes it, like a select
  useEffect(() => {
    const close = (e: PointerEvent) => { if (ref.current?.open && !ref.current.contains(e.target as Node)) ref.current.open = false }
    document.addEventListener('pointerdown', close)
    return () => document.removeEventListener('pointerdown', close)
  }, [])

  const chosen = choices.filter(c => selected.includes(c.agent.id))
  const split = shares(replicas, chosen.length)
  const summary = chosen.length <= 1 ? chosen[0]?.agent.name ?? '—' : chosen.map((c, i) => `${c.agent.name} ×${split[i]}`).join(' · ')
  // kept in the order of the list: the first servers take the rest of an uneven split
  const toggle = (id: number, on: boolean) => {
    const next = choices.map(c => c.agent.id).filter(x => x === id ? on : selected.includes(x))
    if (next.length > 0) onChange(next)
  }

  return (
    <details ref={ref} className="server-picker">
      <summary className="cell" aria-label={label} aria-disabled={disabled} onClick={e => { if (disabled) e.preventDefault() }}
        title="the servers this service runs on; with several its replicas are spread over them">
        {summary}{unknown.length > 0 && <span className="error"> + {unknown.join(', ')} (no such server)</span>}
      </summary>
      <div className="server-picker-menu" role="group" aria-label={label}>
        {choices.map(c => (
          <label key={c.agent.id} className={c.disabled ? 'muted' : undefined} title={c.disabled}>
            <input type="checkbox" checked={selected.includes(c.agent.id)} disabled={!!c.disabled && !selected.includes(c.agent.id)}
              onChange={e => toggle(c.agent.id, e.target.checked)} />
            {c.agent.name}{!c.agent.online && <span className="muted"> (offline)</span>}
            {c.disabled && <span className="mini"> — {c.disabled}</span>}
          </label>
        ))}
        <p className="mini">Several servers: the {replicas} replica{replicas === 1 ? '' : 's'} are spread over them, the first ones take the rest.</p>
      </div>
    </details>
  )
}
