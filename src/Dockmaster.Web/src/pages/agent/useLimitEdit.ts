import { useState } from 'react'
import { api, type Container } from '../../lib/api'
import { useCan } from '../../lib/access'
import { composeMem } from '../../lib/fmt'
import { withToast } from '../../lib/toast'

type Field = 'memory' | 'cpus'
export interface LimitEdit { id: string; field: Field; value: string; error?: string }
export type LimitEditor = ReturnType<typeof useLimitEdit>

const currentLimit = (c: Container, field: Field) => field === 'memory' ? composeMem(c.memoryLimit) : c.cpuLimit > 0 ? String(c.cpuLimit) : ''
const fieldName = (field: Field) => field === 'memory' ? 'memory' : 'CPU'

/** A running container's memory / CPU limit changed in place, live; one cell at a time. */
export function useLimitEdit(agentId: number) {
  const [edit, setEdit] = useState<LimitEdit | null>(null)
  // limits are written into the project's compose file: editing projects on the server
  const editable = useCan()(agentId, 'edit')

  async function save(c: Container, e: LimitEdit) {
    const value = e.value.trim()
    if (value === currentLimit(c, e.field)) { setEdit(null); return }
    if (value === '') {
      // docker cannot drop a limit live; the server removes it from the compose file and redeploys the service
      if (!c.project || !c.service) { setEdit({ ...e, error: 'not a Dockmaster project — re-create the container without the limit yourself' }); return }
      if (!confirm(`Docker cannot remove a limit from a running container.\n\nRemove the ${fieldName(e.field)} limit from the compose file of "${c.project}" and redeploy "${c.service}" now?`)) return
    }
    const ok = await withToast(`${c.name}: ${value === '' ? 'removing the limit, redeploying' : `setting the ${fieldName(e.field)} limit to ${value}`}…`,
      () => api<{ message: string }>(`/agents/${agentId}/containers/${c.id}/limits`, { method: 'POST', json: { [e.field]: value } }),
      r => `${c.name}: ${r.message}`,
      // the field stays open and turns red, with the reason under it
      message => { setEdit(cur => cur && cur.id === e.id && cur.field === e.field ? { ...cur, error: message } : cur); return null })
    if (ok) setEdit(null)
  }

  return {
    /** the user may change limits here; otherwise the cells only show them */
    editable,
    /** the edit of this cell, if it is the one being edited */
    of: (c: Container, field: Field) => edit?.id === c.id && edit.field === field ? edit : null,
    start: (c: Container, field: Field) => setEdit({ id: c.id, field, value: currentLimit(c, field) }),
    change: (e: LimitEdit) => setEdit(e),
    cancel: () => setEdit(null),
    save,
  }
}
