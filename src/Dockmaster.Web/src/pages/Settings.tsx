import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText, type ApiKey, type NewApiKeyResponse, type NotificationSettings, type Settings as SettingsDto, type Template } from '../lib/api'
import { Ago } from '../components/Status'
import SettingsBackup from './SettingsBackup'
import Account from '../components/Account'
import Masonry from '../components/Masonry'
import { useMe } from '../lib/access'

/** One's own account for everyone; for an admin also the servers, notifications, API keys and backups. */
export default function Settings() {
  const admin = useMe()?.isAdmin ?? false
  return (
    <>
      <div className="toolbar">
        <h1>Settings</h1>
      </div>
      {/* side by side where the screen is wide enough */}
      <Masonry className="settings-grid">
        <Account />
        {admin && (
          <>
            <ServerSettings />
            <Notifications />
            <ApiKeys />
            <SettingsBackup />
          </>
        )}
      </Masonry>
    </>
  )
}

/** Where agents connect to and which image they run, the mesh's address range. */
function ServerSettings() {
  const [s, setS] = useState<SettingsDto | null>(null)
  const [url, setUrl] = useState('')
  const [image, setImage] = useState('')
  const [subnet, setSubnet] = useState('')
  const [port, setPort] = useState('')
  const [mtu, setMtu] = useState('')
  const [tuning, setTuning] = useState(false)
  const [msg1, setMsg1] = useState<{ ok: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    api<SettingsDto>('/settings').then(d => {
      setS(d); setUrl(d.agentPublicUrl); setImage(d.agentImage); setSubnet(d.meshSubnet); setPort(String(d.meshPort))
      setMtu(d.meshMtu ? String(d.meshMtu) : ''); setTuning(d.meshTcpTuning)
    }).catch(e => setLoadError(errorText(e)))
  }, [])

  async function saveAgents(e: FormEvent) {
    e.preventDefault()
    setMsg1(null)
    try {
      await api('/settings', { method: 'PUT', json: { agentPublicUrl: url, agentImage: image, meshSubnet: subnet, meshPort: Number(port) || 0, meshMtu: Number(mtu) || 0, meshTcpTuning: tuning } })
      setMsg1({ ok: true, text: 'saved' })
    } catch (err) { setMsg1({ ok: false, text: errorText(err) }) }
  }

  if (!s) return loadError ? <p className="error">{loadError}</p> : null
  return (
    <form className="card form" onSubmit={saveAgents}>
      <h2>Servers</h2>
      <label htmlFor="s-url">Dockmaster URL agents connect to (https://host:agent-port)</label>
      <input id="s-url" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://nas.example.com:9443" />
      <label htmlFor="s-image">Agent image (used in the docker run snippet)</label>
      <input id="s-image" value={image} onChange={e => setImage(e.target.value)} placeholder="registry.example.com/dockmaster/dockmaster-agent:latest" />
      <div className="field-label" id="s-fingerprint">Agent TLS certificate fingerprint (SHA-256, agents pin this)</div>
      <pre className="cmd" aria-labelledby="s-fingerprint">{s.fingerprintPretty}</pre>

      <h2>Network between servers</h2>
      <div className="row">
        <div>
          <label htmlFor="s-subnet">Mesh subnet (each server gets one address)</label>
          <input id="s-subnet" value={subnet} onChange={e => setSubnet(e.target.value)} placeholder="10.77.0.0/16" disabled={s.meshInUse} />
        </div>
        <div className="narrow">
          <label htmlFor="s-port">UDP port</label>
          <input id="s-port" value={port} inputMode="numeric" onChange={e => setPort(e.target.value.replace(/\D/g, ''))} placeholder="51821" disabled={s.meshInUse} />
        </div>
      </div>
      <p className="mini">{s.meshInUse
        ? 'Servers are in the mesh: the subnet and port cannot change until every server has left it.'
        : 'Pick a range no server uses already; the port is deliberately not 51820, which a WireGuard already on a host would hold.'}</p>
      <div className="row">
        <div className="narrow">
          <label htmlFor="s-mtu">Tunnel MTU</label>
          <input id="s-mtu" value={mtu} inputMode="numeric" onChange={e => setMtu(e.target.value.replace(/\D/g, ''))} placeholder="auto" />
        </div>
        <div>
          <div className="field-label">TCP on the servers</div>
          <label className="check" htmlFor="s-tuning"><input id="s-tuning" type="checkbox" checked={tuning} onChange={e => setTuning(e.target.checked)} /> BBR and bigger buffers (faster over long, lossy paths; host-wide)</label>
        </div>
      </div>
      <p className="mini">Empty MTU: each server gets the one its measured paths carry (Network page → Tune speed), 1420 until measured. A number (1280–1440) is used on every server instead.
        TCP tuning applies to the hosts at once; a container keeps the TCP settings it started with until it is re-created (its next deploy).</p>
      <div className="toolbar"><button className="primary" type="submit">Save</button>{msg1 && <span className={msg1.ok ? 'success' : 'error'}>{msg1.text}</span>}</div>
    </form>
  )
}

/**
 * Where events go besides the Events page: a Seq server (its alerts send e-mail through its own SMTP). The API key is
 * write-only: an empty field keeps the stored one.
 */
function Notifications() {
  const [n, setN] = useState<NotificationSettings | null>(null)
  const [url, setUrl] = useState('')
  const [key, setKey] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    // what was typed while it loaded stays
    api<NotificationSettings>('/settings/notifications').then(d => { setN(d); setUrl(u => u || d.seqUrl) }).catch(e => setMsg({ ok: false, text: errorText(e) }))
  }, [])

  async function save(e: FormEvent) {
    e.preventDefault()
    setMsg(null)
    try {
      const typed = key.trim()
      await api('/settings/notifications', { method: 'PUT', json: { seqUrl: url, seqApiKey: typed || null } })
      setN({ seqUrl: url.trim().replace(/\/+$/, ''), seqApiKeySet: n?.seqApiKeySet || typed.length > 0 }); setKey('')
      setMsg({ ok: true, text: url ? 'saved — events go to Seq from now on' : 'saved — events are not sent anywhere' })
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function removeKey() {
    setMsg(null)
    try {
      // the stored address, not an unsaved one in the field
      await api('/settings/notifications', { method: 'PUT', json: { seqUrl: n?.seqUrl ?? '', seqApiKey: '' } })
      setN({ seqUrl: n?.seqUrl ?? '', seqApiKeySet: false }); setMsg({ ok: true, text: 'API key removed' })
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function test() {
    setMsg(null); setBusy(true)
    try { setMsg({ ok: true, text: (await api<{ message: string }>('/settings/notifications/test', { method: 'POST', json: { seqUrl: url, seqApiKey: key.trim() || null } })).message }) }
    catch (err) { setMsg({ ok: false, text: errorText(err) }) }
    finally { setBusy(false) }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2>Notifications</h2>
      <p className="mini">
        Failed deploys, servers gone, containers restarting or unhealthy, hosts short of memory or disk, services at their limits, memory that
        only grows, and what the autoscaler does or would do are listed under Events. Set a Seq server to get them there as well; an alert in
        Seq on <code>Application = 'Dockmaster' and @Level in ['Warning', 'Error']</code> sends them by e-mail through its SMTP.
      </p>
      <div className="row">
        <div>
          <label htmlFor="s-seq">Seq address (empty: send nowhere)</label>
          <input id="s-seq" value={url} onChange={e => setUrl(e.target.value)} placeholder="http://seq.lan:5341" />
        </div>
        <div>
          <label htmlFor="s-seqkey">API key{n?.seqApiKeySet ? ' (stored — type to replace; a test to another address needs it typed)' : ' (optional)'}</label>
          <input id="s-seqkey" type="password" value={key} onChange={e => setKey(e.target.value)} autoComplete="off" placeholder={n?.seqApiKeySet ? '••••••••' : ''} />
        </div>
      </div>
      <div className="toolbar">
        <button className="primary" type="submit">Save</button>
        <button type="button" disabled={busy || !url} onClick={() => { void test() }}>Send a test event</button>
        {n?.seqApiKeySet && <button type="button" onClick={() => { void removeKey() }}>Remove the API key</button>}
        {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
      </div>
    </form>
  )
}

/**
 * Keys a program manages instances of templates with (and nothing else). A key is shown once, when it is made: only its
 * hash is kept, so a lost key is replaced, not looked up.
 */
function ApiKeys() {
  const [keys, setKeys] = useState<ApiKey[] | null>(null)
  const [templates, setTemplates] = useState<Template[]>([])
  const [name, setName] = useState('')
  const [picked, setPicked] = useState<number[]>([])
  const [made, setMade] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = () => {
    api<ApiKey[]>('/apikeys').then(setKeys).catch(e => setMsg({ ok: false, text: errorText(e) }))
    api<Template[]>('/templates').then(setTemplates).catch(() => {})
  }
  useEffect(load, [])

  async function create(e: FormEvent) {
    e.preventDefault()
    setMsg(null); setMade(null)
    try {
      const r = await api<NewApiKeyResponse>('/apikeys', { method: 'POST', json: { name, templateIds: picked } })
      setMade(r.key); setName(''); setPicked([]); load()
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  async function remove(k: ApiKey) {
    if (!confirm(`Revoke the key "${k.name}"? Whatever uses it stops working at once.`)) return
    try { await api(`/apikeys/${k.id}`, { method: 'DELETE' }); load() }
    catch (err) { setMsg({ ok: false, text: errorText(err) }) }
  }

  return (
    <form className="card form" onSubmit={create}>
      <h2>API keys</h2>
      <p className="mini">
        A key lets a program create, start, stop and delete instances of the templates it lists — through <code>/api/templates/&lt;name&gt;/instances</code>{' '}
        with <code>Authorization: Bearer dmk_…</code> — and nothing else. Templates are on the Templates page.
      </p>
      {keys && keys.length > 0 && (
        <div className="table-wrap"><table>
          <thead><tr><th>Name</th><th>Templates</th><th className="hide-sm">Last used</th><th></th></tr></thead>
          <tbody>
            {keys.map(k => (
              <tr key={k.id}>
                <td className="nowrap">{k.name} <span className="muted mini">…{k.hint}</span></td>
                <td className="muted">{k.templates.join(', ') || '—'}</td>
                <td className="hide-sm"><Ago iso={k.lastUsedAt} /></td>
                <td><button type="button" className="sm danger" onClick={() => { void remove(k) }}>revoke</button></td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      {made && (
        <div className="secrets-hint card">
          <span>The new key — copy it now, it is not shown again:</span>
          <code className="selectable">{made}</code>
          <button type="button" className="sm" onClick={() => { void navigator.clipboard?.writeText(made) }}>copy</button>
        </div>
      )}
      {templates.length === 0 ? <p className="muted mini">Make a template first: a key is always limited to some templates.</p> : (
        <>
          <label htmlFor="k-name">New key for</label>
          <input id="k-name" value={name} onChange={e => setName(e.target.value)} placeholder="game backend" />
          <div className="field-label">May manage instances of</div>
          <div className="check-list">
            {templates.map(t => (
              <label key={t.id} className="check-inline">
                <input type="checkbox" checked={picked.includes(t.id)} onChange={e => setPicked(e.target.checked ? [...picked, t.id] : picked.filter(id => id !== t.id))} /> {t.name}
              </label>
            ))}
          </div>
          <div className="toolbar">
            <button className="primary" type="submit" disabled={!name.trim() || picked.length === 0}>Create key</button>
            {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
          </div>
        </>
      )}
    </form>
  )
}
