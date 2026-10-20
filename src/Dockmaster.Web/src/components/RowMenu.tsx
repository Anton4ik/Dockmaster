import { useEffect, useRef, useState } from 'react'
import { HIDDEN_UNTIL_PLACED, useDismiss, useFixedPopover } from '../lib/hooks'

interface MenuItem { label: string; onClick: () => void; danger?: boolean; disabled?: boolean; title?: string }

/**
 * "⋯" button with a small dropdown of row actions. Closes on outside click, Escape, Tab away or scroll; arrow keys move
 * between items and focus returns to the button when it closes. The list is fixed to the viewport so a scrolling table
 * cannot clip it, and opens upwards when there is no room below.
 */
export default function RowMenu({ items, title = 'actions' }: { items: MenuItem[]; title?: string }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLSpanElement>(null)
  const button = useRef<HTMLButtonElement>(null)
  const list = useRef<HTMLDivElement>(null)
  const close = (refocus = true) => { setOpen(false); if (refocus) button.current?.focus() }
  useDismiss(ref, open, () => close(false))
  useFixedPopover(open, () => setOpen(false), button, list, 'right')

  useEffect(() => {
    if (open) list.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus({ preventScroll: true })
  }, [open])

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { e.stopPropagation(); close() }
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
    e.preventDefault()
    const buttons = [...(ref.current?.querySelectorAll<HTMLButtonElement>('.menu-list button:not(:disabled)') ?? [])]
    const i = buttons.indexOf(document.activeElement as HTMLButtonElement)
    buttons[(i + (e.key === 'ArrowDown' ? 1 : buttons.length - 1)) % buttons.length]?.focus()
  }

  return (
    <span className="menu" ref={ref} onKeyDown={onKey}>
      <button ref={button} className="sm menu-btn" title={title} aria-label={title} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(o => !o)}>⋯</button>
      {open && (
        <div className="menu-list" role="menu" ref={list} style={HIDDEN_UNTIL_PLACED}>
          {items.map(it => (
            <button key={it.label} role="menuitem" className={it.danger ? 'danger' : ''} disabled={it.disabled} title={it.title}
              onClick={() => { close(); it.onClick() }}>{it.label}</button>
          ))}
        </div>
      )}
    </span>
  )
}
