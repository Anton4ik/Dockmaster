import { useCallback, useEffect, useState } from 'react'
import { api, errorText, type AgentDetail, type Container, type ProjectListItem } from '../../lib/api'
import { POLL, usePoll } from '../../lib/hooks'
import { useContainers } from '../../lib/live'

/**
 * Containers of one agent: loaded once, then pushed live every heartbeat, with a slow poll as a safety net for
 * when the push is not there (server restarting while the page loaded). Also the Dockmaster projects, to tell
 * which compose projects are managed.
 */
export function useAgentData(agentId: number) {
  const [detail, setDetail] = useState<AgentDetail | null>(null)
  const [containers, setContainers] = useState<Container[]>([])
  const [projects, setProjects] = useState<ProjectListItem[]>([])
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    const d = await api<AgentDetail>(`/agents/${agentId}`)
    setDetail(d)
    setContainers(d.containers)
  }, [agentId])

  usePoll(useCallback(() => { refresh().catch(e => setError(errorText(e))) }, [refresh]), POLL.agent)
  useEffect(() => { api<ProjectListItem[]>('/projects').then(setProjects).catch(() => {}) }, [])
  useContainers(agentId, setContainers)

  const projectNamed = useCallback((name: string) => projects.find(p => p.name === name && p.agentId === agentId), [projects, agentId])
  const projectOf = useCallback((c: Container) => c.project && c.service ? projectNamed(c.project) : undefined, [projectNamed])

  return { detail, containers, projectOf, projectNamed, refresh, error }
}
