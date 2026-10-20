import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { api, errorText, type Agent, type Volume, type VolumeList } from '../../lib/api'
import { useCan } from '../../lib/access'
import { bytes } from '../../lib/fmt'
import { useSetToggle } from '../../lib/hooks'
import { toast, withToast } from '../../lib/toast'
import RowMenu from '../../components/RowMenu'
import { Tab, Tabs } from '../../components/Tabs'
import NewVolume from './NewVolume'
import ServerNav from './ServerNav'
import { BrowseVolume, CopyVolume, UploadVolume } from './VolumeTools'

interface RemoveResult { removed: string[]; failed: Record<string, string> }
interface ProjectRef { id: number; name: string; agentId: number; servers: string[] }

type Show = 'used' | 'unused' | 'unnamed' | 'all'
const CONFIRM_NAMES = 12

/**
 * The docker volumes of one server: where the containers keep their data, how big each is, who uses it. New volumes
 * (on this server or a share of a NAS) and removing the ones nothing uses. Sizes are measured after the list shows:
 * that reads every volume on the disk.
 */
export default function VolumesPage({ agents }: { agents: Agent[] }) {
  const id = Number(useParams().id)
  // what is in volumes, and making and removing them: the Volumes permission
  const mayVolumes = useCan()(id, 'volumes')
  const agent = agents.find(a => a.id === id)
  const [search, setSearch] = useSearchParams()
  const [volumes, setVolumes] = useState<Volume[] | null>(null)
  const [sizes, setSizes] = useState<Record<string, number> | null>(null)
  const [projects, setProjects] = useState<ProjectRef[]>([])
  const [show, setShow] = useState<Show>(search.get('q') ? 'all' : 'used')
  const [filter, setFilter] = useState(search.get('q') ?? '')
  const [selected, toggle, setSelected] = useSetToggle<string>()
  const [creating, setCreating] = useState(false)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [tool, setTool] = useState<{ kind: 'browse' | 'upload' | 'copy'; volume: Volume } | null>(null)

  const load = useCallback(() => api<VolumeList>(`/agents/${id}/volumes`).then(r => {
    setVolumes(r.volumes); setError(null)
    // then the sizes, which take a while
    setSizes(null)
    api<VolumeList>(`/agents/${id}/volumes?sizes=true`).then(s => setSizes(Object.fromEntries(s.volumes.map(v => [v.name, v.size]))), () => setSizes({}))
  }, e => setError(errorText(e))), [id])
  useEffect(() => { void load() }, [load])
  // which project a volume belongs to, for a link
  useEffect(() => { api<ProjectRef[]>('/projects').then(list => setProjects(list), () => {}) }, [])

  const all = useMemo(() => volumes ?? [], [volumes])
  const unused = all.filter(v => v.containers.length === 0)
  const used = all.filter(v => v.containers.length > 0)
  const unnamed = all.filter(v => v.anonymous)
  const q = filter.trim().toLowerCase()
  const list = (show === 'all' ? all : show === 'used' ? used : show === 'unused' ? unused : unnamed)
    .filter(v => !q || v.name.toLowerCase().includes(q) || v.project.toLowerCase().includes(q) || v.containers.some(c => c.name.toLowerCase().includes(q)))
  const sizeOf = (v: Volume) => sizes?.[v.name] ?? -1
  const sum = (xs: Volume[]) => xs.reduce((n, v) => n + Math.max(0, sizeOf(v)), 0)
  const chosen = list.filter(v => selected.has(v.name))
  // a project's volume nothing uses may still be its data (the project is stopped, or a service moved): never in bulk
  const selectable = list.filter(v => v.containers.length === 0 && !v.project)
  const projectData = list.filter(v => v.containers.length === 0 && v.project)
  const allChosen = selectable.length > 0 && selectable.every(v => selected.has(v.name))

  const projectOf = (v: Volume) => v.project ? projects.find(p => p.name === v.project && (p.agentId === id || p.servers.includes(agent?.name ?? ''))) : undefined

  async function remove(targets: Volume[]) {
    if (targets.length === 0) return
    const names = targets.slice(0, CONFIRM_NAMES).map(v => v.name + (sizeOf(v) > 0 ? ` (${bytes(sizeOf(v))})` : '')).join('\n') + (targets.length > CONFIRM_NAMES ? `\n… and ${targets.length - CONFIRM_NAMES} more` : '')
    if (!confirm(`Remove ${targets.length} volume(s)?\n\n${names}\n\nThe files in them are DELETED FOR GOOD — there is no undo. A share of a NAS only loses its settings here, the files on the NAS stay.`)) return
    setBusy(true)
    setRemoving(new Set(targets.map(v => v.name)))
    const ok = await withToast(`removing ${targets.length} volume${targets.length > 1 ? 's' : ''}…`,
      () => api<RemoveResult>(`/agents/${id}/volumes/remove`, { method: 'POST', json: { names: targets.map(v => v.name) } }),
      r => {
        const failed = Object.entries(r.failed)
        return { text: `removed ${r.removed.length} volume(s)` + (failed.length ? ` — ${failed.length} could not be removed: ${failed.map(([k, v]) => `${k} (${v})`).join('; ')}` : ''), ok: failed.length === 0 }
      })
    if (ok) { setSelected([]); await load() }
    setBusy(false); setRemoving(new Set())
  }

  const pick = (value: Show) => { setShow(value); setSelected([]) }
  const tab = (value: Show, label: string, xs: Volume[], title: string) => (
    <Tab value={value} current={show} onSelect={pick}>
      <span title={title}>{label}</span> <span className="tab-count">{xs.length}</span>
      {sizes && <span className="muted tab-stat" title="their size on disk">{bytes(sum(xs))}</span>}
    </Tab>
  )

  return (
    <>
      <div className="toolbar">
        <h1><Link to={`/agents/${id}`}>{agent?.name ?? `server ${id}`}</Link><span className="muted"> / volumes</span></h1>
        <button className="sm" onClick={() => { void load() }} disabled={busy}>refresh</button>
        {mayVolumes && <button className="sm primary" onClick={() => setCreating(true)} disabled={!agent?.online}>+ new volume</button>}
      </div>
      <ServerNav agentId={id} />
      <p className="muted mini">A volume is a folder Docker keeps for containers: what a container writes there stays when the container is updated or re-created. Give a service one in its project (<b>Services → Data</b>). Volumes a container uses, even a stopped one, cannot be removed.</p>
      {error && <p className="error">{error}</p>}
      {volumes === null ? (!error && <p className="muted">Loading…</p>) : (
        <>
          <Tabs>
            {tab('used', 'in use', used, 'mounted by a container (running or stopped)')}
            {tab('unused', 'unused', unused, 'no container mounts them: left over from removed apps, or made in advance')}
            {tab('unnamed', 'unnamed', unnamed, 'made by docker for an image\'s data folder nobody named: a re-created container gets a new, empty one')}
            {tab('all', 'all', all, 'every volume')}
          </Tabs>
          <div className="filters">
            <input type="search" aria-label="filter volumes" placeholder="filter by name, project, container" value={filter}
              onChange={e => { setFilter(e.target.value); if (search.get('q')) setSearch({}, { replace: true }) }} />
            {mayVolumes && <button className="sm danger" disabled={busy || chosen.length === 0} onClick={() => { void remove(chosen) }}>
              {busy ? 'removing…' : chosen.length ? `remove ${chosen.length} selected` : 'remove selected'}
            </button>}
            {mayVolumes && show === 'unused' && selectable.length > 0 && (
              <button className="sm" disabled={busy} onClick={() => { void remove(selectable) }}>remove all {selectable.length} unused</button>
            )}
            {!sizes && <span className="mini"><span className="spinner inline" />measuring sizes…</span>}
          </div>
          {show === 'unused' && projectData.length > 0 && (
            <p className="mini">{projectData.length} unused volume{projectData.length > 1 ? 's' : ''} of a project {projectData.length > 1 ? 'are' : 'is'} left out of "remove all" and "select all": a stopped project, or a service moved to another server, still has its data there. Tick one to remove it.</p>
          )}
          {show === 'unnamed' && unnamed.length > 0 && (
            <p className="mini warn-text">Unnamed volumes hold data of an image that nobody gave a name. Most are leftovers; before removing one, check in its container what it holds.</p>
          )}
          <div className="table-wrap">
            <table>
              <thead><tr>
                <th className="check"><input type="checkbox" aria-label="select all" checked={allChosen} disabled={selectable.length === 0}
                  onChange={() => setSelected(allChosen ? [] : selectable.map(v => v.name))} /></th>
                <th>Volume</th><th className="num">Size</th><th>Used by</th><th className="hide-sm">Created</th><th />
              </tr></thead>
              <tbody>
                {list.map(v => {
                  const project = projectOf(v)
                  const size = sizeOf(v)
                  return (
                    <tr key={v.name} className={(selected.has(v.name) ? 'selected' : '') + (removing.has(v.name) ? ' removing' : '')}>
                      <td className="check">
                        {removing.has(v.name) ? <span className="spinner" aria-label="removing" /> : (
                          <input type="checkbox" aria-label={`select ${v.name}`} checked={selected.has(v.name)} disabled={v.containers.length > 0}
                            title={v.containers.length > 0 ? 'in use' : undefined} onChange={() => toggle(v.name)} />
                        )}
                      </td>
                      <td className="volume-name">
                        <span className={v.anonymous ? 'muted' : undefined} title={v.mountpoint}>{v.anonymous ? `unnamed ${v.name.slice(0, 12)}` : v.name}</span>
                        {v.share && <span className="badge ok share" title="a shared folder of another machine: every server that mounts it sees the same files">{v.share}</span>}
                        {v.project && (
                          <div className="mini">
                            project {project ? <Link to={`/projects/${project.id}`}>{v.project}</Link> : v.project}
                            {v.composeName && v.composeName !== v.name && <> · <code>{v.composeName}</code> in its compose file</>}
                          </div>
                        )}
                      </td>
                      <td className="num">{size >= 0 ? bytes(size) : sizes ? <span className="muted">—</span> : <span className="spinner inline" aria-label="measuring" />}</td>
                      <td>
                        {v.containers.length === 0 ? <span className="muted">—</span> : (
                          <div className="volume-users">
                            {v.containers.map(c => (
                              <span key={c.name + c.destination} className="nowrap" title={`${c.running ? 'running' : 'stopped'}, ${c.rw ? 'reads and writes' : 'only reads'}`}>
                                <i className={'dot ' + (c.running ? 'on' : 'muted')} />
                                <Link to={`/agents/${id}/containers/${encodeURIComponent(c.name)}`}>{c.name}</Link>
                                <span className="muted"> at </span><code>{c.destination}</code>{!c.rw && <span className="mini"> read-only</span>}
                              </span>
                            ))}
                          </div>
                        )}
                      </td>
                      <td className="muted hide-sm nowrap">{v.created ? new Date(v.created * 1000).toLocaleDateString() : '—'}</td>
                      <td className="menu-cell">
                        {mayVolumes && <RowMenu title={`actions for ${v.name}`} items={[
                          { label: 'browse files', onClick: () => setTool({ kind: 'browse', volume: v }) },
                          { label: 'download (.tar.gz)', onClick: () => { window.location.href = `/api/agents/${id}/volumes/${encodeURIComponent(v.name)}/export` } },
                          { label: 'upload (.tar.gz)…', onClick: () => setTool({ kind: 'upload', volume: v }) },
                          { label: 'copy to another server…', onClick: () => setTool({ kind: 'copy', volume: v }), disabled: !agents.some(a => a.online) },
                        ]} />}
                      </td>
                    </tr>
                  )
                })}
                {list.length === 0 && <tr><td colSpan={6} className="muted">{q ? 'nothing matches the filter' : show === 'unused' ? 'every volume is in use' : show === 'unnamed' ? 'no unnamed volumes' : show === 'used' ? 'no container uses a volume' : 'no volumes'}</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}
      {tool?.kind === 'browse' && <BrowseVolume agentId={id} volume={tool.volume.name} onClose={() => setTool(null)} />}
      {tool?.kind === 'upload' && <UploadVolume agentId={id} volume={tool.volume.name} onClose={() => setTool(null)} onDone={() => { void load() }} />}
      {tool?.kind === 'copy' && <CopyVolume agentId={id} volume={tool.volume} agents={agents} onClose={() => setTool(null)} />}
      {creating && <NewVolume agentId={id} onClose={() => setCreating(false)}
        onCreated={(name, message) => { setCreating(false); toast(message); setShow('all'); setFilter(name); void load() }} />}
    </>
  )
}
