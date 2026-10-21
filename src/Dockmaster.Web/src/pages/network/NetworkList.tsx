import type { MeshNetwork, MeshNode } from '../../lib/api'
import { fmtRate, fmtRtt, HEALTH_DOT, netColor, type Link, type Selection } from './model'

/** The phone's picture of the mesh: per network its servers and links as tappable rows. */
export default function NetworkList({ nodes, networks, links, rates, focus, onSelect }: {
  nodes: MeshNode[]
  networks: MeshNetwork[]
  links: Link[]
  rates: Record<string, { mbps: number }>
  focus: number | null
  onSelect: (s: Selection) => void
}) {
  const shown = networks.filter(n => focus == null || n.id === focus)
  const rate = (l: Link) => (rates[`${l.a.agentId}-${l.b.agentId}`]?.mbps ?? 0) + (rates[`${l.b.agentId}-${l.a.agentId}`]?.mbps ?? 0)
  return (
    <div className="nlist">
      {shown.map(net => {
        const members = nodes.filter(n => n.networkIds.includes(net.id))
        const ls = links.filter(l => l.networkId === net.id)
        return (
          <section key={net.id} className="nlist-net" style={{ ['--c' as string]: netColor(networks, net.id) }}>
            <h3>{net.name} <span className="mini">{members.length} server{members.length === 1 ? '' : 's'}</span></h3>
            <div className="nlist-servers">
              {members.map(n => (
                <button key={n.agentId} className="chip" onClick={() => onSelect({ kind: 'node', id: n.agentId })}>
                  <i className={'dot ' + (!n.online ? 'off' : n.problems.length ? 'bad' : 'ok')} />{n.name}{n.hub ? ' · hub' : ''}
                </button>
              ))}
            </div>
            {ls.map(l => {
              const r = rate(l)
              return (
                <button key={l.key} className="nlist-link" onClick={() => onSelect({ kind: 'link', key: l.key })}>
                  <i className={'dot ' + HEALTH_DOT[l.health]} />
                  <span className="nlist-ends">{l.a.name} ↔ {l.b.name}</span>
                  <span className="mini">{l.ab.path === 'via' ? `via ${l.ab.viaName}` : l.ab.path === 'lan' ? 'LAN' : l.ab.path === 'plain' ? 'LAN, no tunnel' : ''}</span>
                  <span className="num">{fmtRtt(l.rttMs)}</span>
                  {r > 0.005 && <span className={'nlist-rate' + (r > 1 ? ' busy' : '')}>{fmtRate(r)}</span>}
                </button>
              )
            })}
            {ls.length === 0 && <p className="mini">{members.length < 2 ? 'Add a second server to connect them.' : 'No links yet.'}</p>}
          </section>
        )
      })}
    </div>
  )
}
