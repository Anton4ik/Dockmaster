import { useState } from 'react'
import { api, errorText, type Perm, type PermissionInfo, type Role } from '../../lib/api'
import { PERM_LABEL } from '../../lib/access'
import { toast } from '../../lib/toast'
import Modal from '../../components/Modal'
import RowMenu from '../../components/RowMenu'

/** The roles as a table of what each allows (a column per permission), and the form to make or change one. */
export default function RoleList({ roles, permissions, onChanged }: { roles: Role[]; permissions: PermissionInfo[]; onChanged: () => void }) {
  // null: closed; 0: a new role
  const [editing, setEditing] = useState<number | null>(null)
  const keys = (permissions.length > 0 ? permissions.map(p => p.key) : Object.keys(PERM_LABEL)) as Perm[]

  async function remove(r: Role) {
    if (!confirm(`Delete the role ${r.name}?`)) return
    try { await api(`/roles/${r.id}`, { method: 'DELETE' }); onChanged() }
    catch (e) { toast(errorText(e), 'error') }
  }

  return (
    <>
      <div className="toolbar">
        <p className="muted mini grow">A role is what someone may do on a server. Changing a role changes it for everyone who has it, at once.</p>
        <button className="primary" onClick={() => setEditing(0)}>+ Add role</button>
      </div>
      <div className="table-wrap"><table className="roles">
        <thead>
          <tr>
            <th>Role</th>
            {keys.map(k => <th key={k} className="perm-col" title={permissions.find(p => p.key === k)?.description}>{PERM_LABEL[k]}</th>)}
            <th className="hide-sm">Given to</th><th></th>
          </tr>
        </thead>
        <tbody>
          {roles.map(r => (
            <tr key={r.id}>
              <td>
                <button className="link" onClick={() => setEditing(r.id)}>{r.name}</button>
                {r.description && <div className="muted mini">{r.description}</div>}
              </td>
              {keys.map(k => (
                <td key={k} className="perm-col" aria-label={`${PERM_LABEL[k]}: ${r.permissions.includes(k) ? 'yes' : 'no'}`}>
                  {r.permissions.includes(k) ? <span className="perm-yes">✓</span> : <span className="perm-no">·</span>}
                </td>
              ))}
              <td className="muted hide-sm">{r.users.length > 0 ? r.users.join(', ') : '—'}</td>
              <td className="menu-cell">
                <RowMenu title={`actions for ${r.name}`} items={[
                  { label: 'edit', onClick: () => setEditing(r.id) },
                  { label: 'delete', danger: true, onClick: () => { void remove(r) }, disabled: r.users.length > 0, title: r.users.length > 0 ? 'given to someone: give them another role first' : undefined },
                ]} />
              </td>
            </tr>
          ))}
          {roles.length === 0 && <tr><td colSpan={keys.length + 3} className="muted">No roles. Add one to give people access to servers.</td></tr>}
        </tbody>
      </table></div>
      {editing != null && (
        <RoleForm role={roles.find(r => r.id === editing) ?? null} permissions={permissions} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged() }} />
      )}
    </>
  )
}

function RoleForm({ role, permissions, onClose, onSaved }: { role: Role | null; permissions: PermissionInfo[]; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(role?.name ?? '')
  const [description, setDescription] = useState(role?.description ?? '')
  const [picked, setPicked] = useState<Perm[]>(role?.permissions ?? ['view'])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save() {
    // a second submit while the first is on its way would be told the role exists already
    if (busy) return
    setBusy(true)
    setError(null)
    const body = { name: name.trim(), description, permissions: picked }
    try {
      if (role) await api(`/roles/${role.id}`, { method: 'PUT', json: body })
      else await api('/roles', { method: 'POST', json: body })
      onSaved()
    } catch (e) { setError(errorText(e)) }
    finally { setBusy(false) }
  }

  return (
    <Modal label={role ? `Edit role ${role.name}` : 'Add role'} onClose={onClose} onSubmit={() => { void save() }} autoComplete="off" wide>
      <div className="row">
        <div><label htmlFor="r-name">Name</label><input id="r-name" value={name} onChange={e => setName(e.target.value)} placeholder="Support" required autoFocus={!role} /></div>
        <div><label htmlFor="r-desc">Description <span className="muted">(optional)</span></label><input id="r-desc" value={description} onChange={e => setDescription(e.target.value)} placeholder="Restarts things at night" /></div>
      </div>
      <div className="field-label">May</div>
      <div className="perm-list">
        {permissions.map(p => (
          <label key={p.key} className="perm-option">
            <input type="checkbox" checked={p.key === 'view' || picked.includes(p.key)} disabled={p.key === 'view'}
              onChange={e => setPicked(e.target.checked ? [...picked, p.key] : picked.filter(x => x !== p.key))} />
            <span><b>{p.label}</b><span className="muted mini"> — {p.key === 'view' ? p.description + ' (always: comes with any role)' : p.description}</span></span>
          </label>
        ))}
      </div>
      {role && role.users.length > 0 && <p className="muted mini">Given to {role.users.join(', ')}: a change applies to them at once.</p>}
      {error && <p className="error">{error}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="submit" disabled={busy}>{role ? 'Save' : 'Add role'}</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}
