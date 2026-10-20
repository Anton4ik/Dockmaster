import type { ReactNode } from 'react'
import { useEscape } from '../lib/hooks'

/** A form in a card over the page, titled `label`. Escape or a click on the backdrop closes it; `wide` for forms with rows of fields. */
export default function Modal({ label, onClose, onSubmit, autoComplete, wide, children }: {
  label: string
  wide?: boolean
  onClose: () => void
  onSubmit: () => void
  autoComplete?: 'off'
  children: ReactNode
}) {
  useEscape(onClose)
  return (
    <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <form className={'modal card form' + (wide ? ' wide' : '')} role="dialog" aria-modal="true" aria-label={label} autoComplete={autoComplete}
        onSubmit={e => { e.preventDefault(); onSubmit() }}>
        <h2>{label}</h2>
        {children}
      </form>
    </div>
  )
}
