import { useState } from 'react'
import { api, errorText, type Agent, type Grant, type Role, type User } from '../../lib/api'
import { toast } from '../../lib/toast'
import { Ago } from '../../components/Status'
import RowMenu from '../../components/RowMenu'
import UserForm from './UserForm'

/** Everyone who signs in, with what they may do in a few words; the form adds and changes them. */
export default function UserList({ users, roles, agents, meId, onChanged }: { users: User[]; roles: Role[]; agents: Agent[]; meId: number; onChanged: () => void }) {
  // null: closed; 0: a new user
  const [editing, setEditing] = useState<number | null>(null)

  async function setDisabled(u: User, disabled: boolean) {
    if (disabled && !confirm(`Disable ${u.name}?\n\nThey cannot sign in, and are signed out where they are signed in now. Enable them again any time.`)) return
    try {
      await api(`/users/${u.id}`, { method: 'PUT', json: { name: u.name, isAdmin: u.isAdmin, disabled, grants: u.grants } })
      toast(`${u.name} ${disabled ? 'disabled' : 'enabled'}`)
      onChanged()
    } catch (e) { toast(errorText(e), 'error') }
  }

  async function remove(u: User) {
    if (!confirm(`Delete ${u.name}?\n\nThey are signed out at once. Disabling keeps the account for later instead.`)) return
    try { await api(`/users/${u.id}`, { method: 'DELETE' }); onChanged() }
    catch (e) { toast(errorText(e), 'error') }
  }

  return (
    <>
      <div className="toolbar">
        <p className="muted mini grow">An admin may do everything. Anyone else sees only the servers they have a role on, and does there what the role allows.</p>
        <button className="primary" onClick={() => setEditing(0)}>+ Add user</button>
      </div>
      <div className="table-wrap"><table className="users">
        <thead><tr><th>Name</th><th>Access</th><th className="hide-sm">Last sign-in</th><th></th></tr></thead>
        <tbody>
          {users.map(u => (
            <tr key={u.id} className={u.disabled ? 'dim' : ''}>
              <td className="nowrap">
                <button className="link" onClick={() => setEditing(u.id)}>{u.name}</button>
                {u.id === meId && <span className="badge muted user-tag">you</span>}
                {u.disabled && <span className="badge warn user-tag">disabled</span>}
              </td>
              <td><AccessSummary user={u} roles={roles} agents={agents} /></td>
              <td className="hide-sm"><Ago iso={u.lastLoginAt} /></td>
              <td className="menu-cell">
                <RowMenu title={`actions for ${u.name}`} items={[
                  { label: 'edit', onClick: () => setEditing(u.id) },
                  ...(u.id === meId ? [] : [
                    u.disabled ? { label: 'enable', onClick: () => { void setDisabled(u, false) } } : { label: 'disable', onClick: () => { void setDisabled(u, true) } },
                    { label: 'delete', danger: true, onClick: () => { void remove(u) } },
                  ]),
                ]} />
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
      {editing != null && (
        <UserForm user={users.find(u => u.id === editing) ?? null} roles={roles} agents={agents} isMe={editing === meId}
          onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged() }} />
      )}
    </>
  )
}

/** "Admin", or the user's roles: "Operator on every server", "Viewer on nas"… */
function AccessSummary({ user, roles, agents }: { user: User; roles: Role[]; agents: Agent[] }) {
  if (user.isAdmin) return <span className="badge ok">admin — everything</span>
  if (user.grants.length === 0) return <span className="muted">no access yet</span>
  const roleName = (g: Grant) => roles.find(r => r.id === g.roleId)?.name ?? '?'
  const where = (g: Grant) => g.agentId === 0 ? 'every server' : agents.find(a => a.id === g.agentId)?.name ?? `server #${g.agentId}`
  return (
    <span className="grants">
      {user.grants.map(g => <span key={g.agentId} className="grant"><b>{roleName(g)}</b> on {where(g)}</span>)}
    </span>
  )
}
