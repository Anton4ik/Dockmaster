import { useCallback, useRef, useState } from 'react'
import { api, errorText, type Connection, type Mesh } from '../../lib/api'
import { POLL, usePoll } from '../../lib/hooks'
import { rates as nextRates, sample, type Rates, type Sample } from './model'

/**
 * The mesh and the projects that span it, polled while the tab is visible. The traffic rates come from the byte
 * counters of two answers in a row.
 */
export function useMesh() {
  const [mesh, setMesh] = useState<Mesh | null>(null)
  const [connections, setConnections] = useState<Connection[]>([])
  const [error, setError] = useState<string | null>(null)
  const [rates, setRates] = useState<Rates>({})
  const prevSample = useRef<Sample | null>(null)

  /** Takes a fresh mesh: from a poll, or the answer to a change. */
  const apply = useCallback((m: Mesh) => {
    setMesh(m)
    setError(null)
    const s = sample(m, Date.now())
    setRates(prev => nextRates(prevSample.current, s, prev))
    prevSample.current = s
  }, [])

  const load = useCallback(() => { api<Mesh>('/mesh').then(apply).catch(e => setError(errorText(e))) }, [apply])
  const loadConnections = useCallback(() => { api<Connection[]>('/mesh/connections').then(setConnections).catch(() => {}) }, [])
  usePoll(load, POLL.mesh)
  usePoll(loadConnections, POLL.meshConnections)

  return { mesh, connections, rates, error, apply, load, loadConnections }
}
