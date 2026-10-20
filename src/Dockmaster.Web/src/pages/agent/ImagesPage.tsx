import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorText, type Agent } from '../../lib/api'
import { useCan } from '../../lib/access'
import { bytes } from '../../lib/fmt'
import { useSetToggle, useVisibleColumns } from '../../lib/hooks'
import { withToast } from '../../lib/toast'
import { NameList } from '../../components/Status'
import { Tab, Tabs } from '../../components/Tabs'
import ServerNav from './ServerNav'

interface Image { id: string; tags: string[]; size: number; created: number; containers: string[]; dangling: boolean }
interface RemoveResult { removed: string[]; failed: Record<string, string>; freed: number }

type Show = 'all' | 'unused' | 'dangling'
/** the confirmation lists this many images by name, the rest as a count */
const CONFIRM_NAMES = 12

/**
 * Images on one host: what takes the space, what is still used, and removing the rest by choice.
 * "untagged" are the leftovers of older versions a pull replaced; "unused" also includes tagged images no
 * container (running or stopped) was created from.
 */
export default function ImagesPage({ agents }: { agents: Agent[] }) {
  const id = Number(useParams().id)
  // removing images is the host's: the Server permission
  const mayRemove = useCan()(id, 'server')
  const agent = agents.find(a => a.id === id)
  const [images, setImages] = useState<Image[] | null>(null)
  const [show, setShow] = useState<Show>('unused')
  const [selected, toggle, setSelected] = useSetToggle<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<Set<string>>(new Set())
  const columns = useVisibleColumns(5, { sm: 1 })

  const load = useCallback(() => api<Image[]>(`/agents/${id}/images`)
    .then(list => { setImages(list); setError(null) }, e => setError(errorText(e))), [id])
  useEffect(() => { void load() }, [load])

  const all = useMemo(() => images ?? [], [images])
  const unused = all.filter(i => i.containers.length === 0)
  const dangling = unused.filter(i => i.dangling)
  const list = show === 'all' ? all : show === 'unused' ? unused : dangling
  const sum = (xs: Image[]) => xs.reduce((n, i) => n + i.size, 0)
  const chosen = list.filter(i => selected.has(i.id))

  const selectable = list.filter(i => i.containers.length === 0)
  const allChosen = selectable.length > 0 && selectable.every(i => selected.has(i.id))
  const toggleAll = () => setSelected(allChosen ? [] : selectable.map(i => i.id))
  const pick = (value: Show) => { setShow(value); setSelected([]) }

  async function remove(targets: Image[]) {
    if (targets.length === 0) return
    const names = targets.slice(0, CONFIRM_NAMES).map(i => i.tags[0] ?? shortId(i.id)).join('\n') + (targets.length > CONFIRM_NAMES ? `\n… and ${targets.length - CONFIRM_NAMES} more` : '')
    if (!confirm(`Remove ${targets.length} image(s), about ${bytes(sum(targets))}?\n\n${names}\n\nAnything removed is pulled again when a deploy needs it.`)) return
    setBusy(true); setError(null)
    // removing takes a while on a slow disk: the rows show it, and the corner says how far it is
    setRemoving(new Set(targets.map(i => i.id)))
    const ok = await withToast(`removing ${targets.length} image${targets.length > 1 ? 's' : ''} (${bytes(sum(targets))})…`,
      () => api<RemoveResult>(`/agents/${id}/images/remove`, { method: 'POST', json: { ids: targets.map(i => i.id) } }),
      r => {
        const failed = Object.entries(r.failed)
        const text = `removed ${r.removed.length} image(s), freed about ${bytes(r.freed)}` + (failed.length ? ` — ${failed.length} could not be removed: ${failed.map(([k, v]) => `${shortId(k)} (${v})`).join('; ')}` : '')
        return { text, ok: failed.length === 0 }
      })
    if (ok) { setSelected([]); await load() }
    setBusy(false); setRemoving(new Set())
  }

  const tab = (value: Show, label: string, xs: Image[]) => (
    <Tab value={value} current={show} onSelect={pick}>
      {label} <span className="tab-count" title={`${xs.length} image${xs.length === 1 ? '' : 's'}`}>{xs.length}</span>
      <span className="muted tab-stat" title="their size on disk">{bytes(sum(xs))}</span>
    </Tab>
  )

  return (
    <>
      <div className="toolbar">
        <h1><Link to={`/agents/${id}`}>{agent?.name ?? `server ${id}`}</Link><span className="muted"> / images</span></h1>
        <button className="sm" onClick={() => { void load() }} disabled={busy}>refresh</button>
      </div>
      <ServerNav agentId={id} />
      <p className="muted mini">Sizes include layers that images share, so removing several frees less than their sum. Images used by a container, even a stopped one, cannot be removed here.</p>
      {error && <p className="error">{error}</p>}
      {images === null ? (!error && <p className="muted">Loading…</p>) : (
        <>
          <Tabs>
            {tab('dangling', 'untagged', dangling)}
            {tab('unused', 'unused', unused)}
            {tab('all', 'all', all)}
          </Tabs>
          {mayRemove && <div className="filters">
            <button className="sm danger" disabled={busy || chosen.length === 0} onClick={() => { void remove(chosen) }}>
              {busy ? 'removing…' : chosen.length ? `remove ${chosen.length} selected (${bytes(sum(chosen))})` : 'remove selected'}
            </button>
            {show !== 'all' && selectable.length > 0 && (
              <button className="sm" disabled={busy} onClick={() => { void remove(selectable) }}>remove all {selectable.length} {show === 'dangling' ? 'untagged' : 'unused'}</button>
            )}
          </div>}
          <div className="table-wrap">
            <table>
              <thead><tr>
                <th className="check"><input type="checkbox" aria-label="select all" checked={allChosen} disabled={selectable.length === 0} onChange={toggleAll} /></th>
                <th>Image</th><th className="num">Size</th><th className="hide-sm">Created</th><th>Used by</th>
              </tr></thead>
              <tbody>
                {list.map(i => (
                  <tr key={i.id} className={(selected.has(i.id) ? 'selected' : '') + (removing.has(i.id) ? ' removing' : '')}>
                    <td className="check">
                      {removing.has(i.id) ? <span className="spinner" aria-label="removing" /> : <input type="checkbox" aria-label={`select ${i.tags[0] ?? shortId(i.id)}`} checked={selected.has(i.id)} disabled={i.containers.length > 0}
                        title={i.containers.length > 0 ? 'in use' : undefined} onChange={() => toggle(i.id)} />}
                    </td>
                    <td className="trunc wide" title={i.tags.join('\n') || i.id}>
                      {i.tags.length ? i.tags[0] : <span className="muted">untagged {shortId(i.id)}</span>}
                      {i.tags.length > 1 && <span className="muted mini"> +{i.tags.length - 1} tag{i.tags.length > 2 ? 's' : ''}</span>}
                    </td>
                    <td className="num">{bytes(i.size)}</td>
                    <td className="muted hide-sm nowrap">{new Date(i.created * 1000).toLocaleDateString()}</td>
                    <td>{i.containers.length ? <NameList names={i.containers} max={2} /> : <span className="muted">—</span>}</td>
                  </tr>
                ))}
                {list.length === 0 && <tr><td colSpan={columns} className="muted">{show === 'dangling' ? 'no untagged leftovers' : show === 'unused' ? 'every image is in use' : 'no images'}</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  )
}

const shortId = (id: string) => id.replace('sha256:', '').slice(0, 12)
