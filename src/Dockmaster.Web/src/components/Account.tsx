import { useState, type FormEvent } from 'react'
import { api, errorText } from '../lib/api'
import { useMe } from '../lib/access'

/** The signed-in user on the Settings page, for everyone: their own password (the current one asked first) and signing out. */
export default function Account() {
  const me = useMe()
  const [busy, setBusy] = useState(false)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  if (!me) return null

  async function changePassword(e: FormEvent) {
    e.preventDefault()
    // a second Enter while the first is on its way would be told the current password is wrong
    if (busy) return
    setBusy(true)
    setMsg(null)
    try {
      await api('/auth/password', { method: 'POST', json: { current, password: next } })
      setCurrent(''); setNext('')
      setMsg({ ok: true, text: 'password changed — your other sessions are signed out' })
    } catch (err) { setMsg({ ok: false, text: errorText(err) }) }
    finally { setBusy(false) }
  }

  // a new page, not a route: the live connection and everything read for this user go with it, the next one to
  // sign in in this tab starts clean
  async function signOut() {
    try { await api('/auth/logout', { method: 'POST' }) } catch { /* signed out or not, the login page is next */ }
    window.location.assign('/login')
  }

  return (
    <form className="card form" onSubmit={e => { void changePassword(e) }}>
      <h2>Account</h2>
      <p className="mini">Signed in as <strong>{me.name}</strong>. {me.isAdmin ? 'Admin: every server, users, settings.' : 'Your access is set by an admin.'}</p>
      {/* the name, so a password manager files the new password under it */}
      <input type="text" value={me.name} autoComplete="username" readOnly hidden />
      <div className="row">
        <div>
          <label htmlFor="acc-current">Current password</label>
          <input id="acc-current" type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" />
        </div>
        <div>
          <label htmlFor="acc-next">New password (8+ characters)</label>
          <input id="acc-next" type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" />
        </div>
      </div>
      <div className="toolbar form-actions">
        <button className="primary" type="submit" disabled={busy || !current || next.length < 8}>Change password</button>
        {msg && <span className={msg.ok ? 'success' : 'error'}>{msg.text}</span>}
        <button type="button" className="push-right" onClick={() => { void signOut() }}>Sign out</button>
      </div>
    </form>
  )
}
