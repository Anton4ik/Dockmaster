import { Link } from 'react-router-dom'
import type { Agent, ProjectListItem } from '../lib/api'
import { useSetToggle } from '../lib/hooks'
import { Ago, NameList, OnlineDot, ProjectStatus } from './Status'

/**
 * Projects grouped by the server they run on: the same compose project name often exists on several servers, so a
 * flat list reads as duplicates. Each server row folds; the folded set is remembered in this browser. `full` adds the
 * services and last-edit columns (the Projects page), the dashboard shows the short form.
 */
export default function ProjectTable({ projects, agents, full = false }: { projects: ProjectListItem[]; agents: Agent[]; full?: boolean }) {
  const [collapsed, toggle] = useSetToggle<number>('dockmaster.projects.collapsed')

  const groups = new Map<number, ProjectListItem[]>()
  for (const p of projects) groups.set(p.agentId, [...(groups.get(p.agentId) ?? []), p])
  const ordered = [...groups].map(([id, ps]) => ({ id, name: ps[0].agentName, online: agents.find(a => a.id === id)?.online, ps }))
    .sort((a, b) => a.name.localeCompare(b.name))

  const sum = (ps: ProjectListItem[], f: (p: ProjectListItem) => number) => ps.reduce((n, p) => n + f(p), 0)

  return (
    <div className="table-wrap"><table>
      <thead><tr>
        <th>Project</th>{full && <th className="hide-sm">Services</th>}<th>Containers</th>{full && <th className="hide-md">Updated</th>}<th>Last deploy</th>
      </tr></thead>
      <tbody>
        {ordered.map(g => {
          const open = !collapsed.has(g.id)
          const total = { running: sum(g.ps, p => p.running), total: sum(g.ps, p => p.total), problems: sum(g.ps, p => p.problems), updates: sum(g.ps, p => p.updates), jobs: sum(g.ps, p => p.jobs ?? 0) }
          return [
            // cells mirror the project rows (no colSpan), so the hide-* columns line up at every width
            // the name opens the server page; the chevron or anywhere else on the row folds it
            <tr className="group foldable" key={'g' + g.id} onClick={e => { if (!(e.target as HTMLElement).closest('a')) toggle(g.id) }}>
              <td className="group-label">
                <button className="link chev" aria-expanded={open} aria-label={`${open ? 'collapse' : 'expand'} ${g.name}`} title={open ? 'collapse' : 'show its projects'}>{open ? '▾' : '▸'}</button>
                {g.online !== undefined && <OnlineDot online={g.online} title={g.online ? 'online' : 'offline'} />}
                <Link to={`/agents/${g.id}`} title="open the server page">{g.name}</Link>
                <span className="group-count">{g.ps.length} project{g.ps.length > 1 ? 's' : ''}</span>
              </td>
              {full && <td className="hide-sm" />}
              <td className="status">{!open && <ProjectStatus p={total} />}</td>
              {full && <td className="hide-md" />}
              <td />
            </tr>,
            ...(open ? g.ps.map(p => (
              <tr key={p.id} className="in-group">
                <td className="nowrap"><Link to={`/projects/${p.id}`}>{p.name}</Link>{p.servers.length > 1 && <span className="mini" title="services of this project also run on these servers"> + {p.servers.slice(1).join(', ')}</span>}</td>
                {full && <td className="trunc hide-sm"><NameList names={p.services} /></td>}
                <td className="status"><ProjectStatus p={p} /></td>
                {full && <td className="hide-md"><Ago iso={p.updatedAt} /></td>}
                <td><Ago iso={p.lastDeployedAt} suffix={p.lastDeployOk === false ? '✗' : p.lastDeployOk ? '✓' : ''} /></td>
              </tr>
            )) : []),
          ]
        })}
      </tbody>
    </table></div>
  )
}
