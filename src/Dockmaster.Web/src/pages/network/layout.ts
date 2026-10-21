// Where the servers sit on the network graph: a small force layout (no library). Servers repel each other, links pull
// them together, members of a network drift towards the network's middle, so each network forms its own cluster and a
// server shared by two networks lands between them. Distances come from the size of a server's box, not of the canvas:
// a few servers make a compact picture in the middle of a big screen instead of being spread into its corners.
// Deterministic: the same input gives the same picture, and servers the user dragged stay where they were put.

interface LayoutNode { id: number; groups: number[] }
interface LayoutLink { a: number; b: number }
export interface Point { x: number; y: number }

export function layout(nodes: LayoutNode[], links: LayoutLink[], width: number, height: number, pinned: Record<number, Point> = {}, nodeW = 150, nodeH = 56): Record<number, Point> {
  const pos: Record<number, Point> = {}
  if (nodes.length === 0) return pos
  const cx = width / 2, cy = height / 2
  const padX = nodeW / 2 + 16, padY = nodeH / 2 + 30
  // start on a circle, grouped by first network so clusters begin apart
  const order = [...nodes].sort((p, q) => (p.groups[0] ?? 1e9) - (q.groups[0] ?? 1e9) || p.id - q.id)
  // between the middles of two servers of one network: a box and a half
  const ideal = nodeW * 1.35
  const r0 = Math.min(Math.min(width, height) * 0.32, (ideal * order.length) / (2 * Math.PI) + ideal / 2)
  order.forEach((n, i) => {
    const pin = pinned[n.id]
    if (pin) { pos[n.id] = { ...pin }; return }
    const a = (2 * Math.PI * i) / order.length - Math.PI / 2
    pos[n.id] = { x: cx + r0 * Math.cos(a) * (width / Math.max(width, height)), y: cy + r0 * Math.sin(a) }
  })
  if (nodes.length === 1 && !pinned[nodes[0].id]) { pos[nodes[0].id] = { x: cx, y: cy }; return pos }

  const groups = [...new Set(nodes.flatMap(n => n.groups))]
  for (let step = 0; step < 320; step++) {
    const t = 1 - step / 320 // cooling
    const force: Record<number, Point> = {}
    for (const n of nodes) force[n.id] = { x: 0, y: 0 }
    // repulsion, stronger when boxes would overlap. Servers of different networks only push each other away up close
    // (their frames must not overlap): further out nothing drives the clusters apart, gravity packs them together.
    for (let i = 0; i < nodes.length; i++)
      for (let j = i + 1; j < nodes.length; j++) {
        const p = pos[nodes[i].id], q = pos[nodes[j].id]
        let dx = p.x - q.x, dy = p.y - q.y
        if (dx === 0 && dy === 0) { dx = 0.1 * (i + 1); dy = 0.1 }
        const d = Math.max(1, Math.hypot(dx, dy))
        const together = nodes[i].groups.some(g => nodes[j].groups.includes(g))
        if (!together && d > ideal * 2) continue
        const overlap = Math.abs(dx) < nodeW + 24 && Math.abs(dy) < nodeH + 24 ? 3 : 1
        const f = ((together ? ideal : ideal * 1.4) ** 2) / d * overlap
        force[nodes[i].id].x += (dx / d) * f; force[nodes[i].id].y += (dy / d) * f
        force[nodes[j].id].x -= (dx / d) * f; force[nodes[j].id].y -= (dy / d) * f
      }
    // links pull
    for (const l of links) {
      const p = pos[l.a], q = pos[l.b]
      if (!p || !q) continue
      const dx = q.x - p.x, dy = q.y - p.y
      const d = Math.max(1, Math.hypot(dx, dy))
      const f = (d * d) / ideal * 0.6
      force[l.a].x += (dx / d) * f; force[l.a].y += (dy / d) * f
      force[l.b].x -= (dx / d) * f; force[l.b].y -= (dy / d) * f
    }
    // clusters: towards the middle of each network the server is in
    for (const g of groups) {
      const members = nodes.filter(n => n.groups.includes(g))
      if (members.length < 2) continue
      const mx = members.reduce((s, n) => s + pos[n.id].x, 0) / members.length
      const my = members.reduce((s, n) => s + pos[n.id].y, 0) / members.length
      for (const n of members) { force[n.id].x += (mx - pos[n.id].x) * 0.8; force[n.id].y += (my - pos[n.id].y) * 0.8 }
    }
    // gravity keeps the picture centred
    for (const n of nodes) { force[n.id].x += (cx - pos[n.id].x) * 0.15; force[n.id].y += (cy - pos[n.id].y) * 0.15 }
    const maxMove = 40 * t + 1
    for (const n of nodes) {
      if (pinned[n.id]) continue
      const f = force[n.id]
      const len = Math.hypot(f.x, f.y)
      const k = len > maxMove ? maxMove / len : 1
      pos[n.id] = { x: pos[n.id].x + f.x * k * 0.1, y: pos[n.id].y + f.y * k * 0.1 }
    }
  }
  // fit into the box: scale the free nodes' spread down when it is too wide, never up (few nodes stay compact)
  const free = nodes.filter(n => !pinned[n.id])
  if (free.length > 0) {
    const xs = free.map(n => pos[n.id].x), ys = free.map(n => pos[n.id].y)
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys)
    const sx = maxX - minX > 0 ? Math.min(1, (width - 2 * padX) / (maxX - minX)) : 1
    const sy = maxY - minY > 0 ? Math.min(1, (height - 2 * padY) / (maxY - minY)) : 1
    const mx = (minX + maxX) / 2, my = (minY + maxY) / 2
    for (const n of free) pos[n.id] = { x: cx + (pos[n.id].x - mx) * sx, y: cy + (pos[n.id].y - my) * sy }
  }
  for (const n of nodes) pos[n.id] = clamp(pos[n.id], width, height, padX, padY)
  return pos
}

export function clamp(p: Point, width: number, height: number, padX: number, padY: number): Point {
  return { x: Math.min(width - padX, Math.max(padX, p.x)), y: Math.min(height - padY, Math.max(padY, p.y)) }
}

/** Bounding box around a network's members, with room for the label on top. */
export function hull(points: Point[], nodeW: number, nodeH: number, pad = 18) {
  const xs = points.map(p => p.x), ys = points.map(p => p.y)
  const x = Math.min(...xs) - nodeW / 2 - pad, y = Math.min(...ys) - nodeH / 2 - pad - 16
  return { x, y, w: Math.max(...xs) - Math.min(...xs) + nodeW + 2 * pad, h: Math.max(...ys) - Math.min(...ys) + nodeH + 2 * pad + 16 }
}
