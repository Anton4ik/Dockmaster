import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { agentOutdated, api, errorText, type Agent, type Perm } from '../../lib/api'
import { allowed, useCan, useMe } from '../../lib/access'
import { bytes } from '../../lib/fmt'
import { toast } from '../../lib/toast'
import InlineNameForm from '../../components/InlineNameForm'
import RowMenu from '../../components/RowMenu'
import { OnlineBadge } from '../../components/Status'

/**
 * Name with online state, the server menu (rename, update, remove) and the host facts. Outcomes go to the corner of
 * the screen: the container table is long and the top of the page is often scrolled away.
 */
export default function AgentHeader({ agent, onChanged }: { agent: Agent; onChanged: () => void }) {
  const navigate = useNavigate()
  const [renaming, setRenaming] = useState(false)
  const [updating, setUpdating] = useState(false)
  const can = useCan()
  const admin = useMe()?.isAdmin ?? false
  const host = can(agent.id, 'server')

  async function update() {
    if (!confirm(`Update agent "${agent.name}"?\n\nIt pulls its image, replaces its own container and reconnects in a few seconds. Running containers are not touched.`)) return
    setUpdating(true)
    try {
      const r = await api<{ message: string }>(`/agents/${agent.id}/update`, { method: 'POST' })
      toast(`update: ${r.message}`)
    } catch (e) { toast(errorText(e), 'error') }
    finally { setUpdating(false) }
  }

  async function rename(input: string) {
    const name = input.trim()
    if (!name || name === agent.name) { setRenaming(false); return }
    try { await api(`/agents/${agent.id}`, { method: 'PUT', json: { name } }); setRenaming(false); onChanged() }
    catch (e) { toast(errorText(e), 'error') }
  }

  async function remove() {
    if (!confirm(`Remove server "${agent.name}" from Dockmaster?\n\nThe agent container on it keeps running; stop it there.`)) return
    try { await api(`/agents/${agent.id}`, { method: 'DELETE' }); onChanged(); navigate('/') }
    catch (e) { toast(errorText(e), 'error') }
  }

  const legacy = agent.agentVersion === '1.0.0'
  return (
    <div className="toolbar">
      {renaming ? (
        <InlineNameForm initial={agent.name} label="server name" className="actions" inputStyle={{ width: 240 }}
          saveLabel="save" cancelLabel="cancel" onSave={name => { void rename(name) }} onCancel={() => setRenaming(false)} />
      ) : (
        <>
          <h1>{agent.name}<OnlineBadge online={agent.online} /></h1>
          {host && <RowMenu title="server actions" items={allowed([
            { label: 'rename', need: 'server' as Perm, onClick: () => setRenaming(true) },
            { label: updating ? 'updating…' : 'update agent', need: 'server' as Perm, onClick: update, disabled: !agent.online || updating },
            ...(admin ? [{ label: 'remove server', danger: true, onClick: remove }] : []),
          ], perm => can(agent.id, perm))} />}
        </>
      )}
      <span className="host-facts">
        <span className="resource" title="CPU cores">{agent.cpus} CPU</span>
        <span className="resource" title="memory">{agent.memoryTotal > 0 ? bytes(agent.memoryTotal) : '—'}</span>
        <span className="muted">{agent.hostname} · {agent.agentIp} · {agent.os} {agent.arch} · docker {agent.dockerVersion} · agent {agent.agentVersion}</span>
      </span>
      {agentOutdated(agent) && (
        <span className="badge warn" title={legacy ? 'built before self-update existed — update it once by hand' : 'a newer agent image is in the registry'}>
          outdated{agent.online && !legacy && host && <> · <button className="link inherit" disabled={updating} onClick={update}>{updating ? 'updating…' : 'update'}</button></>}
        </span>
      )}
    </div>
  )
}
