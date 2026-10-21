import { useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import type { MeshNetwork, MeshNode } from '../../lib/api'
import { useStoredState } from '../../lib/hooks'
import { clamp, hull, layout, type Point } from './layout'
import { flowSeconds, fmtRtt, netColor, type Link, type Selection } from './model'

interface Props {
  nodes: MeshNode[]
  networks: MeshNetwork[]
  links: Link[]
  /** Mbit/s per direction "from-to" */
  rates: Record<string, { mbps: number }>
  /** only this network stands out; the rest fades */
  focus: number | null
  selection: Selection
  onSelect: (s: Selection) => void
  /** servers being added right now: they pulse */
  joining: number[]
}

// where servers were dragged to, as fractions of the box; stored as they move
const PIN_KEY = 'dm.network.pins'

/**
 * The mesh as a picture: servers as cards, networks as tinted areas around their members, links as lines whose colour
 * tells the handshake's freshness and whose moving dots follow the real traffic in each direction. New links draw
 * themselves in, a fresh handshake sends a pulse along the line. Servers can be dragged; where they were put is kept
 * in this browser.
 */
export default function NetworkGraph({ nodes, networks, links, rates, focus, selection, onSelect, joining }: Props) {
  const wrap = useRef<HTMLDivElement>(null)
  const svg = useRef<SVGSVGElement>(null)
  const [width, setWidth] = useState(900)
  const [pins, setPins] = useStoredState<Record<number, Point>>(PIN_KEY, {})
  const drag = useRef<{ id: number; startX: number; startY: number; moved: boolean } | null>(null)

  useLayoutEffect(() => {
    const el = wrap.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(Math.max(320, el.clientWidth)))
    ro.observe(el)
    setWidth(Math.max(320, el.clientWidth))
    return () => ro.disconnect()
  }, [])

  // big screens get bigger cards, the picture keeps a pleasant aspect
  const big = width >= 1500
  const nodeW = big ? 176 : 150, nodeH = big ? 62 : 56
  const height = Math.round(Math.min(780, Math.max(380, width * (nodes.length > 6 ? 0.62 : 0.5))))

  // pins are stored as fractions of the box, so they survive a resize
  const pinsPx = useMemo(() => Object.fromEntries(Object.entries(pins).map(([id, p]) => [id, { x: p.x * width, y: p.y * height }])) as Record<number, Point>, [pins, width, height])
  // deterministic, so re-running it on every poll puts everything at the same place (a few dozen servers cost nothing)
  const pos = useMemo(
    () => layout(nodes.map(n => ({ id: n.agentId, groups: n.networkIds })), links.map(l => ({ a: l.a.agentId, b: l.b.agentId })), width, height, pinsPx, nodeW, nodeH),
    [nodes, links, width, height, pinsPx, nodeW, nodeH])

  // links that just appeared draw themselves in; a new handshake sends a pulse
  const seen = useRef<Set<string> | null>(null)
  const lastHs = useRef<Record<string, number>>({})
  const [fresh, setFresh] = useState<Set<string>>(new Set())
  const [pulses, setPulses] = useState<Set<string>>(new Set())
  useEffect(() => {
    const keys = new Set(links.map(l => l.key))
    const born = seen.current ? [...keys].filter(k => !seen.current!.has(k)) : []
    seen.current = keys
    const pulsed: string[] = []
    for (const l of links) {
      const hs = Math.min(l.ab.handshakeAgeS ?? 1e9, l.ba?.handshakeAgeS ?? 1e9)
      const before = lastHs.current[l.key]
      if (before != null && hs < before - 1) pulsed.push(l.key)
      lastHs.current[l.key] = hs
    }
    if (born.length === 0 && pulsed.length === 0) return
    // the animations restart only when the class comes back: set, then clear after they ran
    const show = setTimeout(() => { setFresh(new Set(born)); setPulses(new Set(pulsed)) }, 0)
    const hide = setTimeout(() => { setFresh(new Set()); setPulses(new Set()) }, 1400)
    return () => { clearTimeout(show); clearTimeout(hide) }
  }, [links])

  function toSvg(e: { clientX: number; clientY: number }): Point {
    const m = svg.current?.getScreenCTM()
    if (!m) return { x: 0, y: 0 }
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(m.inverse())
    return { x: p.x, y: p.y }
  }

  function down(e: ReactPointerEvent, id: number) {
    e.stopPropagation()
    ;(e.currentTarget as Element).setPointerCapture(e.pointerId)
    drag.current = { id, startX: e.clientX, startY: e.clientY, moved: false }
  }
  function move(e: ReactPointerEvent) {
    const d = drag.current
    if (!d) return
    if (!d.moved && Math.hypot(e.clientX - d.startX, e.clientY - d.startY) < 4) return
    d.moved = true
    const p = clamp(toSvg(e), width, height, nodeW / 2 + 8, nodeH / 2 + 8)
    setPins(prev => ({ ...prev, [d.id]: { x: p.x / width, y: p.y / height } }))
  }
  function up(id: number) {
    const d = drag.current
    drag.current = null
    if (!d) return
    if (!d.moved) onSelect({ kind: 'node', id })
  }
  const resetLayout = () => setPins({})

  const dim = (netIds: number[]) => focus != null && !netIds.includes(focus)
  const selectedKey = selection?.kind === 'link' ? selection.key : null
  const selectedNode = selection?.kind === 'node' ? selection.id : null

  return (
    <div className="ngraph" ref={wrap}>
      <svg ref={svg} viewBox={`0 0 ${width} ${height}`} width="100%" height={height} role="img" aria-label="servers and the links between them"
        onPointerMove={move} onClick={() => onSelect(null)}>
        {/* networks: a tinted area around their members */}
        {networks.map(n => {
          const pts = n.members.map(id => pos[id]).filter(Boolean)
          if (pts.length === 0) return null
          const h = hull(pts, nodeW, nodeH)
          return (
            <g key={n.id} className={'nnet' + (n.trusted ? ' trusted' : '') + (focus != null && focus !== n.id ? ' dim' : '')} style={{ ['--c' as string]: netColor(networks, n.id) }}>
              <rect x={h.x} y={h.y} width={h.w} height={h.h} rx={18} />
              <text x={h.x + 14} y={h.y + 18}>{n.name}{n.trusted && <tspan className="nnet-trusted"> · trusted LAN</tspan>}</text>
            </g>
          )
        })}

        {links.map(l => {
          const p = pos[l.a.agentId], q = pos[l.b.agentId]
          if (!p || !q) return null
          const via = l.ab.path === 'via'
          const plain = l.ab.path === 'plain'
          const dx = q.x - p.x, dy = q.y - p.y, len = Math.max(1, Math.hypot(dx, dy))
          const nx = -dy / len, ny = dx / len // unit normal: the two directions run side by side
          // through a hub: a curve bowing away, so it does not hide the real links to the hub
          const mid = via ? { x: (p.x + q.x) / 2 + nx * 46, y: (p.y + q.y) / 2 + ny * 46 } : { x: (p.x + q.x) / 2, y: (p.y + q.y) / 2 }
          const d = via ? `M${p.x},${p.y} Q${mid.x * 2 - (p.x + q.x) / 2},${mid.y * 2 - (p.y + q.y) / 2} ${q.x},${q.y}` : `M${p.x},${p.y} L${q.x},${q.y}`
          const ab = flowSeconds(rates[`${l.a.agentId}-${l.b.agentId}`]?.mbps ?? 0)
          const ba = flowSeconds(rates[`${l.b.agentId}-${l.a.agentId}`]?.mbps ?? 0)
          const cls = ['nlink', l.health, via && 'via', plain && 'plain', selectedKey === l.key && 'selected', fresh.has(l.key) && 'enter', pulses.has(l.key) && 'pulse',
            dim([l.networkId]) && 'dim'].filter(Boolean).join(' ')
          const label = via ? `via ${l.ab.viaName} · ${fmtRtt(l.rttMs)}` : plain ? `LAN · ${fmtRtt(l.rttMs)}` : fmtRtt(l.rttMs)
          return (
            <g key={l.key} className={cls} onClick={e => { e.stopPropagation(); onSelect({ kind: 'link', key: l.key }) }}>
              <path className="nlink-hit" d={d} />
              <path className="nlink-base" d={d} pathLength={via ? undefined : 1} />
              {!via && l.health !== 'off' && ab != null && (
                <line className="nlink-flow" x1={p.x + nx * 3} y1={p.y + ny * 3} x2={q.x + nx * 3} y2={q.y + ny * 3} style={{ animationDuration: `${ab}s` }} />
              )}
              {!via && l.health !== 'off' && ba != null && (
                <line className="nlink-flow" x1={q.x - nx * 3} y1={q.y - ny * 3} x2={p.x - nx * 3} y2={p.y - ny * 3} style={{ animationDuration: `${ba}s` }} />
              )}
              <g className="nlink-label" transform={`translate(${mid.x},${mid.y})`}>
                <rect x={-label.length * 3.4 - 8} y={-10} width={label.length * 6.8 + 16} height={20} rx={10} />
                <text textAnchor="middle" dy="4">{label}</text>
              </g>
            </g>
          )
        })}

        {nodes.map(n => {
          const p = pos[n.agentId]
          if (!p) return null
          const state = !n.online ? 'off' : n.problems.length > 0 ? 'bad' : !n.up ? 'warn' : !n.inSync ? 'warn' : 'ok'
          const cls = ['nnode', state, selectedNode === n.agentId && 'selected', joining.includes(n.agentId) && 'joining', dim(n.networkIds) && 'dim'].filter(Boolean).join(' ')
          const name = n.name.length > (big ? 19 : 16) ? n.name.slice(0, big ? 18 : 15) + '…' : n.name
          return (
            <g key={n.agentId} className={cls} transform={`translate(${p.x - nodeW / 2},${p.y - nodeH / 2})`}
              onPointerDown={e => down(e, n.agentId)} onPointerUp={() => up(n.agentId)} onClick={e => e.stopPropagation()}
              role="button" tabIndex={0} aria-label={`server ${n.name}`} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') onSelect({ kind: 'node', id: n.agentId }) }}>
              <circle className="nnode-ring" cx={nodeW / 2} cy={nodeH / 2} r={nodeW / 2} />
              <rect className="nnode-box" width={nodeW} height={nodeH} rx={10} />
              <circle className="nnode-dot" cx={14} cy={19} r={4.5} />
              <text className="nnode-name" x={26} y={23}>{name}</text>
              {n.hub && <text className="nnode-hub" x={nodeW - 10} y={22} textAnchor="end">HUB</text>}
              <text className="nnode-ip" x={14} y={nodeH - 14}>{n.meshIp ?? '—'}</text>
              <text className="nnode-kind" x={nodeW - 10} y={nodeH - 14} textAnchor="end">{n.public ? 'public' : 'NAT'}</text>
            </g>
          )
        })}
      </svg>
      {Object.keys(pins).length > 0 && <button className="sm ngraph-reset" onClick={resetLayout} title="forget where you dragged the servers">auto layout</button>}
    </div>
  )
}
