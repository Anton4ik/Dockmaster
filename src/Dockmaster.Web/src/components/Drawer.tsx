import { useRef, type ReactNode } from 'react'
import { useAutoScroll, useEscape } from '../lib/hooks'

/**
 * Side panel with a title and monospace output (deploy progress), or its own body (a container's LogView) as children.
 * Escape closes it.
 */
export default function Drawer({ title, icon, actions, text = '', footer, follow = true, onClose, closeLabel = 'close', children }: {
  title: string
  icon?: ReactNode
  actions?: ReactNode
  text?: string
  footer?: ReactNode
  follow?: boolean
  onClose: () => void
  closeLabel?: string
  children?: ReactNode
}) {
  const pre = useRef<HTMLPreElement>(null)
  useAutoScroll(pre, text + (footer ? '1' : ''), follow)
  useEscape(onClose)
  return (
    <aside className="drawer" aria-label={title}>
      <header>
        <h2 title={title}>{icon}{title}</h2>
        {actions}
        <button className="sm" onClick={onClose}>{closeLabel}</button>
      </header>
      {children ?? <pre className="log" ref={pre}>{text || '…'}{footer}</pre>}
    </aside>
  )
}
