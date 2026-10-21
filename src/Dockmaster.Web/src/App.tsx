import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { api, type Agent, type Me } from './lib/api'
import { MeContext, useMe } from './lib/access'
import { BP, useStoredState } from './lib/hooks'
import { useAgentUpdates, useReconnect } from './lib/live'
import Icon from './components/Icon'
import { ICON } from './components/icons'
import { OnlineDot } from './components/Status'
import Toasts from './components/Toasts'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import AgentNew from './pages/AgentNew'
import AgentRoute from './pages/agent/AgentPage'
import ImagesPage from './pages/agent/ImagesPage'
import VolumesPage from './pages/agent/VolumesPage'
import DockerNetworksPage from './pages/agent/DockerNetworksPage'
import ContainerRoute from './pages/container/ContainerPage'
import Projects from './pages/Projects'
import Templates from './pages/Templates'
import ProjectRoute from './pages/project/ProjectEdit'
import NetworkPage from './pages/network/NetworkPage'
import Registries from './pages/Registries'
import Settings from './pages/Settings'
import Events from './pages/Events'
import Users from './pages/users/Users'

/** Signs in first (or makes the first admin on the first run), then the app. */
export default function App() {
  const [me, setMe] = useState<Me | null>(null)
  const location = useLocation()

  // asked again when the login page is left or entered: after signing in, or after signing out
  const onLogin = location.pathname === '/login'
  const loadMe = useCallback(() => api<Me>('/auth/me').then(setMe).catch(() => setMe({ authenticated: false, hasPassword: true })), [])
  useEffect(() => { void loadMe() }, [loadMe, onLogin])

  if (!me) return null
  if (onLogin) return <Login me={me} onLogin={loadMe} />
  if (!me.authenticated || !me.user) return <Navigate to={'/login?returnUrl=' + encodeURIComponent(location.pathname)} replace />
  return (
    <MeContext.Provider value={me.user}>
      <Shell reloadMe={loadMe} />
    </MeContext.Provider>
  )
}

// the menu's state is stored as a word, as it always was
const SIDEBAR_CODEC = { parse: (s: string) => s === 'collapsed', stringify: (collapsed: boolean) => collapsed ? 'collapsed' : 'open' }

/** The side menu (pages, then the servers with their state) and the page. Pages only an admin may use are not offered to others. */
function Shell({ reloadMe }: { reloadMe: () => Promise<void> }) {
  const [agents, setAgents] = useState<Agent[]>([])
  // folded by default where the screen is narrow; a choice made once is kept
  const [collapsed, setCollapsed] = useStoredState('dm.sidebar', () => window.innerWidth < BP.md, SIDEBAR_CODEC)
  const me = useMeOrThrow()

  const refreshAgents = useCallback(() => { api<Agent[]>('/agents').then(setAgents).catch(() => {}) }, [])
  useEffect(refreshAgents, [refreshAgents])
  useAgentUpdates(useCallback((a: Agent) => {
    setAgents(prev => {
      const i = prev.findIndex(x => x.id === a.id)
      if (i < 0) return [...prev, a].sort((x, y) => x.name.localeCompare(y.name))
      const next = prev.slice(); next[i] = a; return next
    })
  }, []))
  // the server ends the live connection when the user's access changed: what they may do and see is read again
  useReconnect(useCallback(() => { void reloadMe(); refreshAgents() }, [reloadMe, refreshAgents]))

  const admin = me.isAdmin
  const toggleLabel = collapsed ? 'Expand menu' : 'Collapse menu'
  return (
    <div className="shell">
      <nav className={'sidebar' + (collapsed ? ' mini' : '')}>
        <div className="brand"><span className="logo">⚓</span><span className="label"> Dockmaster</span></div>
        <NavLink to="/" end title="Dashboard"><NavIcon d={ICON.dashboard} /><span className="label">Dashboard</span></NavLink>
        <NavLink to="/projects" title="Projects"><NavIcon d={ICON.projects} /><span className="label">Projects</span></NavLink>
        {admin && <NavLink to="/templates" title="Templates"><NavIcon d={ICON.templates} /><span className="label">Templates</span></NavLink>}
        <NavLink to="/events" title="Events"><NavIcon d={ICON.events} /><span className="label">Events</span></NavLink>
        {admin && <NavLink to="/network" title="Network"><NavIcon d={ICON.network} /><span className="label">Network</span></NavLink>}
        {admin && <NavLink to="/registries" title="Registries"><NavIcon d={ICON.registries} /><span className="label">Registries</span></NavLink>}
        {admin && <NavLink to="/users" title="Users"><NavIcon d={ICON.users} /><span className="label">Users</span></NavLink>}
        <NavLink to="/settings" title="Settings"><NavIcon d={ICON.settings} /><span className="label">Settings</span></NavLink>
        <div className="section"><span className="label">Servers</span></div>
        {agents.map(a => (
          <NavLink key={a.id} to={`/agents/${a.id}`} title={`${a.name} — ${a.online ? 'online' : 'offline'}`} className="agent">
            <OnlineDot online={a.online} /><span className="initials">{initials(a.name)}</span><span className="label">{a.name}</span>
          </NavLink>
        ))}
        {agents.length === 0 && admin && <NavLink to="/agents/new" title="add a server"><NavIcon d={ICON.plus} /><span className="label">add a server</span></NavLink>}
        {agents.length === 0 && !admin && <span className="label muted mini no-servers">no servers given to you yet — ask an admin</span>}
        <button className="collapse" onClick={() => setCollapsed(c => !c)} title={toggleLabel} aria-label={toggleLabel} aria-expanded={!collapsed}>
          <NavIcon d={ICON.panel + (collapsed ? ICON.panelOpen : ICON.panelClose)} />
        </button>
      </nav>
      <main className="content">
        <Routes>
          <Route path="/" element={<Dashboard agents={agents} />} />
          <Route path="/agents/new" element={<AdminOnly><AgentNew onCreated={refreshAgents} /></AdminOnly>} />
          <Route path="/agents/:id" element={<AgentRoute agents={agents} onChanged={refreshAgents} />} />
          <Route path="/agents/:id/images" element={<ImagesPage agents={agents} />} />
          <Route path="/agents/:id/volumes" element={<VolumesPage agents={agents} />} />
          <Route path="/agents/:id/networks" element={<DockerNetworksPage agents={agents} />} />
          <Route path="/agents/:id/containers/:name" element={<ContainerRoute agents={agents} />} />
          <Route path="/projects" element={<Projects agents={agents} />} />
          <Route path="/projects/new" element={<ProjectRoute agents={agents} />} />
          <Route path="/projects/:id" element={<ProjectRoute agents={agents} />} />
          <Route path="/templates" element={<AdminOnly><Templates agents={agents} /></AdminOnly>} />
          <Route path="/registries" element={<AdminOnly><Registries /></AdminOnly>} />
          <Route path="/network" element={<AdminOnly><NetworkPage agents={agents} /></AdminOnly>} />
          <Route path="/users" element={<AdminOnly><Users agents={agents} /></AdminOnly>} />
          <Route path="/settings" element={<Settings />} />
          <Route path="/events" element={<Events />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
      <Toasts />
    </div>
  )
}

function useMeOrThrow() {
  const me = useMe()
  if (!me) throw new Error('signed in, but no user')
  return me
}

/** A page only an admin may use: anyone else is sent to the dashboard (the server refuses them anyway). */
function AdminOnly({ children }: { children: ReactNode }) {
  return useMe()?.isAdmin ? <>{children}</> : <Navigate to="/" replace />
}

const NavIcon = ({ d }: { d: string }) => <Icon className="nav-icon" d={d} />

/** two letters for an agent in the collapsed menu: "InfoHub" → IH, "rate me" → RM, "NAS" → NA */
function initials(name: string) {
  const words = name.trim().split(/[\s_.-]+/).filter(Boolean)
  if (words.length > 1) return (words[0][0] + words[1][0]).toUpperCase()
  const caps = name.match(/[A-Z]/g)
  if (caps && caps.length > 1) return caps.slice(0, 2).join('')
  return name.slice(0, 2).toUpperCase()
}
