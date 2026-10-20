import type { Container } from '../../lib/api'

/** Compose projects (standalone containers last), each with its containers by service in name order. */
export function groupContainers(containers: Container[]) {
  const groups = new Map<string, Container[]>()
  for (const c of containers) {
    const k = c.project || ''
    ;(groups.get(k) ?? groups.set(k, []).get(k)!).push(c)
  }
  return [...groups.keys()]
    .sort((a, b) => (a === '' ? 1 : b === '' ? -1 : a.localeCompare(b)))
    .map(key => {
      const cs = groups.get(key)!.slice().sort((a, b) => a.name.localeCompare(b.name))
      const services = new Map<string, Container[]>()
      for (const c of cs) {
        const s = key && c.service ? c.service : c.id
        ;(services.get(s) ?? services.set(s, []).get(s)!).push(c)
      }
      return { key, containers: cs, services }
    })
}
