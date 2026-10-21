import { errorText } from './api'

type Kind = 'busy' | 'ok' | 'error'
export interface Toast { id: number; text: string; kind: Kind }

/**
 * Short notices pinned to the corner of the screen, so the outcome of an action is visible wherever the page is
 * scrolled. "busy" stays with a spinner until replaced, "ok" fades after a few seconds, "error" stays until closed.
 */
let toasts: Toast[] = []
let nextId = 1
const listeners = new Set<(t: Toast[]) => void>()
const timers = new Map<number, ReturnType<typeof setTimeout>>()
const OK_FADES_AFTER_MS = 5000
/** older notices beyond this many are dropped */
const MAX_SHOWN = 5

function emit() { listeners.forEach(l => l(toasts)) }

export function dismissToast(id: number) {
  clearTimeout(timers.get(id)); timers.delete(id)
  toasts = toasts.filter(t => t.id !== id)
  emit()
}

/** Shows a notice, or replaces the one with `id`; returns its id. */
export function toast(text: string, kind: Kind = 'ok', id?: number): number {
  const t = { id: id ?? nextId++, text, kind }
  toasts = toasts.some(x => x.id === t.id) ? toasts.map(x => x.id === t.id ? t : x) : [...toasts.slice(-(MAX_SHOWN - 1)), t]
  clearTimeout(timers.get(t.id)); timers.delete(t.id)
  if (kind === 'ok') timers.set(t.id, setTimeout(() => dismissToast(t.id), OK_FADES_AFTER_MS))
  emit()
  return t.id
}

/** What an action that worked says: a text, or a text with whether it went well after all. */
type Outcome = string | { text: string; ok: boolean }

/**
 * Runs `action` behind a busy notice, which then turns into the outcome: `done` (as it is, or made from the result),
 * or the error's message through `failed` (return null there to drop the notice, for an error shown elsewhere).
 * Resolves true when the action worked.
 */
export async function withToast<T>(busy: string, action: () => Promise<T>, done: Outcome | ((result: T) => Outcome),
  failed: (message: string) => string | null = m => m): Promise<boolean> {
  const id = toast(busy, 'busy')
  try {
    const r = await action()
    const o = typeof done === 'function' ? done(r) : done
    if (typeof o === 'string') toast(o, 'ok', id)
    else toast(o.text, o.ok ? 'ok' : 'error', id)
    return true
  } catch (e) {
    const text = failed(errorText(e))
    if (text === null) dismissToast(id)
    else toast(text, 'error', id)
    return false
  }
}

/** Current notices and a subscription, for the <Toasts /> renderer. */
export const currentToasts = () => toasts
export function subscribeToasts(l: (t: Toast[]) => void) { listeners.add(l); return () => { listeners.delete(l) } }
