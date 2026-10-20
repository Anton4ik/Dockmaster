import { useMemo, useState } from 'react'
import type { ServiceSummary } from '../../lib/api'
import { looksSecret, parseEnvFile } from '../../lib/fmt'
import Modal from '../../components/Modal'

/** A value to store as a secret: a line of the .env, or a service's own setting (`service` set). */
export interface SecretMove { name: string; value: string; secret: string; service?: string }

const VARIABLE = /^[A-Za-z_][A-Za-z0-9_]*$/

/** "storeapi" + "OpenAI__Token" → "storeapi_OpenAI_Token": a name that is a secret and a ${VARIABLE} both. */
const secretName = (service: string, name: string) => `${service}_${name}`.replace(/[^A-Za-z0-9_]/g, '_').replace(/_+/g, '_')

/** The passwords, keys and connection strings written in plain text: in the .env, or as a service's own value. */
function candidates(env: string, services: ServiceSummary[]): SecretMove[] {
  const fromEnv = Object.entries(parseEnvFile(env)).filter(([k, v]) => looksSecret(k) && v && VARIABLE.test(k)).map(([k, v]) => ({ name: k, value: v, secret: k }))
  const fromServices = services.flatMap(s => (s.environment ?? []).filter(e => looksSecret(e.name) && e.value && !e.value.includes('${'))
    .map(e => ({ name: e.name, value: e.value, secret: secretName(s.name, e.name), service: s.name })))
  return [...fromEnv, ...fromServices]
}

/**
 * Passwords in plain text, moved into Secrets in one go: each value is stored encrypted, its .env line goes, and a
 * service's own value becomes ${the secret}. Compose fills the secrets in like .env variables, so the containers get
 * the same values.
 */
export default function MoveToSecrets({ env, services, secretNames, running, onMove }: {
  env: string
  services: ServiceSummary[]
  /** the secrets the project has: one of the same name is replaced */
  secretNames: string[]
  running: boolean
  onMove: (moves: SecretMove[]) => Promise<boolean>
}) {
  const found = useMemo(() => candidates(env, services), [env, services])
  const [open, setOpen] = useState(false)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  if (found.length === 0) return null
  const key = (m: SecretMove) => (m.service ?? '.env') + '/' + m.name
  const chosen = found.filter(m => picked.has(key(m)))

  return (
    <div className="card secrets-hint">
      <span>⚠ {found.length} value{found.length === 1 ? '' : 's'} that look{found.length === 1 ? 's' : ''} like a password {found.length === 1 ? 'is' : 'are'} readable by anyone who opens this project, and kept in every deploy's history.</span>
      <button type="button" className="sm primary" disabled={running} onClick={() => { setPicked(new Set(found.map(key))); setOpen(true) }}>Move to Secrets…</button>
      {open && (
        <Modal label="Move passwords to Secrets" wide onClose={() => setOpen(false)} onSubmit={() => {
          if (chosen.length === 0 || busy) return
          setBusy(true)
          void onMove(chosen).then(ok => { setBusy(false); if (ok) setOpen(false) })
        }}>
          <p className="mini">Stored encrypted and never shown again. Compose fills them in as <code>${'{'}NAME{'}'}</code> like the .env did, so the containers get the same values; nothing restarts now. Untick what is not a secret.</p>
          <div className="table-wrap"><table>
            <thead><tr><th></th><th>Where</th><th>Setting</th><th>Secret</th><th>Value</th></tr></thead>
            <tbody>
              {found.map(m => (
                <tr key={key(m)}>
                  <td><input type="checkbox" aria-label={`move ${m.name}`} checked={picked.has(key(m))}
                    onChange={e => setPicked(p => { const n = new Set(p); if (e.target.checked) n.add(key(m)); else n.delete(key(m)); return n })} /></td>
                  <td className="muted nowrap">{m.service ?? '.env'}</td>
                  <td><code>{m.name}</code></td>
                  <td><code>{m.secret}</code>{secretNames.includes(m.secret) && <span className="mini warn-text"> replaces the stored one</span>}</td>
                  <td className="muted nowrap">{m.value.slice(0, 3)}••••••</td>
                </tr>
              ))}
            </tbody>
          </table></div>
          <p className="mini">Needs the agents of this Dockmaster version: an older agent refuses the deploy rather than start a service without its password. The history of past deploys still holds the old .env.</p>
          <div className="toolbar form-actions flush">
            <button className="primary" type="submit" disabled={busy || chosen.length === 0}>{busy ? 'moving…' : `Move ${chosen.length}`}</button>
            <button type="button" onClick={() => setOpen(false)}>Cancel</button>
          </div>
        </Modal>
      )}
    </div>
  )
}
