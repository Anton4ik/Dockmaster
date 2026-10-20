// What the signed-in user may do: the app asks before it shows a button; the server checks again on every call.
import { createContext, useCallback, useContext } from 'react'
import type { MeUser, Perm } from './api'

export const MeContext = createContext<MeUser | null>(null)

/** The signed-in user (null on the sign-in page). */
export const useMe = () => useContext(MeContext)

/** An admin may do everything; anyone else what their role on every server and their role on this server give. */
export function can(me: MeUser | null, agentId: number | null | undefined, perm: Perm) {
  if (!me) return false
  if (me.isAdmin || me.everywhere.includes(perm)) return true
  return agentId != null && (me.servers[agentId]?.includes(perm) ?? false)
}

/** can() for the signed-in user. */
export function useCan() {
  const me = useMe()
  return useCallback((agentId: number | null | undefined, perm: Perm) => can(me, agentId, perm), [me])
}

/** Menu items the user may use: the ones that need a permission they lack are left out. */
export const allowed = <T extends { need?: Perm }>(items: T[], may: (perm: Perm) => boolean) => items.filter(i => !i.need || may(i.need))

/** May do it on some server (e.g. make a project: somewhere to put it). */
export const canAnywhere = (me: MeUser | null, perm: Perm) =>
  !!me && (me.isAdmin || me.everywhere.includes(perm) || Object.values(me.servers).some(p => p.includes(perm)))

/** May do it on every one of the servers (a project that spans several needs it on each). */
export const canAll = (me: MeUser | null, agentIds: number[], perm: Perm) => agentIds.length > 0 && agentIds.every(id => can(me, id, perm))

/** Short words for a permission (the Roles tab has the longer descriptions, from the server). */
export const PERM_LABEL: Record<Perm, string> = {
  view: 'view', control: 'start & stop', deploy: 'deploy', edit: 'edit projects', secrets: 'secrets', terminal: 'terminal', volumes: 'volumes', server: 'server',
}
