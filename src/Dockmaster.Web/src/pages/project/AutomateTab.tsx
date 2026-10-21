import { useEffect, useState } from 'react'
import { api, errorText, type ServiceSummary } from '../../lib/api'
import { toast } from '../../lib/toast'

/**
 * Deploying from CI: one secret URL per project. CI calls it after it pushed a new image, and the project is pulled
 * and deployed — with a new image tag for a service when CI builds versioned tags. Examples for the usual CI tools.
 */
export default function AutomateTab({ projectId, services }: { projectId: number; services: ServiceSummary[] }) {
  const [path, setPath] = useState<string | null | undefined>(undefined)
  const [service, setService] = useState(services[0]?.name ?? '')
  const [withTag, setWithTag] = useState(false)
  const [wait, setWait] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { api<{ path: string | null }>(`/projects/${projectId}/hook`).then(r => setPath(r.path), e => setError(errorText(e))) }, [projectId])

  async function change(method: 'POST' | 'DELETE') {
    const warn = method === 'DELETE' ? 'Turn the webhook off? CI calls to it fail from now on.' : path ? 'Make a new URL? The old one stops working: update it in CI.' : null
    if (warn && !confirm(warn)) return
    setBusy(true); setError(null)
    try { setPath((await api<{ path: string | null }>(`/projects/${projectId}/hook`, { method })).path) }
    catch (e) { setError(errorText(e)) }
    finally { setBusy(false) }
  }

  const base = path ? window.location.origin + path : ''
  const query = [services.length > 1 || withTag ? `services=${service}` : '', withTag ? 'tag=${TAG}' : '', wait ? 'wait=true' : ''].filter(Boolean).join('&')
  const url = base + (query ? '?' + query : '')
  const curl = `curl -fsS -X POST "${url}"`
  const copy = (text: string) => navigator.clipboard.writeText(text).then(() => toast('copied'), () => toast('copy it by hand', 'error'))
  const tagOf = { drone: '${DRONE_COMMIT_SHA:0:8}', github: '${{ github.sha }}', gitlab: '$CI_COMMIT_SHORT_SHA' }

  return (
    <>
      <p className="muted">Deploy this project from CI (Drone, GitHub Actions, GitLab…) the moment a new image is pushed — instead of clicking <b>Pull &amp; deploy</b> or a <code>deploy.sh</code> on the server. CI calls one secret URL; Dockmaster pulls and deploys, rolling like any deploy, and the history shows it as <span className="badge muted">webhook</span>.</p>
      {error && <p className="error">{error}</p>}
      {path === undefined ? <p className="muted">Loading…</p> : !path ? (
        <div className="card">
          <p>No webhook yet.</p>
          <button className="primary" disabled={busy} onClick={() => { void change('POST') }}>Create a webhook URL</button>
        </div>
      ) : (
        <>
          <div className="card">
            <div className="field-label">What CI calls</div>
            <div className="row">
              {services.length > 1 || withTag ? (
                <div className="narrow">
                  <label htmlFor="hook-svc">Service</label>
                  <select id="hook-svc" value={service} onChange={e => setService(e.target.value)}>
                    {services.map(s => <option key={s.name} value={s.name}>{s.name}</option>)}
                  </select>
                </div>
              ) : null}
              <div className="checks hook-options">
                {services.length > 1 && !withTag && <span className="mini">only this service is deployed — remove <code>services=</code> from the URL for all of them</span>}
                <label title="CI builds versioned images (app:1.4.2, app:3f2a1c9d): the URL carries the tag, written into the compose file before the deploy">
                  <input type="checkbox" checked={withTag} onChange={e => setWithTag(e.target.checked)} /> with a new image tag</label>
                <label title="CI waits for the deploy and its step fails when the deploy fails">
                  <input type="checkbox" checked={wait} onChange={e => setWait(e.target.checked)} /> wait for the result</label>
              </div>
            </div>
            <pre className="cmd">{curl}</pre>
            <div className="toolbar">
              <button className="sm primary" onClick={() => { void copy(curl) }}>copy command</button>
              <button className="sm" onClick={() => { void copy(url) }}>copy URL</button>
              <div className="spacer" />
              <button className="sm" disabled={busy} onClick={() => { void change('POST') }} title="a new secret URL; the old one stops working">new URL</button>
              <button className="sm danger" disabled={busy} onClick={() => { void change('DELETE') }}>turn off</button>
            </div>
            <p className="mini">The URL is the key: anyone who has it can deploy this project (nothing else). Keep it in CI's secrets, not in the repository.{withTag && <> <code>${'{'}TAG{'}'}</code> is the image tag CI just pushed; the image name stays as the compose file has it.</>}{wait && ' With “wait”, curl -f fails the CI step when the deploy fails.'}</p>
          </div>

          <h2>In CI</h2>
          <details open>
            <summary>Drone (<code>.drone.yml</code>, after the step that pushes the image)</summary>
            <pre className="cmd">{`- name: deploy
  image: curlimages/curl
  environment:
    DOCKMASTER_HOOK:
      from_secret: dockmaster_hook   # the URL above, without ?…
  commands:
    - curl -fsS -X POST "$DOCKMASTER_HOOK${hookQuery(query, withTag, tagOf.drone)}"`}</pre>
          </details>
          <details>
            <summary>GitHub Actions</summary>
            <pre className="cmd">{`- name: Deploy
  run: curl -fsS -X POST "\${{ secrets.DOCKMASTER_HOOK }}${hookQuery(query, withTag, tagOf.github)}"`}</pre>
          </details>
          <details>
            <summary>GitLab CI</summary>
            <pre className="cmd">{`deploy:
  stage: deploy
  image: curlimages/curl
  script:
    - curl -fsS -X POST "$DOCKMASTER_HOOK${hookQuery(query, withTag, tagOf.gitlab)}"`}</pre>
          </details>
          <p className="mini">Put the URL without its <code>?…</code> part into CI's secret; the step adds the part after it. Dockmaster must be reachable from the CI runner at {window.location.origin}.</p>
        </>
      )}
    </>
  )
}

/** The `?…` part of the URL for a CI step, with that CI's variable for the tag. */
const hookQuery = (query: string, withTag: boolean, tagVar: string) => query ? '?' + (withTag ? query.replace('${TAG}', tagVar) : query) : ''
