import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, type NewAgentResponse, type Settings } from '../lib/api'

/** Adds a server: a name and the URL its agent connects to, then the one-time `docker run` command to start it. */
export default function AgentNew({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<NewAgentResponse | null>(null)

  useEffect(() => { api<Settings>('/settings').then(s => setUrl(s.agentPublicUrl)).catch(() => {}) }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      setResult(await api<NewAgentResponse>('/agents', { method: 'POST', json: { name, publicUrl: url } }))
      onCreated()
    } catch (err) { setError(errorText(err)) }
  }

  if (result) return (
    <>
      <h1>Add server</h1>
      <div className="card">
        <p>Server <b>{name}</b> added. Run this on it (Linux with Docker) to start the Dockmaster agent. The token is shown only once.</p>
        <pre className="cmd">{result.command}</pre>
        <p className="mini">The agent connects <i>outbound</i> to {url} over TLS and pins the Dockmaster certificate fingerprint <code>{result.fingerprint}</code>. Nothing needs to be opened on the server.</p>
        <p className="mini">Update the agent later: <code>docker pull &lt;image&gt; && docker rm -f dockmaster-agent</code> and run the same command again (the volume keeps project files).</p>
        <Link className="btn primary" to={`/agents/${result.id}`}>Open server page</Link>
      </div>
    </>
  )

  return (
    <>
      <h1>Add server</h1>
      <form className="form card" onSubmit={submit}>
        <label htmlFor="agent-name">Server name (how it shows up in the dashboard)</label>
        <input id="agent-name" value={name} onChange={e => setName(e.target.value)} placeholder="web-prod" required autoFocus />
        <label htmlFor="agent-url">Dockmaster URL the agent connects to (must be reachable from the server; port is the agent port)</label>
        <input id="agent-url" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://nas.example.com:9443" required />
        {error && <p className="error">{error}</p>}
        <div className="form-actions"><button className="primary" type="submit">Create</button></div>
      </form>
    </>
  )
}
