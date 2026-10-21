import type { Mesh, MeshNetwork, MeshNode, MeshPair } from '../../lib/api'
import type { StreamResult } from '../../lib/live'

export type Selection = { kind: 'node'; id: number } | { kind: 'link'; key: string } | null

/** A streamed mesh task (a server joins or leaves, a bandwidth test), shown in the panel of what it is about. */
export interface MeshTask { kind: 'join' | 'leave' | 'bench' | 'tune'; key: string; title: string; lines: string[]; result?: StreamResult }

/** Mbit/s per direction ("from-to") and when it was measured. */
export type Rates = Record<string, { mbps: number; at: number }>

/** Network colour by position, so a network keeps its colour on every page. */
export const netColor = (networks: MeshNetwork[], id: number) => `var(--net-${(networks.findIndex(n => n.id === id) % 6 + 6) % 6})`

/** One line on the graph: both directions between two servers that share a network. */
export interface Link {
  key: string
  a: MeshNode
  b: MeshNode
  ab: MeshPair
  ba: MeshPair | undefined
  networkId: number
  /** 'ok' handshake in the last 3 minutes, 'stale' older, 'down' never / no path, 'off' a side is offline */
  health: 'ok' | 'stale' | 'down' | 'off'
  rttMs: number | null
}

/** The dot colour of a link's health. */
export const HEALTH_DOT: Record<Link['health'], string> = { ok: 'ok', stale: 'warn', down: 'bad', off: 'muted' }

const linkKey =(a: number, b: number) => (a < b ? `${a}-${b}` : `${b}-${a}`)

export function links(mesh: Mesh): Link[] {
  const byId = new Map(mesh.nodes.map(n => [n.agentId, n]))
  const out: Link[] = []
  for (const p of mesh.pairs) {
    if (p.from > p.to) continue
    const a = byId.get(p.from), b = byId.get(p.to)
    if (!a || !b) continue
    const ba = mesh.pairs.find(x => x.from === p.to && x.to === p.from)
    const rtts = [p.rttMs, ba?.rttMs].filter((v): v is number => v != null)
    const hs = [p.handshakeAgeS, ba?.handshakeAgeS].filter((v): v is number => v != null)
    const health: Link['health'] = !a.online || !b.online ? 'off'
      // no tunnel, so no handshake: the ping tells
      : p.path === 'plain' ? (rtts.length ? 'ok' : 'down')
      : p.path === 'none' || hs.length === 0 ? 'down'
      : Math.min(...hs) < 180 ? 'ok' : 'stale'
    out.push({ key: linkKey(a.agentId, b.agentId), a, b, ab: p, ba, networkId: p.networkId, health, rttMs: rtts.length ? Math.min(...rtts) : null })
  }
  return out
}

/** Bytes counters seen at a time, per direction ("from-to"), to turn them into a rate. */
export interface Sample { at: number; bytes: Record<string, number> }

/** Mbit/s per direction from two samples; the agent refreshes its counters every few seconds, so an unchanged counter keeps the last rate for a while. */
export function rates(prev: Sample | null, cur: Sample, last: Rates): Rates {
  const next: Rates = {}
  for (const [k, v] of Object.entries(cur.bytes)) {
    const before = prev?.bytes[k]
    const seconds = prev ? (cur.at - prev.at) / 1000 : 0
    if (before != null && v > before && seconds > 0) next[k] = { mbps: ((v - before) * 8) / seconds / 1e6, at: cur.at }
    else if (last[k] && cur.at - last[k].at < 12000) next[k] = last[k]
    else next[k] = { mbps: 0, at: cur.at }
  }
  return next
}

/** Sent bytes per direction (tx of from towards to). */
export function sample(mesh: Mesh, at: number): Sample {
  const bytes: Record<string, number> = {}
  for (const p of mesh.pairs) if (p.path !== 'via') bytes[`${p.from}-${p.to}`] = p.tx
  return { at, bytes }
}

/** Seconds one dot takes along a link: faster with more traffic, null = idle (no dots). */
export function flowSeconds(mbps: number): number | null {
  if (mbps < 0.005) return null
  return Math.max(0.35, Math.min(4, 3.2 / Math.log2(2 + mbps)))
}

export const fmtRate = (mbps: number) => mbps >= 100 ? `${Math.round(mbps)} Mbit/s` : mbps >= 1 ? `${mbps.toFixed(1)} Mbit/s` : mbps > 0.005 ? `${Math.round(mbps * 1000)} kbit/s` : 'idle'
export const fmtRtt = (ms: number | null) => ms == null ? '—' : ms < 10 ? `${ms.toFixed(1)} ms` : `${Math.round(ms)} ms`
