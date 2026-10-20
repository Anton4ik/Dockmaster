import type { MeshNetwork } from '../../lib/api'
import Icon from '../../components/Icon'
import { ICON } from '../../components/icons'
import InlineNameForm from '../../components/InlineNameForm'
import { netColor } from './model'

/** The networks as toggle chips: one stands out on the page, "All" shows every one; and creating another. */
export default function NetworkChips({ networks, focus, onFocus, adding, onAdding, onCreate }: {
  networks: MeshNetwork[]
  focus: number | null
  onFocus: (id: number | null) => void
  /** the name field for a new network is open */
  adding: boolean
  onAdding: (open: boolean) => void
  onCreate: (name: string) => void
}) {
  return (
    <div className="net-chips" role="group" aria-label="networks">
      <button aria-pressed={focus == null} className={'net-chip' + (focus == null ? ' on' : '')} onClick={() => onFocus(null)}>All</button>
      {networks.map(n => (
        <button key={n.id} aria-pressed={focus === n.id} className={'net-chip' + (focus === n.id ? ' on' : '')} style={{ ['--c' as string]: netColor(networks, n.id) }}
          onClick={() => onFocus(focus === n.id ? null : n.id)}>
          <i className="net-swatch" />{n.name}
          {n.trusted && <span className="net-trusted" title="trusted LAN: members on one LAN talk without the tunnel, unencrypted"><Icon d={ICON.unlocked} size={13} stroke={2} /></span>}
          <span className="mini">{n.members.length}</span>
        </button>
      ))}
      {adding
        ? <InlineNameForm className="net-new" inputClassName="cell" label="name of the new network" placeholder="home, prod, office…"
            saveLabel="Create" cancelLabel="Cancel" onSave={onCreate} onCancel={() => onAdding(false)} />
        : <button className="net-chip add" onClick={() => onAdding(true)}>+ New network</button>}
    </div>
  )
}
