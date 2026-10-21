// SignalR connection to /hub: agent heartbeats, container lists, streamed deploy logs and container logs.
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { Agent, Container, DmEvent } from './api'

let connection: HubConnection | null = null
let starting: Promise<void> | null = null
const agentListeners = new Set<(a: Agent) => void>()
const eventListeners = new Set<(e: DmEvent) => void>()
// told when the connection is back: what was pushed meanwhile is lost, lists read again
const reconnectListeners = new Set<() => void>()
const containerListeners = new Map<number, Set<(c: Container[]) => void>>()

function get(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl('/hub')
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('agent', (a: Agent) => agentListeners.forEach(l => l(a)))
    connection.on('event', (e: DmEvent) => eventListeners.forEach(l => l(e)))
    connection.on('containers', (agentId: number, cs: Container[]) => containerListeners.get(agentId)?.forEach(l => l(cs)))
    connection.onreconnected(() => {
      watched.clear()
      toldHidden = false
      syncWatches()
      reconnectListeners.forEach(l => l())
    })
    // closed for good: the server ends a connection without a reconnect when the user's access changed, and automatic
    // reconnecting gives up after a while. Connect again (after a pause: a connection ended at once must not spin)
    // and tell the listeners, so what the user may do and the lists are read again.
    connection.onclose(() => {
      setTimeout(() => { ready().then(() => reconnectListeners.forEach(l => l())).catch(() => {}) }, 1000)
    })
    // a tab in the background stops watching: its servers report less often, and their lists are not pushed to it
    document.addEventListener('visibilitychange', syncWatches)
  }
  return connection
}

// the servers this connection told the hub it watches
const watched = new Set<number>()
// what this connection last told the hub about the tab being in the background (a new connection starts as shown)
let toldHidden = false

/**
 * Tells the hub which servers this tab shows right now: the ones with container listeners, none while the tab is
 * hidden. The hub pushes their container lists to it and has those servers report every few seconds; a server
 * nobody watches reports every half minute.
 */
function syncWatches() {
  const c = connection
  if (!c || c.state !== HubConnectionState.Connected) return
  if (document.hidden !== toldHidden) {
    toldHidden = document.hidden
    void c.invoke('SetHidden', toldHidden).catch(() => { toldHidden = !toldHidden })
  }
  const wanted = document.hidden ? new Set<number>() : new Set(containerListeners.keys())
  for (const id of wanted) if (!watched.has(id)) { watched.add(id); void c.invoke('WatchAgent', id).catch(() => watched.delete(id)) }
  for (const id of [...watched]) if (!wanted.has(id)) { watched.delete(id); void c.invoke('UnwatchAgent', id).catch(() => {}) }
}

/** Connected hub; a failed start (server restarting) is retried until it works, so subscribers never stay silent. */
async function ready(): Promise<HubConnection> {
  const c = get()
  if (c.state === HubConnectionState.Connected) return c
  if (!starting) {
    starting = (async () => {
      for (let attempt = 0; ; attempt++) {
        if (c.state === HubConnectionState.Connected) return
        // automatic reconnect in progress: wait for it instead of calling start(), which would fail
        if (c.state !== HubConnectionState.Disconnected) { await new Promise(r => setTimeout(r, 500)); continue }
        try { await c.start(); watched.clear(); toldHidden = false; return } // groups are per connection: watched again below
        catch (err) {
          if (attempt >= 30) throw err
          await new Promise(r => setTimeout(r, Math.min(1000 * 2 ** attempt, 5000)))
        }
      }
    })().finally(() => { starting = null })
  }
  await starting
  syncWatches()
  return c
}

/**
 * Someone signed in: a connection made before belongs to who was signed in then (the hub knows the user from the
 * connection's start). It ends, and the close handler connects again as the new user.
 */
export async function restartLive() {
  if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop()
}

/** Subscribe to agent heartbeats (all agents). */
export function useAgentUpdates(cb: (a: Agent) => void) {
  useEffect(() => {
    agentListeners.add(cb)
    ready().catch(() => {})
    return () => { agentListeners.delete(cb) }
  }, [cb])
}

/**
 * Subscribe to new events (failed deploys, servers gone, autoscaler decisions…); `onReconnect` is called when the
 * connection is back after a drop, when events may have been missed. Both must be stable.
 */
export function useEvents(cb: (e: DmEvent) => void, onReconnect?: () => void) {
  useEffect(() => {
    eventListeners.add(cb)
    if (onReconnect) reconnectListeners.add(onReconnect)
    ready().catch(() => {})
    return () => {
      eventListeners.delete(cb)
      if (onReconnect) reconnectListeners.delete(onReconnect)
    }
  }, [cb, onReconnect])
}

/**
 * Called when the connection is back after a drop. The server also ends a connection when the user's access changed
 * (a role given or taken): what the user may do is read again then. `cb` must be stable.
 */
export function useReconnect(cb: () => void) {
  useEffect(() => {
    reconnectListeners.add(cb)
    ready().catch(() => {})
    return () => { reconnectListeners.delete(cb) }
  }, [cb])
}

/** Subscribe to the container list of one agent (pushed every heartbeat). */
export function useContainers(agentId: number | undefined, cb: (c: Container[]) => void) {
  useEffect(() => {
    if (!agentId) return
    let set = containerListeners.get(agentId)
    if (!set) containerListeners.set(agentId, set = new Set())
    set.add(cb)
    void ready().then(syncWatches).catch(() => {})
    return () => {
      set!.delete(cb)
      if (set!.size === 0) {
        containerListeners.delete(agentId)
        void ready().then(syncWatches).catch(() => {})
      }
    }
  }, [agentId, cb])
}

/** Subscribe to the container lists of several agents at once (a project that spans servers). `cb` must be stable. */
export function useProjectContainers(agentIds: number[], cb: (agentId: number, c: Container[]) => void) {
  const key = [...new Set(agentIds)].sort((a, b) => a - b).join(',')
  useEffect(() => {
    const ids = key ? key.split(',').map(Number) : []
    const subs = ids.map(id => {
      let set = containerListeners.get(id)
      if (!set) containerListeners.set(id, set = new Set())
      const listener = (c: Container[]) => cb(id, c)
      set.add(listener)
      return { id, set, listener }
    })
    void ready().then(syncWatches).catch(() => {})
    return () => {
      for (const s of subs) {
        s.set.delete(s.listener)
        if (s.set.size === 0) containerListeners.delete(s.id)
      }
      void ready().then(syncWatches).catch(() => {})
    }
  }, [key, cb])
}

/** How a server stream ended: its final "\u0001ok|fail message" marker, or why it stopped early. */
export interface StreamResult { ok: boolean; message: string }
interface StreamHandle {
  /** stops the command on the server (the cancel button) */
  cancel(): void
  /** stops watching: the server runs the command to its end, its result goes to the history */
  detach(): void
  done: Promise<StreamResult>
}

/**
 * Runs a server stream method; lines arrive through onLine, the final "\u0001ok|fail message" marker resolves `done`.
 * A command stream starts with "\u0002runId", the id CancelRun takes.
 */
function stream(method: string, args: unknown[], onLine: (line: string) => void): StreamHandle {
  let sub: { dispose(): void } | null = null
  let stopped = false
  let runId: string | null = null
  // cancel pressed before the run id arrived: sent as soon as it does
  let cancelWanted = false
  // dispose() ends the subscription without calling complete or error, so cancel / detach resolve `done` themselves
  let finish: (r: StreamResult) => void = () => {}
  const cancelRun = (id: string) => { void ready().then(c => c.invoke('CancelRun', id)).catch(() => {}) }
  const done = new Promise<StreamResult>(resolve => {
    finish = resolve
    void ready().then(c => {
      if (stopped) return
      let result = { ok: false, message: 'stream ended' }
      sub = c.stream<string>(method, ...args).subscribe({
        next: line => {
          if (line.startsWith('\u0002')) {
            runId = line.slice(1)
            if (cancelWanted) { cancelRun(runId); sub?.dispose() }
          } else if (line.startsWith('\u0001')) {
            const sp = line.indexOf(' ')
            result = { ok: line.slice(1, sp) === 'ok', message: line.slice(sp + 1) }
          } else onLine(line)
        },
        error: err => resolve({ ok: false, message: String(err?.message ?? err) }),
        complete: () => resolve(result),
      })
    }).catch(err => resolve({ ok: false, message: String(err) }))
  })
  const stop = (message: string) => { stopped = true; finish({ ok: false, message }) }
  return {
    cancel: () => {
      if (runId) { cancelRun(runId); sub?.dispose() }
      else if (sub) cancelWanted = true // keeps listening for the id, then cancels
      stop('cancelled')
    },
    detach: () => { sub?.dispose(); stop('detached') },
    done,
  }
}

/**
 * One server stream at a time for a page (a deploy, a pull & redeploy, a mesh join). run() resolves with the outcome,
 * or with null when another run() replaced it or the page was left: leaving stops watching, the server finishes what
 * it started (the result is in the history). cancel() stops it on the server; run() then resolves with "cancelled".
 */
export function useStreamTask() {
  const handle = useRef<StreamHandle | null>(null)
  const [running, setRunning] = useState(false)
  useEffect(() => () => { const h = handle.current; handle.current = null; h?.detach() }, [])

  const run = useCallback(async (method: string, args: unknown[], onLine: (line: string) => void): Promise<StreamResult | null> => {
    handle.current?.detach()
    const h = stream(method, args, onLine)
    handle.current = h
    setRunning(true)
    const result = await h.done
    if (handle.current !== h) return null
    handle.current = null
    setRunning(false)
    return result
  }, [])
  const cancel = useCallback(() => handle.current?.cancel(), [])

  return { run, cancel, running }
}

/** Log lines asked for when a log opens: a few screens in the drawer, more on the container page. */
export const LOG_TAIL = { drawer: 300, page: 500 }
// a followed log grows for hours: past MAX characters it is cut back to the last KEEP
const LOG_MAX_CHARS = 300_000
const LOG_KEEP_CHARS = 200_000

/** The log of a container, followed live; empty until there is a container. */
export function useFollowLogs(agentId: number, containerId: string | undefined, tail: number) {
  const [text, setText] = useState('')
  useEffect(() => {
    if (!containerId) return
    return followLogs(agentId, containerId, tail, t => setText(prev => {
      const next = prev + t
      return next.length > LOG_MAX_CHARS ? next.slice(-LOG_KEEP_CHARS) : next
    }))
  }, [agentId, containerId, tail])
  return text
}

/** Follows container logs; returns a stop function. */
function followLogs(agentId: number, containerId: string, tail: number, onText: (t: string) => void): () => void {
  let sub: { dispose(): void } | null = null
  let stopped = false
  void ready().then(c => {
    if (stopped) return
    sub = c.stream<string>('ContainerLogs', agentId, containerId, tail).subscribe({
      next: onText,
      error: err => onText(`\n[${err?.message ?? err}]\n`),
      complete: () => {},
    })
  }).catch(err => { if (!stopped) onText(`\n[${String(err)}]\n`) })
  return () => { stopped = true; sub?.dispose() }
}

/** A shell in a container: what it prints comes to `onData`; keys go in with send, window sizes with resize. */
export interface Terminal { send(data: string): void; resize(cols: number, rows: number): void; close(): void }

/** Opens a shell in a container (bash, or sh); `onExit` tells how it ended (the shell exited, or it failed to start). */
export function openTerminal(agentId: number, container: string, cols: number, rows: number,
  onData: (text: string) => void, onExit: (ok: boolean, message: string) => void): Terminal {
  let sub: { dispose(): void } | null = null
  let session: string | null = null
  let stopped = false
  // keys typed before the session is known wait for it
  let pending = ''
  const flush = () => {
    if (!session || !pending) return
    const data = pending
    pending = ''
    void ready().then(c => c.invoke('ExecInput', session, data, 0, 0)).catch(() => {})
  }
  void ready().then(c => {
    if (stopped) return
    sub = c.stream<string>('Exec', agentId, container, cols, rows).subscribe({
      // every item starts with its kind: \u0004 output, \u0003 the session, \u0001 how it ended
      next: text => {
        if (text.startsWith('\u0004')) onData(text.slice(1))
        else if (text.startsWith('\u0003')) { session = text.slice(1); flush() }
        else if (text.startsWith('\u0001')) { const ok = text.startsWith('\u0001ok'); onExit(ok, text.slice(ok ? 4 : 6)) }
      },
      error: err => onExit(false, err?.message ?? String(err)),
      complete: () => {},
    })
  }).catch(err => { if (!stopped) onExit(false, String(err)) })
  return {
    send: data => { pending += data; flush() },
    resize: (c, r) => { if (session) void ready().then(h => h.invoke('ExecInput', session, '', c, r)).catch(() => {}) },
    close: () => { stopped = true; sub?.dispose() },
  }
}
