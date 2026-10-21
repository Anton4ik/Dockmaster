import { Link } from 'react-router-dom'
import type { Agent, Container } from '../../lib/api'
import { counts } from '../../lib/fmt'
import { usePhone } from '../../lib/hooks'
import RowMenu from '../../components/RowMenu'
import { ProjectStatus } from '../../components/Status'

export type ProjectOp = 'restart' | 'stop' | 'start' | 'down'

/**
 * The project's server and name, its state, and its actions: pull & deploy first, the rest beside it or in a menu on a phone.
 * Deleting sits in the "⋯" menu, out of the row of everyday buttons.
 */
export default function ProjectToolbar({ name, isNew, hosts, containers, running, may, onDeploy, onAction, onDelete }: {
  name: string
  /** the project's own server first, then the others its services are placed on */
  hosts: Agent[]
  isNew: boolean
  containers: Container[]
  running: boolean
  /** what the user may do with the project (on every server it is on): the rest is not offered */
  may: { deploy: boolean; control: boolean; edit: boolean }
  onDeploy: (pull: boolean) => void
  onAction: (op: ProjectOp) => void
  /** opens the confirmation; nothing is deleted yet */
  onDelete: () => void
}) {
  const phone = usePhone()
  const del = { label: 'delete project…', danger: true, onClick: onDelete, disabled: running }
  const menu = [
    ...(may.deploy ? [{ label: 'deploy (no pull)', onClick: () => onDeploy(false), disabled: running }] : []),
    ...(may.control ? [
      { label: 'restart', onClick: () => onAction('restart'), disabled: running },
      { label: 'stop', onClick: () => onAction('stop'), disabled: running },
      { label: 'start', onClick: () => onAction('start'), disabled: running },
      { label: 'down', danger: true, onClick: () => onAction('down'), disabled: running },
    ] : []),
    ...(may.edit ? [del] : []),
  ]
  return (
    <div className="toolbar project-toolbar">
      <h1>
        {!isNew && hosts.length > 0 && <>
          <Link to={`/agents/${hosts[0].id}`} title="the project's server">{hosts[0].name}</Link>
          {hosts.length > 1 && <span className="muted project-more-hosts" title={`services also run on ${hosts.slice(1).map(a => a.name).join(', ')}`}>+{hosts.length - 1}</span>}
          <span className="muted">/</span>
        </>}
        <span className="project-name">{isNew ? 'New project' : name}</span>
      </h1>
      {!isNew && <ProjectStatus p={counts(containers)} />}
      <div className="spacer" />
      {!isNew && (
        <>
          {may.deploy && <button className="primary" disabled={running} onClick={() => onDeploy(true)}>Pull &amp; deploy</button>}
          {phone ? (
            // a row of six buttons wraps into a jumble on a phone: the main action stays, the rest go into the menu
            menu.length > 0 && <RowMenu title="project actions" items={menu} />
          ) : (
            <>
              {may.deploy && <button disabled={running} onClick={() => onDeploy(false)}>Deploy (no pull)</button>}
              {may.control && <>
                <button disabled={running} onClick={() => onAction('restart')}>Restart</button>
                <button disabled={running} onClick={() => onAction('stop')}>Stop</button>
                <button disabled={running} onClick={() => onAction('start')}>Start</button>
                <button className="danger" disabled={running} onClick={() => onAction('down')}>Down</button>
              </>}
              {may.edit && <RowMenu title="more project actions" items={[del]} />}
            </>
          )}
        </>
      )}
    </div>
  )
}
