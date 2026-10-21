import { useCallback, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, type Agent, type ProjectListItem } from '../lib/api'
import { POLL, usePoll, useStoredState } from '../lib/hooks'
import ProjectTable from '../components/ProjectTable'
import { canAnywhere, useMe } from '../lib/access'

/** Every project, grouped by server, with its containers' state; refreshed every few seconds. */
export default function Projects({ agents }: { agents: Agent[] }) {
  const [projects, setProjects] = useState<ProjectListItem[] | null>(null)
  usePoll(useCallback(() => { api<ProjectListItem[]>('/projects').then(setProjects).catch(() => {}) }, []), POLL.projects)
  // instances of templates come and go by the dozen: apart unless asked for (the Templates page lists them by template)
  const [showInstances, setShowInstances] = useStoredState('dm.projects.instances', false)
  const instances = projects?.filter(p => p.template).length ?? 0
  const shown = projects?.filter(p => showInstances || !p.template) ?? null
  const mayCreate = canAnywhere(useMe(), 'edit')

  return (
    <>
      <div className="toolbar">
        <h1>Projects</h1>
        <div className="spacer" />
        {instances > 0 && (
          <label className="check-inline" title="projects made from templates through the API">
            <input type="checkbox" checked={showInstances} onChange={e => setShowInstances(e.target.checked)} /> {instances} template instance{instances > 1 ? 's' : ''}
          </label>
        )}
        {mayCreate && <Link className="btn primary" to="/projects/new">+ New project</Link>}
      </div>
      {projects && projects.length === 0 && <p className="muted">No projects yet. A project is a docker compose file deployed to one server.</p>}
      {shown && shown.length > 0 && (
        <ProjectTable projects={shown} agents={agents} full />
      )}
    </>
  )
}
