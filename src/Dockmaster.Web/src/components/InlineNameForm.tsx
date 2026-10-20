import { useState, type CSSProperties } from 'react'

/** A name edited in place of the title: Enter saves, Escape (or the cancel button, when there is one) gives up. */
export default function InlineNameForm({ initial = '', label, placeholder, saveLabel, cancelLabel, className, inputClassName, inputStyle, onSave, onCancel }: {
  initial?: string
  /** the input's accessible name */
  label: string
  placeholder?: string
  saveLabel: string
  cancelLabel?: string
  className: string
  inputClassName?: string
  inputStyle?: CSSProperties
  onSave: (name: string) => void
  onCancel: () => void
}) {
  const [name, setName] = useState(initial)
  return (
    <form className={className} onSubmit={e => { e.preventDefault(); onSave(name) }}>
      <input className={inputClassName} style={inputStyle} aria-label={label} placeholder={placeholder} value={name} autoFocus
        onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Escape') onCancel() }} />
      <button className="sm primary" type="submit">{saveLabel}</button>
      {cancelLabel && <button className="sm" type="button" onClick={onCancel}>{cancelLabel}</button>}
    </form>
  )
}
