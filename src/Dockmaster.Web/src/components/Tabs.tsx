import type { ReactNode } from 'react'

/** A row of tabs under a page's title. */
export function Tabs({ children }: { children: ReactNode }) {
  return <div className="tabs" role="tablist">{children}</div>
}

/** One tab: `value` is what it opens, `current` the open one; `title` says more on hover. */
export function Tab<T extends string>({ value, current, onSelect, title, children }: { value: T; current: T; onSelect: (value: T) => void; title?: string; children: ReactNode }) {
  const selected = value === current
  return <button role="tab" aria-selected={selected} className={selected ? 'active' : undefined} title={title} onClick={() => onSelect(value)}>{children}</button>
}
