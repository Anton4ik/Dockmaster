import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type RefObject, type SetStateAction } from 'react'

/** The widths (max-width, px) at which the hide-lg / hide-md / hide-sm columns go; the same numbers as in styles/. */
export const BP = { sm: 800, md: 1100, lg: 1400 }

/** How often pages poll the server for what is not pushed live, in ms. */
export const POLL = { projects: 5000, dashboard: 10_000, agent: 10_000, mesh: 3000, meshConnections: 15_000 }

/** Keeps the latest value in a ref, so listeners registered once still call the current callback. */
function useLatest<T>(value: T) {
  const ref = useRef(value)
  useLayoutEffect(() => { ref.current = value })
  return ref
}

/** Calls onEscape when Escape is pressed while `enabled`. */
export function useEscape(onEscape: () => void, enabled = true) {
  const cb = useLatest(onEscape)
  useEffect(() => {
    if (!enabled) return
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') cb.current() }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [enabled, cb])
}

/** Closes a popup on a click outside `ref`, on Escape, and when focus leaves it with Tab. */
export function useDismiss(ref: RefObject<HTMLElement | null>, open: boolean, onClose: () => void) {
  const cb = useLatest(onClose)
  useEscape(onClose, open)
  useEffect(() => {
    if (!open) return
    const outside = (e: Event) => { if (!ref.current?.contains(e.target as Node)) cb.current() }
    document.addEventListener('mousedown', outside)
    document.addEventListener('focusin', outside)
    return () => { document.removeEventListener('mousedown', outside); document.removeEventListener('focusin', outside) }
  }, [open, ref, cb])
}

/** How a stored value is written and read back; keep it a module constant, the setter depends on `stringify`. */
interface Codec<T> { parse: (stored: string) => T; stringify: (value: T) => string }

/**
 * useState remembered in this browser under `key` (JSON unless `codec` says otherwise); no key, no remembering.
 * Blocked storage (private mode) or a stored value that does not parse just means the default.
 */
export function useStoredState<T>(key: string | null, init: T | (() => T), codec: Partial<Codec<T>> = {}) {
  const parse = codec.parse ?? ((s: string) => JSON.parse(s) as T)
  const stringify = codec.stringify ?? JSON.stringify
  const [value, setValue] = useState<T>(() => {
    try {
      const stored = key ? localStorage.getItem(key) : null
      if (stored) return parse(stored)
    } catch { /* storage blocked or not ours: the default */ }
    return typeof init === 'function' ? (init as () => T)() : init
  })
  const set = useCallback((next: SetStateAction<T>) => setValue(prev => {
    const v = typeof next === 'function' ? (next as (p: T) => T)(prev) : next
    if (key) try { localStorage.setItem(key, stringify(v)) } catch { /* not remembered then */ }
    return v
  }), [key, stringify])
  return [value, set] as const
}

/**
 * A set of keys with a toggle, e.g. which groups are expanded, and a setter to replace it; with `storageKey` it is
 * remembered in this browser (as a JSON array).
 */
export function useSetToggle<T>(storageKey: string | null = null) {
  const [list, setList] = useStoredState<T[]>(storageKey, [], LIST)
  const set = useMemo(() => new Set(list), [list])
  const toggle = useCallback((key: T) => setList(prev => prev.includes(key) ? prev.filter(k => k !== key) : [...prev, key]), [setList])
  const replace = useCallback((keys: Iterable<T>) => setList([...keys]), [setList])
  return [set, toggle, replace] as const
}
const LIST = { parse: (s: string) => { const v: unknown = JSON.parse(s); return Array.isArray(v) ? v : [] } }

/** Scrolls an element to the bottom whenever `dep` changes, while `enabled`. */
export function useAutoScroll(ref: RefObject<HTMLElement | null>, dep: unknown, enabled = true) {
  useEffect(() => {
    if (enabled && ref.current) ref.current.scrollTop = ref.current.scrollHeight
  }, [dep, enabled, ref])
}

/**
 * Calls `fn` now and then every `ms` while the tab is visible: a hidden tab skips the ticks. `fn` must be stable
 * (useCallback); when it changes, the polling starts over with a call.
 */
export function usePoll(fn: () => void, ms: number) {
  useEffect(() => {
    fn()
    const t = setInterval(() => { if (!document.hidden) fn() }, ms)
    return () => clearInterval(t)
  }, [fn, ms])
}

/** Whether a CSS media query matches, updated live. */
export function useMedia(query: string) {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches)
  useEffect(() => {
    const m = window.matchMedia(query)
    const on = () => setMatches(m.matches)
    on()
    m.addEventListener('change', on)
    return () => m.removeEventListener('change', on)
  }, [query])
  return matches
}

/** Narrower than the phone breakpoint: pages swap crowded rows for menus and lists. */
export const usePhone = () => useMedia(`(max-width: ${BP.sm}px)`)

/**
 * How many of a table's columns are visible under the hide-lg / hide-md / hide-sm breakpoints. A colSpan wider than
 * the visible columns adds phantom columns and shifts every row, so full-width cells span exactly this.
 */
export function useVisibleColumns(total: number, hidden: { lg?: number; md?: number; sm?: number }) {
  const lg = useMedia(`(max-width: ${BP.lg}px)`), md = useMedia(`(max-width: ${BP.md}px)`), sm = usePhone()
  return total - (lg ? hidden.lg ?? 0 : 0) - (md ? hidden.md ?? 0 : 0) - (sm ? hidden.sm ?? 0 : 0)
}

/**
 * Places a popup next to its button with fixed positioning, so a scrolling table or a clipped cell cannot cut it off:
 * below the button, or above when there is no room below; aligned to the button's `align` edge. Render the popup
 * with `style={HIDDEN_UNTIL_PLACED}`; it is placed before it is painted. It closes on scroll (outside itself) and on
 * resize, where a fixed popup would drift away from its button.
 */
export function useFixedPopover(open: boolean, close: () => void, button: RefObject<HTMLElement | null>, popup: RefObject<HTMLElement | null>, align: 'left' | 'right') {
  const onClose = useLatest(close)

  useLayoutEffect(() => {
    const el = popup.current
    if (!open || !button.current || !el) return
    const b = button.current.getBoundingClientRect()
    const p = el.getBoundingClientRect()
    const below = window.innerHeight - b.bottom
    const up = below < p.height + 8 && b.top > below
    const px = (n: number) => `${Math.round(n)}px`
    // written to the element directly: a measured position needs no extra render
    Object.assign(el.style, {
      position: 'fixed', margin: '0',
      top: up ? 'auto' : px(b.bottom + 2), bottom: up ? px(window.innerHeight - b.top + 2) : 'auto',
      left: align === 'left' ? px(Math.max(4, Math.min(b.left, window.innerWidth - p.width - 4))) : 'auto',
      right: align === 'right' ? px(Math.max(4, window.innerWidth - b.right)) : 'auto',
      visibility: 'visible',
    })
  }, [open, button, popup, align])

  useEffect(() => {
    if (!open) return
    const onScroll = (e: Event) => { if (!popup.current?.contains(e.target as Node)) onClose.current() }
    const onResize = () => onClose.current()
    window.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onResize)
    return () => { window.removeEventListener('scroll', onScroll, true); window.removeEventListener('resize', onResize) }
  }, [open, popup, onClose])
}

export const HIDDEN_UNTIL_PLACED: CSSProperties = { visibility: 'hidden' }
