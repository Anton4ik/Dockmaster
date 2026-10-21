import { NavLink } from 'react-router-dom'

/** What a server has, one page each: its containers, the images they come from, the volumes that keep their data. */
export default function ServerNav({ agentId }: { agentId: number }) {
  return (
    <nav className="tabs subnav" aria-label="server">
      <NavLink end to={`/agents/${agentId}`}>Containers</NavLink>
      <NavLink to={`/agents/${agentId}/images`} title="docker images on this server: sizes, unused ones, clean-up">Images</NavLink>
      <NavLink to={`/agents/${agentId}/volumes`} title="where the containers keep their data: sizes, who uses what, new volumes and network shares">Volumes</NavLink>
      <NavLink to={`/agents/${agentId}/networks`} title="docker networks on this server: which containers reach each other">Docker networks</NavLink>
    </nav>
  )
}
