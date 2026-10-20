import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, type Agent, type Grant, type Perm, type Role, type User } from '../../lib/api'
import { PERM_LABEL } from '../../lib/access'
import Modal from '../../components/Modal'

const NONE = 0

/**
 * A user: name, password, admin or not, and — for anyone but an admin — a role on every server and/or a role per
 * server. What the chosen roles allow is spelled out under each choice.
 */
export default function UserForm({ user, roles, agents, isMe, onClose, onSaved }: {
  user: User | null; roles: Role[]; agents: Agent[]; isMe: boolean; onClose: () => void; onSaved: () => void
}) {
  const [name, setName] = useState(user?.name ?? '')
  const [password, setPassword] = useState('')
  const [isAdmin, setIsAdmin] = useState(user?.isAdmin ?? false)
  const [disabled, setDisabled] = useState(user?.disabled ?? false)
  // server id (0 = every server) → role id
  const [grants, setGrants] = useState<Record<number, number>>(() => Object.fromEntries((user?.grants ?? []).map(g => [g.agentId, g.roleId])))
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const setRole = (agentId: number, roleId: number) => setGrants(prev => {
    const next = { ...prev }
    if (roleId === NONE) delete next[agentId]
    else next[agentId] = roleId
    return next
  })

  async function save() {
    setError(null); setBusy(true)
    const list: Grant[] = Object.entries(grants).map(([agentId, roleId]) => ({ agentId: Number(agentId), roleId }))
    const body = { name: name.trim(), password: password || null, isAdmin, disabled, grants: list }
    try {
      if (user) await api(`/users/${user.id}`, { method: 'PUT', json: body })
      else await api('/users', { method: 'POST', json: body })
      onSaved()
    } catch (e) { setError(errorText(e)) }
    finally { setBusy(false) }
  }

  const everywhere = roles.find(r => r.id === grants[0])
  return (
    <Modal label={user ? `Edit ${user.name}` : 'Add user'} onClose={onClose} onSubmit={() => { void save() }} autoComplete="off" wide>
      <div className="row">
        <div>
          <label htmlFor="u-name">Name — what they sign in with</label>
          <input id="u-name" value={name} onChange={e => setName(e.target.value)} placeholder="anna" autoCapitalize="none" spellCheck={false} required autoFocus={!user} />
        </div>
        {/* one's own password is changed where the current one is asked for: Settings → Account */}
        {isMe ? (
          <div>
            <div className="field-label">Password</div>
            <p className="muted mini">Your own one is changed in <Link to="/settings">Settings → Account</Link>, which asks for the current one.</p>
          </div>
        ) : (
          <div>
            <label htmlFor="u-password">{user ? <>New password <span className="muted">(empty keeps it; a new one signs them out)</span></> : 'Password (8+ characters)'}</label>
            <input id="u-password" type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password" required={!user} minLength={user ? undefined : 8} />
          </div>
        )}
      </div>
      <label className="check" title={isMe ? 'another admin can take it away from you' : undefined}>
        <input type="checkbox" checked={isAdmin} disabled={isMe} onChange={e => setIsAdmin(e.target.checked)} />
        Admin — may do everything: every server, users, settings, registries, the network, templates, backups
      </label>
      {user && !isMe && (
        <label className="check"><input type="checkbox" checked={disabled} onChange={e => setDisabled(e.target.checked)} /> Disabled — cannot sign in</label>
      )}

      {!isAdmin && (
        <>
          <h3 className="form-section">Access to servers</h3>
          {roles.length === 0 && <p className="error mini">There are no roles: make one on the Roles tab first.</p>}
          <div className="access-grid">
            <GrantRow agentId={0} label="Every server" hint="also servers added later" roles={roles} value={grants[0] ?? NONE} onChange={id => setRole(0, id)} />
            {agents.map(a => (
              <GrantRow key={a.id} agentId={a.id} label={a.name} roles={roles} value={grants[a.id] ?? NONE} onChange={id => setRole(a.id, id)}
                inherited={everywhere} />
            ))}
          </div>
          {Object.keys(grants).length === 0 && <p className="muted mini">No role anywhere: they can sign in, but see no server.</p>}
        </>
      )}
      {error && <p className="error">{error}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy}>{user ? 'Save' : 'Add user'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}

/** One server and the role on it; under it, what that gives (with the every-server role added). */
function GrantRow({ agentId, label, hint, roles, value, onChange, inherited }: {
  agentId: number; label: string; hint?: string; roles: Role[]; value: number; onChange: (roleId: number) => void; inherited?: Role
}) {
  const role = roles.find(r => r.id === value)
  const perms = (Object.keys(PERM_LABEL) as Perm[]).filter(p => inherited?.permissions.includes(p) || role?.permissions.includes(p))
  const id = `grant-${agentId}`
  return (
    <div className="grant-row">
      <label htmlFor={id} className="grant-server">{label}{hint && <span className="muted mini"> — {hint}</span>}</label>
      <select id={id} value={value} onChange={e => onChange(Number(e.target.value))}>
        <option value={NONE}>{inherited ? `— (${inherited.name} from every server)` : '— no access'}</option>
        {roles.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
      </select>
      <span className="muted mini grant-perms">{perms.length > 0 ? 'may: ' + perms.map(p => PERM_LABEL[p]).join(', ') : ''}</span>
    </div>
  )
}
