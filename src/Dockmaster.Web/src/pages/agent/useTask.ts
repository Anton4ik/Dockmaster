import { useState } from 'react'
import type { Container } from '../../lib/api'
import { containerKey } from '../../lib/fmt'
import { useStreamTask, type StreamResult } from '../../lib/live'

/** A long-running action on a server's containers, as the side drawer shows it. */
export interface AgentTask {
  title: string
  text: string
  /** containerKey()s of the rows involved: they stay highlighted and busy even after re-creation changes their ids */
  keys: string[]
  result?: StreamResult
}

interface Step { label?: string; method: string; args: unknown[] }

/** One long-running action at a time (pull & redeploy, project update) streamed into the side drawer. */
export function useTask(agentId: number) {
  const [task, setTask] = useState<AgentTask | null>(null)
  // leaving the page (or switching agents) stops following the stream; the agent finishes what it started
  const stream = useStreamTask()

  async function run(title: string, cs: Container[], steps: Step[]) {
    setTask({ title, text: '', keys: [...new Set(cs.map(containerKey))] })
    const append = (line: string) => setTask(t => t && { ...t, text: t.text + line + '\n' })
    let result: StreamResult = { ok: true, message: '' }
    for (const step of steps) {
      if (step.label) append(`── ${step.label}`)
      const r = await stream.run(step.method, step.args, append)
      if (r === null) return // replaced by another task, or the page was left
      result = r
      if (step.label) append((result.ok ? '✓ ' : '✗ ') + result.message)
      if (!result.ok) break
    }
    setTask(t => t && { ...t, result })
  }

  /**
   * Pull the newest image and re-create. A Dockmaster project redeploys the whole service (rolling, one replica at
   * a time, nothing replaced when the image did not change); other containers are re-created one after another.
   */
  function redeploy(cs: Container[], label: string, managed: boolean) {
    const what = cs.length > 1 ? `${cs.length} replicas of ${label}` : label
    const how = managed
      ? `Deployed through project "${cs[0].project}"${cs.length > 1 ? ': replicas are replaced one at a time, each waits until the new one is ready' : ''}. If the new image does not come up, the old container keeps running. Nothing is replaced when the image did not change.`
      : `${cs.length > 1 ? 'One at a time: ' : ''}the container is stopped, re-created with the same settings and started. If the new one does not start, the old one comes back. Nothing is done when the image did not change.`
    if (!confirm(`Pull the newest image and re-create ${what}?\n\n${how}`)) return
    void run(`pull & redeploy ${label}`, cs, managed
      ? [{ method: 'RecreateContainer', args: [agentId, cs[0].id, true] }]
      : cs.map(c => ({ label: cs.length > 1 ? c.name : undefined, method: 'RecreateContainer', args: [agentId, c.id, true] })))
  }

  /** One container; in a Dockmaster project (`managed`) a deploy covers the whole service, so all its replicas go. */
  function redeployContainer(c: Container, all: Container[], managed: boolean) {
    if (managed) redeploy(all.filter(x => x.project === c.project && x.service === c.service), c.service, true)
    else redeploy([c], c.name, false)
  }

  /** A whole Dockmaster project: pull everything, deploy service after service, unchanged ones are left alone. */
  function updateProject(projectId: number, name: string, cs: Container[]) {
    if (!confirm(`Update project "${name}"?\n\nPulls the images of all services, then goes through the services one by one. Services whose image and config did not change are left alone; changed ones are replaced replica by replica, each waiting until the new one is ready. If a new version does not come up, the old containers keep running and the update stops there.`)) return
    void run(`update ${name}`, cs, [{ method: 'Deploy', args: [projectId, null, true] }])
  }

  /** Containers the update check marked outdated, re-created one at a time. */
  function updateOutdated(label: string, outdated: Container[]) {
    if (!confirm(`Pull and re-create the ${outdated.length} outdated container(s) of ${label}, one at a time?\n\n${outdated.map(c => c.name).join('\n')}\n\nEach is re-created with the same settings; if the new one does not start, the old one comes back and the update stops.`)) return
    void run(`update ${label}`, outdated, outdated.map(c => ({ label: c.name, method: 'RecreateContainer', args: [agentId, c.id, true] })))
  }

  const running = task != null && !task.result
  const involves = (c: Container) => task != null && task.keys.includes(containerKey(c))
  /** closes a finished task's log (a running one is only hidden by its drawer) */
  const close = () => setTask(null)
  /** stops the running task on the server; its drawer shows the outcome */
  const cancel = () => stream.cancel()

  return { task, running, involves, redeploy, redeployContainer, updateProject, updateOutdated, close, cancel }
}
