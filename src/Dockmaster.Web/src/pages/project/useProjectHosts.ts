import { useCallback, useEffect, useState } from 'react'
import { agentByName, api, type Agent, type AgentDetail, type Container, type ServiceSummary } from '../../lib/api'
import { useProjectContainers } from '../../lib/live'

/**
 * The servers a project's services are placed on (its own server plus every x-dockmaster.agent / agents) and the project's
 * containers on them: pushed live every heartbeat, loaded once up front for the first paint.
 */
export function useProjectHosts(agents: Agent[], agentId: number, services: ServiceSummary[], projectName: string) {
  const [byAgent, setByAgent] = useState<Record<number, Container[]>>({})

  const hostIds = agentId ? [agentId] : []
  for (const name of services.flatMap(s => s.agentNames)) {
    const a = agentByName(agents, name)
    if (a && !hostIds.includes(a.id)) hostIds.push(a.id)
  }
  const hostKey = hostIds.join(',')

  const onContainers = useCallback((id: number, cs: Container[]) => setByAgent(prev => ({ ...prev, [id]: cs })), [])
  useProjectContainers(hostIds, onContainers)
  const fetchHosts = useCallback((ids: number[]) =>
    Promise.all(ids.map(id => api<AgentDetail>(`/agents/${id}`).then(d => onContainers(id, d.containers)))), [onContainers])
  // first paint without waiting for the next heartbeat push
  useEffect(() => { fetchHosts(hostKey ? hostKey.split(',').map(Number) : []).catch(() => {}) }, [hostKey, fetchHosts])

  const containers = projectName ? hostIds.flatMap(id => (byAgent[id] ?? []).filter(c => c.project === projectName).map(c => ({ ...c, agentId: id }))) : []
  return { hostIds, containers, refresh: () => fetchHosts(hostIds) }
}
