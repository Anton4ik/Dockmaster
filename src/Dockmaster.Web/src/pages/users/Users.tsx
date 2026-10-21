import { useCallback, useEffect, useState } from 'react'
import { api, errorText, type Agent, type PermissionInfo, type Role, type User } from '../../lib/api'
import { useMe } from '../../lib/access'
import { useStoredState } from '../../lib/hooks'
import { Tab, Tabs } from '../../components/Tabs'
import UserList from './UserList'
import RoleList from './RoleList'

type View = 'users' | 'roles'

/**
 * Who signs in and what each may do. An admin may do everything; anyone else gets a role per server (or one role on
 * every server): a role is a set of permissions, made on the Roles tab.
 */
export default function Users({ agents }: { agents: Agent[] }) {
  const me = useMe()
  const [view, setView] = useStoredState<View>('dm.users.view', 'users')
  const [users, setUsers] = useState<User[] | null>(null)
  const [roles, setRoles] = useState<Role[]>([])
  const [permissions, setPermissions] = useState<PermissionInfo[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api<User[]>('/users').then(setUsers).catch(e => setError(errorText(e)))
    api<Role[]>('/roles').then(setRoles).catch(e => setError(errorText(e)))
  }, [])
  useEffect(load, [load])
  useEffect(() => { api<PermissionInfo[]>('/roles/permissions').then(setPermissions).catch(() => {}) }, [])

  return (
    <>
      <div className="toolbar">
        <h1>Users</h1>
        <Tabs>
          <Tab value="users" current={view} onSelect={setView} title="who signs in, and their role on each server">People{users ? ` (${users.length})` : ''}</Tab>
          <Tab value="roles" current={view} onSelect={setView} title="sets of permissions given on servers">Roles ({roles.length})</Tab>
        </Tabs>
      </div>
      {error && <p className="error">{error}</p>}
      {view === 'users'
        ? users && <UserList users={users} roles={roles} agents={agents} meId={me?.id ?? 0} onChanged={load} />
        : <RoleList roles={roles} permissions={permissions} onChanged={load} />}
    </>
  )
}
